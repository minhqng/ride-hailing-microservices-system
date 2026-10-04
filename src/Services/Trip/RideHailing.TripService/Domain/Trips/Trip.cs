// Trip aggregate root: authoritative local ride lifecycle (Doc 01 §6).
//
// The aggregate guards DSI-008 (at most one current assignment), DSI-009
// (assignment identity coherence), DSI-010 (terminal monotonicity), DSI-011
// (start requires current assignment) and DSI-012 (completion only from
// IN_PROGRESS). It records only the Trip-side reference to authoritative
// Driver/Payment results; reservation and payment authority stay with the
// Driver and Payment services. Matching-round lifecycle, stale-round guards,
// request idempotency, persistence mapping and messaging belong to later work.

namespace RideHailing.TripService.Domain.Trips;

/// <summary>
/// Authoritative local Trip lifecycle: state, current assignment reference,
/// frozen fare and a mechanical concurrency version. All mutations go through
/// guarded transition methods; rejected operations leave the aggregate
/// untouched (no partial state, assignment or version change).
/// </summary>
public sealed class Trip
{
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

    /// <summary>Zero or one coherent current assignment (DSI-008).</summary>
    public TripAssignment? Assignment { get; private set; }

    /// <summary>When the Trip was created.</summary>
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>
    /// Mechanical concurrency version, advanced once per committed local
    /// transition. Rejected operations never advance it. The initial value is
    /// an implementation detail, not a business rule.
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
        TripAssignment? assignment,
        DateTimeOffset createdAt,
        int version)
    {
        TripId = tripId;
        RiderId = riderId;
        Pickup = pickup;
        Dropoff = dropoff;
        Fare = fare;
        State = state;
        Assignment = assignment;
        CreatedAt = createdAt;
        Version = version;
    }

    /// <summary>
    /// Constructs a valid new Trip-domain object in CREATED state with no
    /// assignment. Takes an already-created <see cref="FareSnapshot"/> and the
    /// single application-supplied creation timestamp; creation performs no
    /// idempotency, active-trip or fare-calculation logic, which belong to
    /// later application work. The domain performs no clock access.
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

        return new Trip(tripId, riderId, pickup, dropoff, fare, TripState.Created, null, createdAt, 0);
    }

    /// <summary>
    /// Reconstitutes persisted historical state. Enforces structural
    /// coherence: DRIVER_RESERVED, DRIVER_ACCEPTED, READY_TO_START,
    /// IN_PROGRESS and COMPLETED require a coherent current assignment
    /// because each is reachable only with one (DSI-009, DSI-011, DSI-012).
    /// CREATED, MATCHING and MATCH_FAILED do not require an assignment.
    /// CANCELLED allows either form so pre-assignment cancellation and
    /// post-assignment cleanup identity both survive.
    /// </summary>
    internal static Trip Reconstitute(
        Guid tripId,
        Guid riderId,
        TripLocation pickup,
        TripLocation dropoff,
        FareSnapshot fare,
        TripState state,
        TripAssignment? assignment,
        DateTimeOffset createdAt,
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

        if (RequiresAssignment(state) && assignment is null)
        {
            throw new ArgumentException(
                $"Cannot reconstitute {state.ToCanonicalName()} without a current assignment.",
                nameof(assignment));
        }

        return new Trip(tripId, riderId, pickup, dropoff, fare, state, assignment, createdAt, version);
    }

    /// <summary>
    /// Applies the local consequence of a legal Driver reservation result:
    /// MATCHING → DRIVER_RESERVED with one coherent assignment. The caller
    /// supplies authoritative facts; this method never contacts the Driver
    /// service. Current-round/stale-message eligibility belongs to later
    /// application guards.
    /// </summary>
    public TripOutcome ApplyDriverReserved(Guid driverId, Guid reservationId, int matchingRound)
    {
        if (State.IsTerminal())
        {
            return Terminal(nameof(ApplyDriverReserved));
        }

        if (State != TripState.Matching)
        {
            return Illegal(nameof(ApplyDriverReserved), TripState.Matching);
        }

        if (driverId == Guid.Empty || reservationId == Guid.Empty)
        {
            return TripOutcome.Fail(
                TripErrorCode.AssignmentRequired,
                "Applying a Driver reservation requires non-empty driver and reservation identities.");
        }

        if (Assignment is not null)
        {
            return TripOutcome.Fail(
                TripErrorCode.AssignmentAlreadyPresent,
                "Trip already holds a current assignment; a second simultaneous assignment is rejected (DSI-008).");
        }

        Assignment = new TripAssignment(driverId, reservationId, matchingRound);
        Commit(TripState.DriverReserved);
        return TripOutcome.Success();
    }

    /// <summary>
    /// Records the deterministic local part of Driver acceptance:
    /// DRIVER_RESERVED → DRIVER_ACCEPTED. The caller must identify the
    /// current assignment; mismatched identities are rejected.
    /// </summary>
    public TripOutcome RecordDriverAccepted(Guid driverId, Guid reservationId)
    {
        if (State.IsTerminal())
        {
            return Terminal(nameof(RecordDriverAccepted));
        }

        if (State != TripState.DriverReserved)
        {
            return Illegal(nameof(RecordDriverAccepted), TripState.DriverReserved);
        }

        if (Assignment is null)
        {
            return TripOutcome.Fail(
                TripErrorCode.AssignmentRequired,
                "Driver acceptance requires a current assignment.");
        }

        if (driverId == Guid.Empty || reservationId == Guid.Empty)
        {
            return TripOutcome.Fail(
                TripErrorCode.AssignmentRequired,
                "Driver acceptance requires non-empty driver and reservation identities.");
        }

        if (Assignment.DriverId != driverId || Assignment.ReservationId != reservationId)
        {
            return TripOutcome.Fail(
                TripErrorCode.AssignmentIdentityMismatch,
                "Supplied driver/reservation identity does not match the current assignment.");
        }

        Commit(TripState.DriverAccepted);
        return TripOutcome.Success();
    }

    /// <summary>
    /// Records the local consequence of authoritative Payment authorization:
    /// DRIVER_ACCEPTED → READY_TO_START. Payment truth itself is supplied by
    /// the future workflow layer, never decided here.
    /// </summary>
    public TripOutcome MarkPaymentAuthorized()
    {
        if (State.IsTerminal())
        {
            return Terminal(nameof(MarkPaymentAuthorized));
        }

        if (State != TripState.DriverAccepted)
        {
            return Illegal(nameof(MarkPaymentAuthorized), TripState.DriverAccepted);
        }

        Commit(TripState.ReadyToStart);
        return TripOutcome.Success();
    }

    /// <summary>
    /// Records the Trip-state consequence of terminal Payment failure:
    /// DRIVER_ACCEPTED → CANCELLED. The assignment is preserved so later
    /// workflow work keeps the exact cleanup identity. No Driver release,
    /// void or message is executed here.
    /// </summary>
    public TripOutcome MarkPaymentFailed()
    {
        if (State.IsTerminal())
        {
            return Terminal(nameof(MarkPaymentFailed));
        }

        if (State != TripState.DriverAccepted)
        {
            return Illegal(nameof(MarkPaymentFailed), TripState.DriverAccepted);
        }

        Commit(TripState.Cancelled);
        return TripOutcome.Success();
    }

    /// <summary>
    /// Records the locally deterministic matching failure:
    /// MATCHING → MATCH_FAILED. Rejected when the aggregate already holds a
    /// current assignment, which contradicts a clean matching failure.
    /// </summary>
    public TripOutcome MarkMatchingFailed()
    {
        if (State.IsTerminal())
        {
            return Terminal(nameof(MarkMatchingFailed));
        }

        if (State != TripState.Matching)
        {
            return Illegal(nameof(MarkMatchingFailed), TripState.Matching);
        }

        if (Assignment is not null)
        {
            return TripOutcome.Fail(
                TripErrorCode.AssignmentAlreadyPresent,
                "Matching cannot fail cleanly while the Trip holds a current assignment.");
        }

        Commit(TripState.MatchFailed);
        return TripOutcome.Success();
    }

    /// <summary>
    /// Starts the ride: READY_TO_START → IN_PROGRESS (DSI-011). Requires the
    /// current assignment to exist with matching identity. READY_TO_START
    /// itself is the local fact that authorization was previously accepted;
    /// no remote service is consulted.
    /// </summary>
    public TripOutcome Start(Guid driverId, Guid reservationId)
    {
        if (State.IsTerminal())
        {
            return Terminal(nameof(Start));
        }

        if (State != TripState.ReadyToStart)
        {
            return Illegal(nameof(Start), TripState.ReadyToStart);
        }

        var identity = CheckCurrentAssignment(driverId, reservationId, nameof(Start));
        if (identity is not null)
        {
            return identity;
        }

        Commit(TripState.InProgress);
        return TripOutcome.Success();
    }

    /// <summary>
    /// Completes the ride: IN_PROGRESS → COMPLETED (DSI-012) for the current
    /// Trip/Driver relationship. Afterwards the Trip is terminal. No Payment
    /// capture is performed here.
    /// </summary>
    public TripOutcome Complete(Guid driverId, Guid reservationId)
    {
        if (State.IsTerminal())
        {
            return Terminal(nameof(Complete));
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

        Commit(TripState.Completed);
        return TripOutcome.Success();
    }

    /// <summary>
    /// Cancels the Trip from an unambiguous pre-start state (MATCHING,
    /// DRIVER_RESERVED, DRIVER_ACCEPTED) → CANCELLED. The assignment is
    /// preserved for exact later cleanup. Cancellation from IN_PROGRESS is
    /// rejected; cancellation from CREATED or READY_TO_START depends on
    /// product policy that is not frozen, so it is left unapplied with
    /// <see cref="TripErrorCode.CancellationPolicyUnresolved"/> rather than
    /// inventing an allow/reject semantic.
    /// </summary>
    public TripOutcome Cancel()
    {
        if (State.IsTerminal())
        {
            return Terminal(nameof(Cancel));
        }

        switch (State)
        {
            case TripState.Matching:
            case TripState.DriverReserved:
            case TripState.DriverAccepted:
                Commit(TripState.Cancelled);
                return TripOutcome.Success();
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

    private static bool RequiresAssignment(TripState state) =>
        state is TripState.DriverReserved
            or TripState.DriverAccepted
            or TripState.ReadyToStart
            or TripState.InProgress
            or TripState.Completed;

    private void Commit(TripState next)
    {
        State = next;
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

    private TripOutcome? CheckCurrentAssignment(Guid driverId, Guid reservationId, string operation)
    {
        if (Assignment is null)
        {
            return TripOutcome.Fail(
                TripErrorCode.AssignmentRequired,
                $"{operation} requires a current assignment (DSI-011).");
        }

        if (driverId == Guid.Empty || reservationId == Guid.Empty)
        {
            return TripOutcome.Fail(
                TripErrorCode.AssignmentRequired,
                $"{operation} requires non-empty driver and reservation identities.");
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
