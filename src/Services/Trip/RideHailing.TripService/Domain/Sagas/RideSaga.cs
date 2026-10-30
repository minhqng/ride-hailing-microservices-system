// RideSaga aggregate root: Trip-owned persisted ride workflow (A-W10-01,
// Doc 05A §18, Doc 02 §14).
//
// The saga is the durable orchestration state for the payment legs of one
// ride: which logical Payment operation is in flight (by stable operationKey,
// DSI-034), where the workflow stands, and what compensation remains. It
// never mutates Driver/Payment authority and never executes compensation
// itself: handlers pair each saga transition with the Trip transition and the
// required Outbox intents in ONE Trip database transaction (Doc 02 §14.3).
//
// Guard precedence inside every method:
//   terminal status → TerminalState (no reactivation)
//   wrong step/status for the operation → IllegalTransition / StatusConflict
//   malformed input → IdentityRequired
//   exact duplicate of an already-current fact → Success with IsReplay and
//   NO mutation (no StepVersion advance).
// StepVersion is the application-managed optimistic-concurrency token,
// advanced exactly once per committed saga transition (mirrors Trip.Version).

namespace RideHailing.TripService.Domain.Sagas;

/// <summary>
/// One authoritative current ride-workflow per Trip (UNIQUE trip_id at the
/// storage layer). All mutations go through guarded transition methods;
/// rejected and replay operations leave the aggregate untouched.
/// </summary>
public sealed class RideSaga
{
    /// <summary>Stable workflow identity; carried on every payment envelope.</summary>
    public Guid SagaId { get; private set; }

    /// <summary>The orchestrated ride. Unique across ride_sagas.</summary>
    public Guid TripId { get; private set; }

    public RideSagaStatus Status { get; private set; }

    public RideSagaStep CurrentStep { get; private set; }

    /// <summary>
    /// Application-managed concurrency token, advanced once per committed
    /// saga transition. Persistence compares the original value only.
    /// </summary>
    public int StepVersion { get; private set; }

    /// <summary>The Driver reservation this saga's compensation must fence on.</summary>
    public Guid? ReservationId { get; private set; }

    public string? AuthorizeOperationKey { get; private set; }

    public string? CaptureOperationKey { get; private set; }

    public string? VoidOperationKey { get; private set; }

    /// <summary>Last observed non-terminal outcome code (uncertain/retry state).</summary>
    public string? LastErrorCode { get; private set; }

