// Payment-outcome saga intake (A-W10-03/04/08, Doc 05A §18, Doc 02 §14.4-14.6).
//
// ONE shared application core for authoritative Payment outcome facts, used
// by both the broker consumer (trip.payment-outcome.q, envelope-carried
// operationKey/sagaId lineage) and the trusted system intakes. It pairs
// every Trip transition with the matching saga transition and any required
// outbox intents in the CALLER's open unit of work — never a separate
// commit, never a remote Payment call.
//
// Lineage rules enforced before any mutation (DSI-034, Doc 05A §18.4):
//   * a supplied operationKey must equal the saga's key for the awaited step;
//   * a supplied sagaId must equal this saga's identity;
//   * the saga must be on the step the outcome resolves.
// DECLINED ≠ UNKNOWN: only authority-confirmed no-effect outcomes
// (DECLINED, NO_PROVIDER_EFFECT) compensate. Transient/timeout/lost-response
// codes are durably observed and the SAME operationKey stays in force
// (Doc 02 §15.5). A capture failure never regresses a COMPLETED Trip
// (DSI-028): the saga holds the pending/review state, not the ride.
//
// Trips without a saga keep the frozen W8 skeleton behavior: the outcome is
// parked durably and never mutates ride state from a tripId alone.

using RideHailing.Messaging.Outbox;
using RideHailing.TripService.Domain.Sagas;
using RideHailing.TripService.Domain.Trips;
using RideHailing.TripService.Infrastructure.Persistence;
using RideHailing.TripService.Integration.Messaging;
using static RideHailing.TripService.Application.Trips.Lifecycle.PaymentIntakeResult;

namespace RideHailing.TripService.Application.Trips.Lifecycle;

/// <summary>An authoritative Payment outcome fact awaiting application.</summary>
public sealed record PaymentOutcomeFact(
    string MessageType,
    string? OperationKey,
    string? SagaId,
    string? ResultCode,
    bool Retryable = false);

/// <summary>Receipt-style application result (disposition + stable reason code).</summary>
public sealed record PaymentIntakeResult(string Disposition, string Reason)
{
    public static PaymentIntakeResult Applied { get; } = new("Applied", string.Empty);

    public static PaymentIntakeResult Replay { get; } = new("BusinessReplay", string.Empty);

    public static PaymentIntakeResult Parked(string reason) => new("PendingWorkflow", reason);

    public static PaymentIntakeResult Review(string reason) => new("NeedsReview", reason);
}

/// <summary>
/// Applies one Payment outcome to the Trip/Saga pair inside the caller's
/// open unit of work. Cleanup intents the transition creates are staged by
/// the CALLER from its own pre-transition snapshot; this core stages only
/// the saga-owned payment commands (void).
/// </summary>
public static class PaymentOutcomeIntake
{
    /// <summary>
    /// Authority-confirmed codes proving NO provider effect exists for the
    /// authorization: compensation may treat the payment leg as absent.
    /// Anything else (TRANSIENT_FAILURE, TIMEOUT_*, PROVIDER_RESPONSE_LOST,
    /// UNKNOWN, or a missing code) is uncertain and must NOT compensate.
    /// </summary>
    private static readonly HashSet<string> TerminalNoEffectCodes = new(StringComparer.Ordinal)
    {
        "DECLINED", "NO_PROVIDER_EFFECT",
    };

    private const string ReconcileCleanupKey = "payment";

    public static PaymentIntakeResult Apply(
        Trip trip,
        RideSaga? saga,
        PaymentOutcomeFact fact,
        DateTimeOffset now,
        TripDbContext db)
    {
        ArgumentNullException.ThrowIfNull(trip);
        ArgumentNullException.ThrowIfNull(fact);
        ArgumentNullException.ThrowIfNull(db);

        if (saga is null)
        {
            // Frozen W8 skeleton: no saga lineage exists, so a tripId alone
            // never authorizes or fails the ride. The one recorded exception
            // (authorization success on a cancelled Trip) stays durable as a
            // reconcile obligation for manual/W10 lineage recovery.
            if (fact.MessageType == "PaymentAuthorizedV1" && trip.State == TripState.Cancelled)
            {
                trip.RecordCancelledPaymentAuthorization(now);
            }

            return Parked("PAYMENT_LINEAGE_UNRESOLVED");
        }

        if (saga.TripId != trip.TripId)
        {
            return Review("SAGA_TRIP_MISMATCH");
        }

        if (fact.SagaId is not null && fact.SagaId != saga.SagaId.ToString("D"))
        {
            return Review("SAGA_IDENTITY_MISMATCH");
        }

        return fact.MessageType switch
        {
            "PaymentAuthorizedV1" => ApplyAuthorized(trip, saga, fact, now, db),
            "PaymentAuthorizationFailedV1" => ApplyAuthorizationFailed(trip, saga, fact, now, db),
            "PaymentAuthorizationVoidedV1" => ApplyVoided(trip, saga, fact, now),
            "PaymentAuthorizationVoidFailedV1" => ApplyVoidFailed(saga, fact, now),
            "PaymentCapturedV1" => ApplyCaptured(trip, saga, fact, now),
            "PaymentCapturePendingV1" => ApplyCaptureUncertain(saga, fact, now),
            "PaymentRequiresReviewV1" => ApplyRequiresReview(saga, fact, now),
            _ => Review("PAYMENT_OUTCOME_TYPE_UNKNOWN"),
        };
    }

