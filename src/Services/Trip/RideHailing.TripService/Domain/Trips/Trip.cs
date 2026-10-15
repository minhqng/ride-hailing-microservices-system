// Trip aggregate root: authoritative local ride lifecycle (Doc 01 §6).
//
// The aggregate guards DSI-008 (at most one current assignment), DSI-009
// (assignment identity coherence), DSI-010 (terminal monotonicity), DSI-011
// (start requires current assignment), DSI-012 (completion only from
// IN_PROGRESS) and DSI-016 (stale-round fencing via CurrentMatchingRound).
// It records only the Trip-side reference to authoritative Driver/Payment
// results; reservation and payment authority stay with the Driver and Payment
// services. Saga, Outbox, broker and rematch orchestration belong to later
// work; compensation NEEDS are exposed as TripCompensation data, never
// executed here.
//
// Identity/fence precedence inside every intake method:
//   terminal state  → TerminalState (DSI-010/DSI-032; no reactivation, no
//                     replay-success on terminal rows)
//   malformed input → AssignmentRequired (empty identities; no mutation)
//   stale generation→ StaleIdentity (round/reservation not current; no
//                     mutation). On assigned rows a non-current round reports
//                     stale; on unassigned rows a non-current round also
//                     reports stale. CREATED (no round exists yet) reports
//                     IllegalTransition instead: the primary defect is lifecycle
//                     state, not generation.
//   conflicting fact→ AssignmentIdentityMismatch / AssignmentAlreadyPresent
//   illegal edge     → IllegalTransition
// Replay (exact same legal fact already current) is Success with IsReplay and
// performs no mutation: no State/assignment/round/timestamp change and no
// Version advance.

namespace RideHailing.TripService.Domain.Trips;

/// <summary>
/// Authoritative local Trip lifecycle: state, current matching generation,
/// current assignment reference, frozen fare and a mechanical concurrency
/// version. All mutations go through guarded transition methods; rejected and
/// replay operations leave the aggregate untouched (no partial state,
/// assignment, round, timestamp or version change). The domain performs no
/// clock access: every mutating method takes an explicit timestamp.
/// </summary>
public sealed class Trip
{
    private readonly List<TripCleanupIntent> _cleanupIntents = [];
    public IReadOnlyCollection<TripCleanupIntent> CleanupIntents => _cleanupIntents.AsReadOnly();

    /// <summary>One logical ride; stable for the ride lifetime. Immutable.</summary>
    public Guid TripId { get; private set; }

    /// <summary>Requesting rider. Immutable.</summary>
    public Guid RiderId { get; private set; }

    /// <summary>Requested pickup. Immutable.</summary>
    public TripLocation Pickup { get; private set; }

    /// <summary>Requested dropoff. Immutable.</summary>
    public TripLocation Dropoff { get; private set; }

    /// <summary>Frozen authoritative fare (DSI-014). Immutable.</summary>
    public FareSnapshot Fare { get; private set; }

    /// <summary>Current P0 business state.</summary>
    public TripState State { get; private set; }

    /// <summary>
    /// Current matching generation (DSI-016). Zero means no round has been
    /// opened yet; StartMatching advances it to 1. Only the current round may
    /// become (or remain) the assignment. Later rematch rounds belong to
    /// future workflow work.
    /// </summary>
    public int CurrentMatchingRound { get; private set; }

    /// <summary>Zero or one coherent current assignment (DSI-008).</summary>
    public TripAssignment? Assignment { get; private set; }

    /// <summary>When the Trip was created.</summary>
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>
    /// When the last committed local transition applied. Advanced only by
    /// applied transitions; rejected and replay operations never touch it.
    /// </summary>
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// Mechanical concurrency version, advanced once per committed local
    /// transition. Rejected and replay operations never advance it. The
    /// initial value is an implementation detail, not a business rule.
    /// </summary>
    public int Version { get; private set; }

    // Persistence materialization path (A-W03-07): EF Core 10 cannot bind
    // ComplexProperty references through the domain constructor, so the
    // model uses this private parameterless constructor plus private setters.
    // Public immutability is preserved; all business construction still goes
    // through Create/Reconstitute with structural validation.
    private Trip()
    {
        Pickup = null!;
        Dropoff = null!;
        Fare = null!;
    }