    public string? RequiresReviewReason { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    // Persistence materialization path (EF Core 10 cannot bind through the
    // domain constructor); public immutability is preserved, business
    // construction still goes through Start/Reconstitute with validation.
    private RideSaga()
    {
    }

    private RideSaga(
        Guid sagaId, Guid tripId, RideSagaStatus status, RideSagaStep step, int stepVersion,
        Guid? reservationId, string? authorizeKey, string? captureKey, string? voidKey,
        string? lastErrorCode, string? requiresReviewReason,
        DateTimeOffset createdAt, DateTimeOffset updatedAt)
    {
        SagaId = sagaId;
        TripId = tripId;
        Status = status;
        CurrentStep = step;
        StepVersion = stepVersion;
        ReservationId = reservationId;
        AuthorizeOperationKey = authorizeKey;
        CaptureOperationKey = captureKey;
        VoidOperationKey = voidKey;
        LastErrorCode = lastErrorCode;
        RequiresReviewReason = requiresReviewReason;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
    }

    /// <summary>
    /// Creates the saga at the DriverAccepted→Authorize step (A-W10-02): the
    /// caller commits this row in the same transaction as the Trip's
    /// DRIVER_ACCEPTED transition and the AuthorizePayment(K) outbox intent.
    /// </summary>
    public static RideSaga Start(
        Guid sagaId, Guid tripId, Guid reservationId, string authorizeOperationKey, DateTimeOffset now)
    {
        if (sagaId == Guid.Empty || tripId == Guid.Empty || reservationId == Guid.Empty)
        {
            throw new ArgumentException("RideSaga requires non-empty saga, trip and reservation identities.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(authorizeOperationKey);
        if (authorizeOperationKey != RideSagaOperationKeys.Authorize(tripId))
        {
            throw new ArgumentException(
                $"Authorize operation key must follow the frozen lineage '{RideSagaOperationKeys.Authorize(tripId)}'.",
                nameof(authorizeOperationKey));
        }

        if (now == default)
        {
            throw new ArgumentException("RideSaga requires a concrete timestamp.", nameof(now));
        }

        return new RideSaga(sagaId, tripId, RideSagaStatus.Active, RideSagaStep.AuthorizePending,
            stepVersion: 1, reservationId, authorizeOperationKey, captureKey: null, voidKey: null,
            lastErrorCode: null, requiresReviewReason: null, createdAt: now, updatedAt: now);
    }

    /// <summary>
    /// Reconstitutes persisted state with structural-coherence validation.
    /// Operation keys must match the frozen per-trip lineage (DSI-034), and
    /// step/operation presence must agree (capture step ⇒ capture key, void
    /// step ⇒ void key, terminal success ⇒ captured step).
    /// </summary>
    internal static RideSaga Reconstitute(
        Guid sagaId, Guid tripId, RideSagaStatus status, RideSagaStep step, int stepVersion,
        Guid? reservationId, string? authorizeKey, string? captureKey, string? voidKey,
        string? lastErrorCode, string? requiresReviewReason,
        DateTimeOffset createdAt, DateTimeOffset updatedAt)
    {
        if (sagaId == Guid.Empty || tripId == Guid.Empty)
        {
            throw new ArgumentException("RideSaga requires non-empty identities.");
        }

        if (stepVersion < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(stepVersion), stepVersion, "StepVersion starts at 1.");
        }

        if (createdAt == default || updatedAt == default)
        {
            throw new ArgumentException("RideSaga requires concrete timestamps.");
        }

        if (reservationId is null || reservationId == Guid.Empty)
        {
            throw new ArgumentException("RideSaga requires the fenced reservation identity.");
        }

        if (IsBlankMismatch(authorizeKey, RideSagaOperationKeys.Authorize(tripId)) ||
            IsBlankMismatch(captureKey, RideSagaOperationKeys.Capture(tripId)) ||
            IsBlankMismatch(voidKey, RideSagaOperationKeys.Void(tripId)))
        {
            throw new ArgumentException("Operation keys must be null or follow the frozen per-trip lineage (DSI-034).");
        }

        if (authorizeKey is null)
        {
            throw new ArgumentException("RideSaga always owns an authorize operation key.");
        }

        if (step is RideSagaStep.CapturePending or RideSagaStep.Captured && captureKey is null)
        {
            throw new ArgumentException("Capture steps require the capture operation key.");
        }

        if (step == RideSagaStep.VoidPending && voidKey is null)
        {
            throw new ArgumentException("Void step requires the void operation key.");
        }

        if (status == RideSagaStatus.Completed && step != RideSagaStep.Captured)
        {
            throw new ArgumentException("Terminal COMPLETED requires the CAPTURED step.");
        }

        if (status == RideSagaStatus.Compensated && step is not (RideSagaStep.ReleasePending or RideSagaStep.VoidPending))
        {
            throw new ArgumentException("Terminal COMPENSATED requires a compensation step.");
        }

        return new RideSaga(sagaId, tripId, status, step, stepVersion, reservationId,
            authorizeKey, captureKey, voidKey, lastErrorCode, requiresReviewReason, createdAt, updatedAt);
    }

    private static bool IsBlankMismatch(string? key, string expected) =>
        key is not null && key != expected;

    /// <summary>
    /// Authoritative authorization success (A-W10-03): AUTHORIZE_PENDING →
    /// AUTHORIZED. The handler verifies the outcome's operationKey against
    /// <see cref="AuthorizeOperationKey"/> and applies the Trip's
    /// MarkPaymentAuthorized in the same transaction. Legal from REQUIRES_REVIEW
    /// so a late authoritative success can resolve a parked uncertain outcome,
    /// and illegal while COMPENSATING (that race owns the void path).
    /// </summary>
    public SagaOutcome RecordAuthorizeSucceeded(DateTimeOffset now)
    {
        if (Status.IsTerminal())
        {
            return Terminal(nameof(RecordAuthorizeSucceeded));
        }

        RequireTime(now);

        if (CurrentStep == RideSagaStep.Authorized)
        {
            return SagaOutcome.Success(isReplay: true);
        }

        if (CurrentStep != RideSagaStep.AuthorizePending)
        {
            return Illegal(nameof(RecordAuthorizeSucceeded), RideSagaStep.AuthorizePending);
        }

        if (Status == RideSagaStatus.Compensating)
        {
            return SagaOutcome.Fail(
                RideSagaErrorCode.StatusConflict,
                "An authorize success arriving on a compensating saga requires the void path, never a ride revival.");
        }

        Commit(RideSagaStep.Authorized, now, RideSagaStatus.Active);
        RequiresReviewReason = null;
        return SagaOutcome.Success();
    }

    /// <summary>
    /// Non-terminal authorization outcome (DECLINED ≠ UNKNOWN, A-W10-04):
    /// transient/timeout/unknown results are durably observed but never
    /// compensate, because the provider effect may have happened (Doc 02
    /// §15.5). The same operationKey stays in force for reconciliation.
    /// </summary>
    public SagaOutcome RecordAuthorizeOutcomeUncertain(string errorCode, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        if (Status.IsTerminal())
        {
            return Terminal(nameof(RecordAuthorizeOutcomeUncertain));
        }

        RequireTime(now);

        if (CurrentStep != RideSagaStep.AuthorizePending)
        {
            return Illegal(nameof(RecordAuthorizeOutcomeUncertain), RideSagaStep.AuthorizePending);
        }

        if (LastErrorCode == errorCode)
        {
            return SagaOutcome.Success(isReplay: true);
        }

        // Observation only: no step advance; the outcome is still awaited.
        LastErrorCode = errorCode;
        Commit(CurrentStep, now);
        return SagaOutcome.Success();
    }

    /// <summary>
    /// Terminal authorization failure (A-W10-04): AUTHORIZE_PENDING →
    /// COMPENSATING/RELEASE_PENDING. Only callable with an authority-confirmed
    /// no-effect outcome (DECLINED); the handler pairs it with the Trip's
    /// MarkPaymentFailed and the exact release/cancel-round outbox intents.
    /// </summary>
    public SagaOutcome RecordAuthorizeDeclined(string errorCode, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        if (Status == RideSagaStatus.Compensating && CurrentStep == RideSagaStep.ReleasePending)
        {
            return SagaOutcome.Success(isReplay: true);
        }

        if (Status.IsTerminal())
        {
            return Terminal(nameof(RecordAuthorizeDeclined));
        }

        RequireTime(now);

        if (CurrentStep != RideSagaStep.AuthorizePending)
        {
            return Illegal(nameof(RecordAuthorizeDeclined), RideSagaStep.AuthorizePending);
        }

        LastErrorCode = errorCode;
        Commit(RideSagaStep.ReleasePending, now, RideSagaStatus.Compensating);
        return SagaOutcome.Success();
    }

    /// <summary>
    /// Rider cancellation racing an in-flight authorization (mission §7): the
    /// workflow flips to COMPENSATING immediately (release/matching cleanup
    /// obligations exist on the Trip), but the void decision waits for the
    /// authority-confirmed outcome — the authorization step stays pending.
    /// </summary>
    public SagaOutcome RecordCancellationDuringAuthorize(DateTimeOffset now)
    {
        if (Status == RideSagaStatus.Compensating && CurrentStep == RideSagaStep.AuthorizePending)
        {
            return SagaOutcome.Success(isReplay: true);
        }

        if (Status.IsTerminal())
        {
            return Terminal(nameof(RecordCancellationDuringAuthorize));
        }

        RequireTime(now);

        if (CurrentStep != RideSagaStep.AuthorizePending)
        {
            return SagaOutcome.Fail(
                RideSagaErrorCode.StatusConflict,
                $"Cancellation compensation requires an AUTHORIZE_PENDING saga but the saga is {CurrentStep.ToCanonicalName()}.");
        }

        Commit(CurrentStep, now, RideSagaStatus.Compensating);
        return SagaOutcome.Success();
    }

    /// <summary>
    /// Authorization succeeded AFTER the Trip already cancelled (A-W10-04
    /// race): the authorization effect exists at Payment authority, so an
    /// exact Void operation with the stable void key is required. The Trip
    /// stays CANCELLED (DSI-010) — this never revives the ride.
    /// </summary>
    public SagaOutcome RecordAuthorizeSucceededAfterCancellation(string voidOperationKey, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(voidOperationKey);
        if (voidOperationKey != RideSagaOperationKeys.Void(TripId))
        {
            return SagaOutcome.Fail(
                RideSagaErrorCode.OperationKeyMismatch,
                $"Void operation key must follow the frozen lineage '{RideSagaOperationKeys.Void(TripId)}'.");
        }

        if (Status == RideSagaStatus.Compensated || CurrentStep == RideSagaStep.VoidPending)
        {
            return SagaOutcome.Success(isReplay: true);
        }

        if (Status != RideSagaStatus.Compensating || CurrentStep != RideSagaStep.AuthorizePending)
        {
            return SagaOutcome.Fail(
                RideSagaErrorCode.IllegalTransition,
                "Void is required only when a cancelled saga still awaits its authorization outcome.");
        }

        RequireTime(now);
        VoidOperationKey = voidOperationKey;
        Commit(RideSagaStep.VoidPending, now);
        return SagaOutcome.Success();
    }

    /// <summary>
    /// PaymentAuthorizationVoided confirmed: the void obligation is resolved.
    /// <paramref name="reservationReleaseConfirmed"/> comes from the Trip's
    /// durable cleanup ledger in the same transaction; COMPENSATED requires
    /// BOTH obligations resolved (DSI-033 + DSL-002).
    /// </summary>
    public SagaOutcome RecordVoidResolved(DateTimeOffset now, bool reservationReleaseConfirmed)
    {
        if (Status == RideSagaStatus.Compensated)
        {
            return SagaOutcome.Success(isReplay: true);
        }

        if (Status != RideSagaStatus.Compensating || CurrentStep != RideSagaStep.VoidPending)
        {
            return Illegal(nameof(RecordVoidResolved), RideSagaStep.VoidPending);
        }

        RequireTime(now);

        if (reservationReleaseConfirmed)
        {
            Commit(CurrentStep, now, RideSagaStatus.Compensated);
        }
        else
        {
            Commit(RideSagaStep.ReleasePending, now);
        }

        return SagaOutcome.Success();
    }

    /// <summary>
    /// Driver confirmed the exact reservation release. When the void decision
    /// or a later outcome still owns the pending step, the confirmation is a
    /// durable no-op (the release fact lives on the Trip cleanup ledger) and
    /// the resolver that later leaves that step consults the ledger to finish
    /// compensation; otherwise compensation is terminal.
    /// </summary>
    public SagaOutcome RecordReservationReleased(DateTimeOffset now)
    {
        if (Status == RideSagaStatus.Compensated)
        {
            return SagaOutcome.Success(isReplay: true);
        }

        if (Status != RideSagaStatus.Compensating)
        {
            return SagaOutcome.Fail(
                RideSagaErrorCode.StatusConflict,
                $"Reservation release completes a COMPENSATING saga, not {Status.ToCanonicalName()}.");
        }

        RequireTime(now);

        if (CurrentStep is RideSagaStep.VoidPending or RideSagaStep.AuthorizePending)
        {
            // Release already durable on the Trip cleanup ledger; a pending
            // void decision (VOID_PENDING) or the not-yet-resolved payment
            // outcome (AUTHORIZE_PENDING) still owns the saga's step.
            return SagaOutcome.Success(isReplay: true);
        }

        if (CurrentStep != RideSagaStep.ReleasePending)
        {
            return Illegal(nameof(RecordReservationReleased), RideSagaStep.ReleasePending);
        }

        Commit(CurrentStep, now, RideSagaStatus.Compensated);
        return SagaOutcome.Success();
    }

    /// <summary>
    /// Start guard support: the ride may legally start only when the saga
    /// holds an authoritative authorization (A-W10-05, DSI-026).
    /// </summary>
    public bool MayStartRide =>
        Status == RideSagaStatus.Active && CurrentStep == RideSagaStep.Authorized;

    /// <summary>
    /// Legal completion precheck (A-W10-06): capture is emitted only from the
    /// authorized, non-compensating ride phase.
    /// </summary>
    public bool MayCompleteRide =>
        Status == RideSagaStatus.Active && CurrentStep == RideSagaStep.Authorized;

    /// <summary>
    /// Ride completed (A-W10-07): AUTHORIZED → CAPTURE_PENDING with the stable
    /// capture operation key, committed atomically with the Trip's COMPLETED
    /// transition and the CapturePayment outbox intent (DSI-027).
    /// </summary>
    public SagaOutcome RecordRideCompleted(string captureOperationKey, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(captureOperationKey);
        if (captureOperationKey != RideSagaOperationKeys.Capture(TripId))
        {
            return SagaOutcome.Fail(
                RideSagaErrorCode.OperationKeyMismatch,
                $"Capture operation key must follow the frozen lineage '{RideSagaOperationKeys.Capture(TripId)}'.");
        }

        if (CurrentStep == RideSagaStep.CapturePending)
        {
            return SagaOutcome.Success(isReplay: true);
        }

        if (Status.IsTerminal())
        {
            return Terminal(nameof(RecordRideCompleted));
        }

        RequireTime(now);

        if (Status != RideSagaStatus.Active || CurrentStep != RideSagaStep.Authorized)
        {
            return SagaOutcome.Fail(
                RideSagaErrorCode.StatusConflict,
                $"Capture follows an AUTHORIZED active saga, not {Status.ToCanonicalName()}/{CurrentStep.ToCanonicalName()}.");
        }

        CaptureOperationKey = captureOperationKey;
        Commit(RideSagaStep.CapturePending, now);
        return SagaOutcome.Success();
    }

    /// <summary>
    /// Capture succeeded (A-W10-07): CAPTURE_PENDING → CAPTURED/COMPLETED.
    /// </summary>
    public SagaOutcome RecordCaptureSucceeded(DateTimeOffset now)
    {
        if (Status == RideSagaStatus.Completed)
        {
            return SagaOutcome.Success(isReplay: true);
        }

        RequireTime(now);

        if (Status != RideSagaStatus.Active && Status != RideSagaStatus.RequiresReview)
        {
            return SagaOutcome.Fail(
                RideSagaErrorCode.StatusConflict,
                $"Capture success resolves an awaiting saga, not {Status.ToCanonicalName()}.");
        }

        if (CurrentStep != RideSagaStep.CapturePending)
        {
            return Illegal(nameof(RecordCaptureSucceeded), RideSagaStep.CapturePending);
        }

        LastErrorCode = null;
        RequiresReviewReason = null;
        Commit(RideSagaStep.Captured, now, RideSagaStatus.Completed);
        return SagaOutcome.Success();
    }

    /// <summary>
    /// Capture uncertain/transient after completion (A-W10-08, DSI-028): the
    /// observation is durable but the Trip is NEVER regressed — the saga stays
    /// durably pending on the SAME capture operationKey for forward recovery.
    /// </summary>
    public SagaOutcome RecordCaptureOutcomeUncertain(string errorCode, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        if (Status.IsTerminal())
        {
            return Terminal(nameof(RecordCaptureOutcomeUncertain));
        }

        RequireTime(now);

        if (CurrentStep != RideSagaStep.CapturePending)
        {
            return Illegal(nameof(RecordCaptureOutcomeUncertain), RideSagaStep.CapturePending);
        }

        if (LastErrorCode == errorCode)
        {
            return SagaOutcome.Success(isReplay: true);
        }

        LastErrorCode = errorCode;
        Commit(CurrentStep, now);
        return SagaOutcome.Success();
    }

    /// <summary>
    /// Explicit visible review state (A-W10-08): Payment authority signalled
    /// REQUIRES_REVIEW (for example RETRY_EXHAUSTED). The current step is
    /// preserved so a later authoritative resolution can continue the saga.
    /// A COMPENSATING saga is never owned by the review signal: the review is
    /// recorded as a durable observation and compensation keeps the status,
    /// because every compensation resolver requires it — clobbering the
    /// status would strand the void/release legs permanently.
    /// </summary>
    public SagaOutcome RecordRequiresReview(string reason, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (Status == RideSagaStatus.RequiresReview)
        {
            return SagaOutcome.Success(isReplay: true);
        }

        if (Status.IsTerminal())
        {
            return Terminal(nameof(RecordRequiresReview));
        }

        RequireTime(now);

        if (Status == RideSagaStatus.Compensating)
        {
            if (LastErrorCode == reason)
            {
                return SagaOutcome.Success(isReplay: true);
            }

            LastErrorCode = reason;
            Commit(CurrentStep, now);
            return SagaOutcome.Success();
        }

        RequiresReviewReason = reason;
        Commit(CurrentStep, now, RideSagaStatus.RequiresReview);
        return SagaOutcome.Success();
    }

    private void Commit(RideSagaStep step, DateTimeOffset now, RideSagaStatus? status = null)
    {
        CurrentStep = step;
        if (status.HasValue)
        {
            Status = status.Value;
        }

        UpdatedAt = now;
        StepVersion++;
    }

    private static void RequireTime(DateTimeOffset now)
    {
        if (now == default)
        {
            throw new ArgumentException("A concrete timestamp is required.", nameof(now));
        }
    }

    private SagaOutcome Illegal(string operation, RideSagaStep expected) =>
        SagaOutcome.Fail(
            RideSagaErrorCode.IllegalTransition,
            $"{operation} requires {expected.ToCanonicalName()} but the saga is {CurrentStep.ToCanonicalName()} ({Status.ToCanonicalName()}).");

    private SagaOutcome Terminal(string operation) =>
        SagaOutcome.Fail(
            RideSagaErrorCode.TerminalState,
            $"{operation} is rejected: the saga is terminal ({Status.ToCanonicalName()}).");
}