    private static PaymentIntakeResult ApplyAuthorized(
        Trip trip, RideSaga saga, PaymentOutcomeFact fact, DateTimeOffset now, TripDbContext db)
    {
        if (Mismatch(fact.OperationKey, saga.AuthorizeOperationKey))
        {
            return Review("PAYMENT_OPERATION_MISMATCH");
        }

        if (trip.State == TripState.DriverAccepted && saga.CurrentStep == RideSagaStep.AuthorizePending)
        {
            trip.MarkPaymentAuthorized(now);
            Require(saga.RecordAuthorizeSucceeded(now));
            return Applied;
        }

        if (trip.State is TripState.ReadyToStart or TripState.InProgress
            && saga.CurrentStep == RideSagaStep.Authorized)
        {
            return Replay;
        }

        if (trip.State == TripState.Cancelled)
        {
            // Authorization raced the rider's cancellation and WON at Payment
            // authority: the hold exists, so an exact void with the stable
            // void key is dispatched. The Trip stays CANCELLED (DSI-010).
            if (saga.CurrentStep == RideSagaStep.AuthorizePending)
            {
                trip.RecordCancelledPaymentAuthorization(now);
                Require(saga.RecordAuthorizeSucceededAfterCancellation(
                    RideSagaOperationKeys.Void(trip.TripId), now));
                TripOutboxWriter.Enqueue(db, TripSagaIntents.VoidPaymentAuthorization(trip, saga, now));
                return Applied;
            }

            return Review("STALE_PAYMENT_OUTCOME");
        }

        if (saga.Status == RideSagaStatus.Completed || trip.State == TripState.Completed)
        {
            return Review("STALE_PAYMENT_OUTCOME");
        }

        return Review("PAYMENT_OUT_OF_ORDER");
    }

    private static PaymentIntakeResult ApplyAuthorizationFailed(
        Trip trip, RideSaga saga, PaymentOutcomeFact fact, DateTimeOffset now, TripDbContext db)
    {
        if (Mismatch(fact.OperationKey, saga.AuthorizeOperationKey))
        {
            return Review("PAYMENT_OPERATION_MISMATCH");
        }

        if (saga.CurrentStep != RideSagaStep.AuthorizePending)
        {
            // A failure for an already-resolved authorize step is stale; a
            // late failure can never regress an authorized ride.
            return Review("STALE_PAYMENT_OUTCOME");
        }

        var code = fact.ResultCode;
        if (code is null || !TerminalNoEffectCodes.Contains(code))
        {
            // DECLINED ≠ UNKNOWN: an uncertain outcome never compensates; the
            // saga stays pending on the SAME operationKey for resolution.
            Require(saga.RecordAuthorizeOutcomeUncertain(code ?? "UNKNOWN", now));
            return Parked("PAYMENT_OUTCOME_UNRESOLVED");
        }

        if (trip.State == TripState.DriverAccepted)
        {
            // REL-09: terminal pre-start failure. One transaction carries the
            // Trip cancellation, the saga compensation step and the exact
            // release/cancel-round intents (DSI-033).
            trip.MarkPaymentFailed(now);
            Require(saga.RecordAuthorizeDeclined(code, now));
            // DECLINED is conclusive no-effect evidence: the reconcile
            // obligation created with the cancellation is discharged now.
            trip.ConfirmCleanup(ReconcileCleanupKey, now);
            return Applied;
        }

        if (trip.State == TripState.Cancelled)
        {
            // The rider cancelled first; the outcome then resolved to a
            // definite no-effect. No void is required.
            Require(saga.RecordAuthorizeDeclined(code, now));
            trip.ConfirmCleanup(ReconcileCleanupKey, now);
            // The release confirmation may already be durable on the Trip
            // ledger (Driver routinely confirms before Payment resolves);
            // finish compensation now instead of waiting for a consumed event.
            if (ReleaseConfirmed(trip))
            {
                Require(saga.RecordReservationReleased(now));
            }

            return Applied;
        }

        return Review("PAYMENT_OUT_OF_ORDER");
    }

    private static bool ReleaseConfirmed(Trip trip) =>
        trip.CleanupIntents.Any(i =>
            i.Kind == TripCleanupKind.ReleaseReservation && i.CompletedAt is not null);