    private Trip(
        Guid tripId,
        Guid riderId,
        TripLocation pickup,
        TripLocation dropoff,
        FareSnapshot fare,
        TripState state,
        int currentMatchingRound,
        TripAssignment? assignment,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        int version)
    {
        TripId = tripId;
        RiderId = riderId;
        Pickup = pickup;
        Dropoff = dropoff;
        Fare = fare;
        State = state;
        CurrentMatchingRound = currentMatchingRound;
        Assignment = assignment;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
        Version = version;
    }

    /// <summary>
    /// Constructs a valid new Trip-domain object in CREATED state with no
    /// assignment and no open matching round. Takes an already-created
    /// <see cref="FareSnapshot"/> and the single application-supplied
    /// creation timestamp; creation performs no idempotency, active-trip or
    /// fare-calculation logic, which belong to later application work. The
    /// domain performs no clock access.
    /// </summary>
    public static Trip Create(
        Guid tripId,
        Guid riderId,
        TripLocation pickup,
        TripLocation dropoff,
        FareSnapshot fare,
        DateTimeOffset createdAt)
    {
        if (tripId == Guid.Empty)
        {
            throw new ArgumentException("Trip requires a non-empty identity.", nameof(tripId));
        }

        if (riderId == Guid.Empty)
        {
            throw new ArgumentException("Trip requires a non-empty rider identity.", nameof(riderId));
        }

        ArgumentNullException.ThrowIfNull(pickup);
        ArgumentNullException.ThrowIfNull(dropoff);
        ArgumentNullException.ThrowIfNull(fare);

        if (createdAt == default)
        {
            throw new ArgumentException("Trip requires a concrete creation timestamp.", nameof(createdAt));
        }

        return new Trip(tripId, riderId, pickup, dropoff, fare, TripState.Created, 0, null, createdAt, createdAt, 0);
    }

    /// <summary>
    /// Reconstitutes persisted historical state. Enforces structural
    /// coherence: DRIVER_RESERVED, DRIVER_ACCEPTED, READY_TO_START,
    /// IN_PROGRESS and COMPLETED require a coherent current assignment
    /// because each is reachable only with one (DSI-009, DSI-011, DSI-012).
    /// CREATED requires round zero with no assignment; MATCH_FAILED requires
    /// no assignment; CANCELLED allows either form so pre-assignment
    /// cancellation and post-assignment cleanup identity both survive. Any
    /// present assignment must agree with the current round (DSI-009).
    /// MATCHING requires an open round (>= 1) and normally carries no
    /// assignment; a coherent MATCHING + assignment shape may still be
    /// constructed in memory to prove generation-before-conflict fencing
    /// (DSI-016 before DSI-008). That shape is never persistable: the
    /// state/assignment database checks reject MATCHING rows with an
    /// assignment and remain the persisted-state backstop.
    /// </summary>
    internal static Trip Reconstitute(
        Guid tripId,
        Guid riderId,
        TripLocation pickup,
        TripLocation dropoff,
        FareSnapshot fare,
        TripState state,
        int currentMatchingRound,
        TripAssignment? assignment,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        int version)
    {
        if (tripId == Guid.Empty)
        {
            throw new ArgumentException("Trip requires a non-empty identity.", nameof(tripId));
        }

        if (riderId == Guid.Empty)
        {
            throw new ArgumentException("Trip requires a non-empty rider identity.", nameof(riderId));
        }

        ArgumentNullException.ThrowIfNull(pickup);
        ArgumentNullException.ThrowIfNull(dropoff);
        ArgumentNullException.ThrowIfNull(fare);

        if (version < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(version), version, "Reconstituted version must not be negative.");
        }