    private static PaymentIntakeResult ApplyVoided(Trip trip, RideSaga saga, PaymentOutcomeFact fact, DateTimeOffset now)
    {
        if (Mismatch(fact.OperationKey, saga.VoidOperationKey))
        {
            return Review("PAYMENT_OPERATION_MISMATCH");
        }

        if (saga.CurrentStep != RideSagaStep.VoidPending)
        {
            return saga.Status == RideSagaStatus.Compensated
                ? Replay
                : Review("STALE_PAYMENT_OUTCOME");
        }

        var releaseConfirmed = ReleaseConfirmed(trip);
        Require(saga.RecordVoidResolved(now, releaseConfirmed));
        // The void conclusively resolves the payment leg created by the
        // cancelled-but-authorized race: discharge the reconcile obligation.
        trip.ConfirmCleanup(ReconcileCleanupKey, now);
        return Applied;
    }

    /// <summary>
    /// Void-specific failure. It must match the Saga's VOID key, never the
    /// authorize key: a void failure is a compensation problem, not a
    /// mismatched authorization outcome. Retryable failures stay compensating
    /// on the same void key for reconciliation; terminal failures become a
    /// visible review state without claiming the reservation release is done.
    /// </summary>
    private static PaymentIntakeResult ApplyVoidFailed(
        RideSaga saga, PaymentOutcomeFact fact, DateTimeOffset now)
    {
        if (Mismatch(fact.OperationKey, saga.VoidOperationKey))
        {
            return Review("PAYMENT_OPERATION_MISMATCH");
        }

        if (saga.Status == RideSagaStatus.Compensated)
        {
            return Replay;
        }

        if (saga.CurrentStep != RideSagaStep.VoidPending)
        {
            return Review("STALE_PAYMENT_OUTCOME");
        }

        Require(saga.RecordVoidFailed(
            fact.ResultCode ?? "VOID_FAILED", fact.Retryable, now));
        return fact.Retryable ? Parked("VOID_OUTCOME_UNRESOLVED") : Applied;
    }

    private static PaymentIntakeResult ApplyCaptured(Trip trip, RideSaga saga, PaymentOutcomeFact fact, DateTimeOffset now)
    {
        if (Mismatch(fact.OperationKey, saga.CaptureOperationKey))
        {
            return Review("PAYMENT_OPERATION_MISMATCH");
        }

        if (saga.Status == RideSagaStatus.Completed)
        {
            return Replay;
        }

        if (saga.CurrentStep != RideSagaStep.CapturePending || trip.State != TripState.Completed)
        {
            // Capture success before completion is out of order (DSI-027);
            // never fabricate completion from a payment fact.
            return Review("PAYMENT_OUT_OF_ORDER");
        }

        Require(saga.RecordCaptureSucceeded(now));
        return Applied;
    }

    private static PaymentIntakeResult ApplyCaptureUncertain(RideSaga saga, PaymentOutcomeFact fact, DateTimeOffset now)
    {
        if (Mismatch(fact.OperationKey, saga.CaptureOperationKey))
        {
            return Review("PAYMENT_OPERATION_MISMATCH");
        }

        if (saga.CurrentStep != RideSagaStep.CapturePending)
        {
            return Review("STALE_PAYMENT_OUTCOME");
        }

        // DSI-028: the ride NEVER regresses; the observation is durable and
        // the same capture key stays pending for forward recovery.
        Require(saga.RecordCaptureOutcomeUncertain(fact.ResultCode ?? "CAPTURE_PENDING", now));
        return Applied;
    }

    private static PaymentIntakeResult ApplyRequiresReview(RideSaga saga, PaymentOutcomeFact fact, DateTimeOffset now)
    {
        var expected = saga.CurrentStep switch
        {
            RideSagaStep.CapturePending => saga.CaptureOperationKey,
            RideSagaStep.AuthorizePending => saga.AuthorizeOperationKey,
            RideSagaStep.VoidPending => saga.VoidOperationKey,
            _ => null,
        };

        if (Mismatch(fact.OperationKey, expected))
        {
            return Review("PAYMENT_OPERATION_MISMATCH");
        }

        Require(saga.RecordRequiresReview(fact.ResultCode ?? "PAYMENT_REQUIRES_REVIEW", now));
        return Applied;
    }

    private static bool Mismatch(string? supplied, string? expected) =>
        supplied is null || expected is null || !string.Equals(supplied, expected, StringComparison.Ordinal);

    private static void Require(SagaOutcome outcome)
    {
        // The caller already proved the step/lineage preconditions; a domain
        // rejection here means persisted Trip/Saga disagreement, which is a
        // corruption fault, never a silent partial application.
        if (!outcome.IsSuccess)
        {
            throw new InvalidOperationException(
                $"Saga transition rejected after lineage verification: {outcome.ErrorCode} {outcome.Message}");
        }
    }
}