        if (currentMatchingRound < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(currentMatchingRound), currentMatchingRound, "Reconstituted matching round must not be negative.");
        }

        if (createdAt == default)
        {
            throw new ArgumentException("Trip requires a concrete creation timestamp.", nameof(createdAt));
        }

        if (updatedAt == default)
        {
            throw new ArgumentException("Trip requires a concrete update timestamp.", nameof(updatedAt));
        }

        if (RequiresAssignment(state) && assignment is null)
        {
            throw new ArgumentException(
                $"Cannot reconstitute {state.ToCanonicalName()} without a current assignment.",
                nameof(assignment));
        }

        if (state == TripState.Created && (currentMatchingRound != 0 || assignment is not null))
        {
            throw new ArgumentException(
                "Cannot reconstitute CREATED with an open matching round or a current assignment.",
                nameof(currentMatchingRound));
        }

        if (state == TripState.Matching && currentMatchingRound < 1)
        {
            throw new ArgumentException(
                "Cannot reconstitute MATCHING without an open matching round.",
                nameof(currentMatchingRound));
        }

        if (state == TripState.MatchFailed && assignment is not null)
        {
            throw new ArgumentException(
                "Cannot reconstitute MATCH_FAILED with a current assignment.",
                nameof(assignment));
        }

        if (assignment is not null && (currentMatchingRound < 1 || assignment.MatchingRound != currentMatchingRound))
        {
            throw new ArgumentException(
                "Cannot reconstitute a current assignment that disagrees with the current matching round (DSI-009).",
                nameof(assignment));
        }

        return new Trip(tripId, riderId, pickup, dropoff, fare, state, currentMatchingRound, assignment, createdAt, updatedAt, version);
    }

    /// <summary>
    /// Opens the first matching round: CREATED → MATCHING with the current
    /// round initialized to 1 (A-W05-01, A-W05-02). Repeating the call on a
    /// round-1 MATCHING Trip is an idempotent replay. Later rematch rounds
    /// belong to future workflow work and are rejected here.
    /// </summary>
    public TripOutcome StartMatching(DateTimeOffset now)
    {
        if (State.IsTerminal())
        {
            return Terminal(nameof(StartMatching));
        }

        if (now == default)
        {
            throw new ArgumentException("StartMatching requires a concrete timestamp.", nameof(now));
        }

        if (State == TripState.Matching)
        {
            if (CurrentMatchingRound == 1)
            {
                return TripOutcome.Success(isReplay: true);
            }

            return TripOutcome.Fail(
                TripErrorCode.IllegalTransition,
                "StartMatching cannot open a further matching round from MATCHING; rematch belongs to future workflow.");
        }

        if (State != TripState.Created)
        {
            return Illegal(nameof(StartMatching), TripState.Created);
        }

        if (CurrentMatchingRound != 0)
        {
            throw new InvalidOperationException(
                $"Trip {TripId:D} is CREATED with matching round {CurrentMatchingRound}; persisted state violates Trip invariants.");
        }

        CurrentMatchingRound = 1;
        Commit(TripState.Matching, now);
        return TripOutcome.Success();
    }

    /// <summary>
    /// Applies the local consequence of a legal Driver reservation result:
    /// MATCHING → DRIVER_RESERVED with one coherent assignment. The caller
    /// supplies authoritative facts; this method never contacts the Driver
    /// service and never trusts a stale generation: only the current round
    /// may become the assignment (DSI-016), and only when no assignment
    /// exists yet (DSI-008). A duplicate of the exact current assignment on
    /// an assigned Trip is an idempotent replay; a non-current round is
    /// stale; a different identity for the current round is a conflict.
    /// </summary>
    public TripOutcome ApplyDriverReserved(Guid driverId, Guid reservationId, int matchingRound, DateTimeOffset now)
    {
        if (State.IsTerminal())
        {
            return Terminal(nameof(ApplyDriverReserved));
        }

        if (driverId == Guid.Empty || reservationId == Guid.Empty)
        {
            return TripOutcome.Fail(
                TripErrorCode.AssignmentRequired,
                "Applying a Driver reservation requires non-empty driver and reservation identities.");
        }

        if (now == default)
        {
            throw new ArgumentException("ApplyDriverReserved requires a concrete timestamp.", nameof(now));
        }

        if (State == TripState.Matching)
        {
            // Generation gate before same-generation conflict (DSI-016
            // before DSI-008): a fact from a superseded generation is stale
            // regardless of any assignment the Trip holds; only a current
            // generation fact can conflict as a second assignment.
            if (matchingRound != CurrentMatchingRound)
            {
                return StaleRound(nameof(ApplyDriverReserved), matchingRound);
            }

            if (Assignment is not null)
            {
                return TripOutcome.Fail(
                    TripErrorCode.AssignmentAlreadyPresent,
                    "Trip already holds a current assignment; a second simultaneous assignment is rejected (DSI-008).");
            }

            Assignment = new TripAssignment(driverId, reservationId, matchingRound);
            Commit(TripState.DriverReserved, now);
            return TripOutcome.Success();
        }

        if (IsAssignedState(State))
        {
            if (Assignment is null)
            {
                throw new InvalidOperationException(
                    $"Trip {TripId:D} is {State.ToCanonicalName()} without a current assignment; persisted state violates Trip invariants.");
            }

            if (Assignment.DriverId == driverId
                && Assignment.ReservationId == reservationId
                && Assignment.MatchingRound == matchingRound
                && matchingRound == CurrentMatchingRound)
            {
                return TripOutcome.Success(isReplay: true);
            }

            if (matchingRound != CurrentMatchingRound)
            {
                return StaleRound(nameof(ApplyDriverReserved), matchingRound);
            }

            return TripOutcome.Fail(
                TripErrorCode.AssignmentIdentityMismatch,
                "Supplied driver/reservation identity does not match the current assignment.");
        }

        return Illegal(nameof(ApplyDriverReserved), TripState.Matching);
    }

    /// <summary>
    /// Records the deterministic local part of Driver acceptance:
    /// DRIVER_RESERVED → DRIVER_ACCEPTED. The caller must identify the
    /// current assignment; mismatched identities are rejected. Repeating the
    /// exact current acceptance on DRIVER_ACCEPTED (or as a late duplicate on
    /// READY_TO_START / IN_PROGRESS, where the acceptance fact is still
    /// current) is an idempotent replay with no mutation.
    /// </summary>
    public TripOutcome RecordDriverAccepted(Guid driverId, Guid reservationId, DateTimeOffset now)
    {
        if (State.IsTerminal())
        {
            return Terminal(nameof(RecordDriverAccepted));
        }

        if (driverId == Guid.Empty || reservationId == Guid.Empty)
        {
            return TripOutcome.Fail(
                TripErrorCode.AssignmentRequired,
                "Driver acceptance requires non-empty driver and reservation identities.");
        }

        if (now == default)
        {
            throw new ArgumentException("RecordDriverAccepted requires a concrete timestamp.", nameof(now));
        }

        if (State is TripState.DriverReserved
            or TripState.DriverAccepted
            or TripState.ReadyToStart
            or TripState.InProgress)
        {
            if (Assignment is null)
            {
                throw new InvalidOperationException(
                    $"Trip {TripId:D} is {State.ToCanonicalName()} without a current assignment; persisted state violates Trip invariants.");
            }

            if (Assignment.DriverId != driverId || Assignment.ReservationId != reservationId)
            {
                return TripOutcome.Fail(
                    TripErrorCode.AssignmentIdentityMismatch,
                    "Supplied driver/reservation identity does not match the current assignment.");
            }

            if (State == TripState.DriverReserved)
            {
                Commit(TripState.DriverAccepted, now);
                return TripOutcome.Success();
            }

            return TripOutcome.Success(isReplay: true);
        }

        return Illegal(nameof(RecordDriverAccepted), TripState.DriverReserved);
    }

    /// <summary>
    /// Validates a Driver rejection against the current assignment without
    /// changing Trip state (A-W05-03 accept/reject proof). A rejection for
    /// the exact current reservation is accepted and exposes the guarded
    /// cleanup requirement (exact release + matching cancellation for future
    /// workflow); the assignment is deliberately NOT cleared and the round is
    /// NOT incremented here, because only Driver authority can confirm the
    /// guarded release that a future rematch operation will fence on. A
    /// rejection for any other identity is stale/conflicting and changes
    /// nothing.
    /// This operation validates the current cleanup requirement; it does not
    /// record that compensation has been dispatched. Trip persists no
    /// rejection memory, so every identical validation succeeds alike without
    /// advancing Version: repeated Success means "the requirement still
    /// holds", never "a new compensation was emitted".
    /// </summary>
    public TripOutcome RecordDriverRejected(Guid driverId, Guid reservationId)
    {
        if (State.IsTerminal())
        {
            return Terminal(nameof(RecordDriverRejected));
        }

        if (driverId == Guid.Empty || reservationId == Guid.Empty)
        {
            return TripOutcome.Fail(
                TripErrorCode.AssignmentRequired,
                "Driver rejection requires non-empty driver and reservation identities.");
        }

        if (State != TripState.DriverReserved)
        {
            return Illegal(nameof(RecordDriverRejected), TripState.DriverReserved);
        }

        if (Assignment is null)
        {
            throw new InvalidOperationException(
                $"Trip {TripId:D} is DRIVER_RESERVED without a current assignment; persisted state violates Trip invariants.");
        }

        if (Assignment.DriverId != driverId || Assignment.ReservationId != reservationId)
        {
            return TripOutcome.Fail(
                TripErrorCode.AssignmentIdentityMismatch,
                "Supplied driver/reservation identity does not match the current assignment.");
        }

        return TripOutcome.Success(
            compensation: TripCompensation.ReleaseReservation | TripCompensation.CancelMatching);
    }

    /// <summary>
    /// Records the local consequence of authoritative Payment authorization:
    /// DRIVER_ACCEPTED → READY_TO_START. Payment truth itself is supplied by
    /// the future workflow layer, never decided here. Repeating the call on
    /// READY_TO_START is an idempotent replay.
    /// </summary>
    public TripOutcome MarkPaymentAuthorized(DateTimeOffset now)
    {
        if (State.IsTerminal())
        {
            return Terminal(nameof(MarkPaymentAuthorized));
        }

        if (now == default)
        {
            throw new ArgumentException("MarkPaymentAuthorized requires a concrete timestamp.", nameof(now));
        }

        if (State == TripState.ReadyToStart)
        {
            return TripOutcome.Success(isReplay: true);
        }

        if (State != TripState.DriverAccepted)
        {
            return Illegal(nameof(MarkPaymentAuthorized), TripState.DriverAccepted);
        }

        if (Assignment is null)
        {
            throw new InvalidOperationException(
                $"Trip {TripId:D} is DRIVER_ACCEPTED without a current assignment; persisted state violates Trip invariants.");
        }

        Commit(TripState.ReadyToStart, now);
        return TripOutcome.Success();
    }

    /// <summary>
    /// Records the Trip-state consequence of terminal Payment failure:
    /// DRIVER_ACCEPTED → CANCELLED. The assignment is preserved so later
    /// workflow work keeps the exact cleanup identity, and the outcome
    /// exposes the exact-release requirement. No Driver release, void or
    /// message is executed here.
    /// </summary>
    public TripOutcome MarkPaymentFailed(DateTimeOffset now)
    {
        if (State.IsTerminal())
        {
            return Terminal(nameof(MarkPaymentFailed));
        }

        if (now == default)
        {
            throw new ArgumentException("MarkPaymentFailed requires a concrete timestamp.", nameof(now));
        }

        if (State != TripState.DriverAccepted)
        {
            return Illegal(nameof(MarkPaymentFailed), TripState.DriverAccepted);
        }

        if (Assignment is null)
        {
            throw new InvalidOperationException(
                $"Trip {TripId:D} is DRIVER_ACCEPTED without a current assignment; persisted state violates Trip invariants.");
        }

        AddCancellationCleanup(now);
        Commit(TripState.Cancelled, now);
        return TripOutcome.Success(
            compensation: TripCompensation.ReleaseReservation | TripCompensation.CancelMatching);
    }

    /// <summary>
    /// Records the locally deterministic matching failure:
    /// MATCHING → MATCH_FAILED for the current round (A-W05-04). A stale
    /// round never fails the Trip, and an existing current assignment
    /// contradicts a clean matching failure (DSI-008).
    /// </summary>
    public TripOutcome MarkMatchingFailed(int matchingRound, DateTimeOffset now)
    {
        if (State.IsTerminal())
        {
            return Terminal(nameof(MarkMatchingFailed));
        }

        if (now == default)
        {
            throw new ArgumentException("MarkMatchingFailed requires a concrete timestamp.", nameof(now));
        }

        if (State != TripState.Matching)
        {
            return Illegal(nameof(MarkMatchingFailed), TripState.Matching);
        }

        if (Assignment is not null)
        {
            // Generation gate before same-generation conflict, mirroring
            // ApplyDriverReserved: a stale generation cannot fail the Trip
            // even when an assignment contradiction is also present.
            if (matchingRound != CurrentMatchingRound)
            {
                return StaleRound(nameof(MarkMatchingFailed), matchingRound);
            }

            return TripOutcome.Fail(
                TripErrorCode.AssignmentAlreadyPresent,
                "Matching cannot fail cleanly while the Trip holds a current assignment.");
        }

        if (matchingRound != CurrentMatchingRound)
        {
            return StaleRound(nameof(MarkMatchingFailed), matchingRound);
        }

        Commit(TripState.MatchFailed, now);
        return TripOutcome.Success();
    }

    /// <summary>
    /// Starts the ride: READY_TO_START → IN_PROGRESS (DSI-011). Requires the
    /// current assignment to exist with matching identity. READY_TO_START
    /// itself is the local fact that authorization was previously accepted;
    /// no remote service is consulted. Repeating the exact current start on
    /// IN_PROGRESS is an idempotent replay. Starting before the required
    /// authorization/state is rejected with no mutation (DSI-026).
    /// </summary>
    public TripOutcome Start(Guid driverId, Guid reservationId, DateTimeOffset now)
    {
        if (State.IsTerminal())
        {
            return Terminal(nameof(Start));
        }

        if (driverId == Guid.Empty || reservationId == Guid.Empty)
        {
            return TripOutcome.Fail(
                TripErrorCode.AssignmentRequired,
                "Starting the ride requires non-empty driver and reservation identities.");
        }

        if (now == default)
        {
            throw new ArgumentException("Start requires a concrete timestamp.", nameof(now));
        }

        if (State is TripState.ReadyToStart or TripState.InProgress)
        {
            var identity = CheckCurrentAssignment(driverId, reservationId, nameof(Start));
            if (identity is not null)
            {
                return identity;
            }

            if (State == TripState.ReadyToStart)
            {
                Commit(TripState.InProgress, now);
                return TripOutcome.Success();
            }

            return TripOutcome.Success(isReplay: true);
        }

        return Illegal(nameof(Start), TripState.ReadyToStart);
    }

    /// <summary>
    /// Completes the ride: IN_PROGRESS → COMPLETED (DSI-012) for the current
    /// Trip/Driver relationship. Afterwards the Trip is terminal. No Payment
    /// capture is performed here.
    /// </summary>
    public TripOutcome Complete(Guid driverId, Guid reservationId, DateTimeOffset now)
    {
        if (State.IsTerminal())
        {
            return Terminal(nameof(Complete));
        }

        if (driverId == Guid.Empty || reservationId == Guid.Empty)
        {
            return TripOutcome.Fail(
                TripErrorCode.AssignmentRequired,
                "Completing the ride requires non-empty driver and reservation identities.");
        }

        if (now == default)
        {
            throw new ArgumentException("Complete requires a concrete timestamp.", nameof(now));
        }

        if (State != TripState.InProgress)
        {
            return Illegal(nameof(Complete), TripState.InProgress);
        }

        var identity = CheckCurrentAssignment(driverId, reservationId, nameof(Complete));
        if (identity is not null)
        {
            return identity;
        }

        Commit(TripState.Completed, now);
        return TripOutcome.Success();
    }

    /// <summary>
    /// Cancels the Trip from a pre-start state → CANCELLED. Any present
    /// assignment is preserved for exact later cleanup (DSI-033) and the
    /// outcome exposes the cleanup requirement (exact release when an
    /// assignment exists; matching cancellation when a round was opened).
    /// Cancellation from CREATED or READY_TO_START depends on product policy
    /// that is not frozen, so it is left unapplied with
    /// <see cref="TripErrorCode.CancellationPolicyUnresolved"/> rather than
    /// inventing an allow/reject semantic. Cancellation from IN_PROGRESS is
    /// rejected: the physical ride has begun (pivot).
    /// </summary>
    public TripOutcome Cancel(DateTimeOffset now)
    {
        if (State.IsTerminal())
        {
            return Terminal(nameof(Cancel));
        }

        if (now == default)
        {
            throw new ArgumentException("Cancel requires a concrete timestamp.", nameof(now));
        }

        switch (State)
        {
            case TripState.Matching:
            case TripState.DriverReserved:
            case TripState.DriverAccepted:
                var compensation = TripCompensation.None;
                if (CurrentMatchingRound >= 1)
                {
                    compensation |= TripCompensation.CancelMatching;
                }

                if (Assignment is not null)
                {
                    compensation |= TripCompensation.ReleaseReservation;
                }

                AddCancellationCleanup(now);
                Commit(TripState.Cancelled, now);
                return TripOutcome.Success(compensation: compensation);
            case TripState.Created:
            case TripState.ReadyToStart:
                return TripOutcome.Fail(
                    TripErrorCode.CancellationPolicyUnresolved,
                    $"Cancel from {State.ToCanonicalName()} awaits frozen product policy; the Trip is left unapplied.");
            case TripState.InProgress:
            default:
                return TripOutcome.Fail(
                    TripErrorCode.IllegalTransition,
                    $"Cancel is not a legal transition from {State.ToCanonicalName()}.");
        }
    }

    // Cleanup progress shares the aggregate OCC fence, but never changes its
    // terminal business state or current assignment reference.
    public TripOutcome RecordCancelledReservation(Guid driverId, Guid reservationId,
        int matchingRound, DateTimeOffset now)
    {
        if (State != TripState.Cancelled) return Illegal(nameof(RecordCancelledReservation), TripState.Cancelled);
        if (driverId == Guid.Empty || reservationId == Guid.Empty || now == default)
            return TripOutcome.Fail(TripErrorCode.AssignmentRequired, "Late reservation requires exact identities and timestamp.");
        // An old round can still own a leaked reservation; a future/unopened
        // round is not evidence of this Trip's matching attempt.
        if (matchingRound < 1 || matchingRound > CurrentMatchingRound)
            return StaleRound(nameof(RecordCancelledReservation), matchingRound);
        var known = _cleanupIntents.SingleOrDefault(i => i.ReservationId == reservationId);
        if ((known is not null && (known.DriverId != driverId || known.MatchingRound != matchingRound)) ||
            (Assignment?.ReservationId == reservationId &&
                (Assignment.DriverId != driverId || Assignment.MatchingRound != matchingRound)))
            return TripOutcome.Fail(TripErrorCode.AssignmentIdentityMismatch, "Reservation identity contradicts existing cleanup evidence.");
        var added = AddCleanup(TripCleanupIntent.Create(TripId, TripCleanupKind.ReleaseReservation,
            matchingRound, now, driverId, reservationId));
        if (added) Commit(State, now);
        return TripOutcome.Success(isReplay: !added, compensation: TripCompensation.ReleaseReservation);
    }

    public TripOutcome RecordCancelledPaymentAuthorization(DateTimeOffset now)
    {
        if (State != TripState.Cancelled) return Illegal(nameof(RecordCancelledPaymentAuthorization), TripState.Cancelled);
        if (now == default) throw new ArgumentException("A timestamp is required.", nameof(now));
        var added = AddCleanup(TripCleanupIntent.Create(TripId, TripCleanupKind.ReconcilePayment,
            CurrentMatchingRound, now));
        if (added) Commit(State, now);
        return TripOutcome.Success(isReplay: !added, compensation: TripCompensation.ReconcilePayment);
    }

    /// <summary>Trusted workflow calls only after conclusive authority evidence;
    /// UNKNOWN, dispatch or timeout must never complete an obligation.</summary>
    public TripOutcome ConfirmCleanup(string key, DateTimeOffset now)
    {
        if (State != TripState.Cancelled) return Illegal(nameof(ConfirmCleanup), TripState.Cancelled);
        if (now == default) throw new ArgumentException("A timestamp is required.", nameof(now));
        var intent = _cleanupIntents.SingleOrDefault(i => i.Key == key);
        if (intent is null) return TripOutcome.Fail(TripErrorCode.StaleIdentity, "Cleanup identity is not owned by this Trip.");
        if (intent.CompletedAt is not null) return TripOutcome.Success(isReplay: true);
        intent.Complete(now);
        Commit(State, now);
        return TripOutcome.Success();
    }

    private void AddCancellationCleanup(DateTimeOffset now)
    {
        if (CurrentMatchingRound >= 1)
            AddCleanup(TripCleanupIntent.Create(TripId, TripCleanupKind.CancelMatching, CurrentMatchingRound, now));
        if (Assignment is not null)
        {
            AddCleanup(TripCleanupIntent.Create(TripId, TripCleanupKind.ReleaseReservation,
                Assignment.MatchingRound, now, Assignment.DriverId, Assignment.ReservationId));
            // Authorization can be in flight at cancellation. Reconcile first;
            // this is not an invented Payment operation key or a direct void.
            AddCleanup(TripCleanupIntent.Create(TripId, TripCleanupKind.ReconcilePayment, CurrentMatchingRound, now));
        }
    }

    private bool AddCleanup(TripCleanupIntent intent)
    {
        var existing = _cleanupIntents.SingleOrDefault(i => i.Key == intent.Key);
        if (existing is not null)
        {
            if (existing.DriverId != intent.DriverId || existing.ReservationId != intent.ReservationId ||
                existing.MatchingRound != intent.MatchingRound)
                throw new InvalidOperationException("Contradictory cleanup identity; refusing to overwrite.");
            return false;
        }
        _cleanupIntents.Add(intent);
        return true;
    }

    private static bool RequiresAssignment(TripState state) =>
        state is TripState.DriverReserved
            or TripState.DriverAccepted
            or TripState.ReadyToStart
            or TripState.InProgress
            or TripState.Completed;

    private static bool IsAssignedState(TripState state) =>
        state is TripState.DriverReserved
            or TripState.DriverAccepted
            or TripState.ReadyToStart
            or TripState.InProgress;

    private void Commit(TripState next, DateTimeOffset now)
    {
        State = next;
        UpdatedAt = now;
        Version++;
    }

    private TripOutcome Illegal(string operation, TripState expected) =>
        TripOutcome.Fail(
            TripErrorCode.IllegalTransition,
            $"{operation} requires {expected.ToCanonicalName()} but the Trip is {State.ToCanonicalName()}.");

    private TripOutcome Terminal(string operation) =>
        TripOutcome.Fail(
            TripErrorCode.TerminalState,
            $"{operation} is rejected: the Trip is terminal ({State.ToCanonicalName()}) and cannot reactivate (DSI-010).");

    private TripOutcome StaleRound(string operation, int matchingRound) =>
        TripOutcome.Fail(
            TripErrorCode.StaleIdentity,
            $"{operation} references matching round {matchingRound} but the Trip's current round is {CurrentMatchingRound} (DSI-016); a stale generation cannot overwrite current state.");

    private TripOutcome? CheckCurrentAssignment(Guid driverId, Guid reservationId, string operation)
    {
        if (Assignment is null)
        {
            throw new InvalidOperationException(
                $"Trip {TripId:D} is {State.ToCanonicalName()} without a current assignment; persisted state violates Trip invariants.");
        }

        if (Assignment.DriverId != driverId || Assignment.ReservationId != reservationId)
        {
            return TripOutcome.Fail(
                TripErrorCode.AssignmentIdentityMismatch,
                $"{operation} supplied an identity that does not match the current assignment.");
        }

        return null;
    }
}
