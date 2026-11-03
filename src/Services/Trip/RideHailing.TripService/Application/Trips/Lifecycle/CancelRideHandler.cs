// CancelRide application handler (A-W05 cancellation matrix, pre-start).
//
// Rider-scoped: only the owning Rider can cancel their ride (fail closed;
// missing and not-owned share NotFound per SEC-03). Loads the Trip, calls
// the single aggregate Cancel operation with the deterministic clock,
// persists the authoritative terminal result, maps the domain outcome
// including durable cleanup intents saved atomically with cancellation.
// External dispatch and authority reconciliation belong to the workflow adapter.

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RideHailing.TripService.Domain.Sagas;
using RideHailing.TripService.Domain.Trips;
using RideHailing.TripService.Infrastructure.Persistence;
using RideHailing.TripService.Integration.Messaging;
using RideHailing.TripService.Integration.Observability;

namespace RideHailing.TripService.Application.Trips.Lifecycle;

/// <summary>
/// Rider-scoped pre-start cancellation intake. No HTTP dependency; the Rider
/// identity arrives as explicit trusted application input.
/// </summary>
public sealed class CancelRideHandler
{
    private readonly TripDbContext _db;
    private readonly TimeProvider _time;
    private readonly ILogger<CancelRideHandler> _log;

    public CancelRideHandler(TripDbContext db, TimeProvider time, ILogger<CancelRideHandler> log)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(log);
        _db = db;
        _time = time;
        _log = log;
    }

    /// <summary>
    /// Cancels the owned pre-start Trip, preserving any assignment as the
    /// exact future cleanup identity.
    /// </summary>
    public async Task<TripLifecycleResult> HandleAsync(Guid tripId, Guid riderId, CancellationToken cancellationToken = default)
    {
        var trip = await _db.Trips
            .SingleOrDefaultAsync(t => t.TripId == tripId, cancellationToken)
            .ConfigureAwait(false);

        if (trip is null || trip.RiderId != riderId)
        {
            return TripLifecycleResult.NotFound();
        }

        if (trip.State == TripState.Cancelled)
            return TripLifecycleResult.Success(trip, isReplay: true, TripCompensation.None);

        var saga = await _db.RideSagas
            .SingleOrDefaultAsync(s => s.TripId == tripId, cancellationToken)
            .ConfigureAwait(false);

        // A legacy/authority-shaped intake can mark READY_TO_START without a
        // persisted saga. Cancelling that ride cannot mint the exact void
        // operation lineage, so refuse it instead of stranding an
        // authorization; the normal W10 path always has the saga.
        if (trip.State == TripState.ReadyToStart && saga is null)
        {
            return TripLifecycleResult.Fail(trip, TripOutcome.Fail(
                TripErrorCode.IllegalTransition,
                "Cancellation from READY_TO_START requires the persisted authorization saga; this ride must be reviewed."));
        }

        // Snapshot before Cancel so only the obligations added by this
        // transition are staged; an upgrade/backfill is never silently
        // republished as a new logical message.
        var preexistingCleanup = TripOutboxWriter.CleanupKeys(trip);
        var now = _time.GetUtcNow();
        var outcome = trip.Cancel(now);
        if (!outcome.IsSuccess)
        {
            return TripLifecycleResult.Fail(trip, outcome);
        }

        // A-W10: cancelling at DRIVER_ACCEPTED with an in-flight
        // authorization flips the saga to COMPENSATING immediately (release
        // and matching cleanup exist now), while the void decision waits for
        // the authority-confirmed outcome — never assume the effect absent.
        // At READY_TO_START the authorization already succeeded, so the exact
        // void command is staged in this same transaction (no second effect,
        // no blind retry) and the saga starts directly at VOID_PENDING.
        var sagaStatusBefore = saga?.Status;
        if (saga is not null)
        {
            SagaOutcome sagaOutcome;
            if (saga.CurrentStep == RideSagaStep.AuthorizePending)
            {
                sagaOutcome = saga.RecordCancellationDuringAuthorize(now);
            }
            else if (saga.CurrentStep == RideSagaStep.Authorized)
            {
                sagaOutcome = saga.RecordCancellationAfterAuthorize(
                    RideSagaOperationKeys.Void(tripId), now);
                if (sagaOutcome.IsSuccess && !sagaOutcome.IsReplay)
                {
                    TripOutboxWriter.Enqueue(_db,
                        TripSagaIntents.VoidPaymentAuthorization(trip, saga, now));
                }
            }
            else
            {
                throw new InvalidOperationException(
                    $"Cancellation from {trip.State.ToCanonicalName()} cannot compensate saga step " +
                    $"{saga.CurrentStep.ToCanonicalName()} for trip {tripId:D}.");
            }

            if (!sagaOutcome.IsSuccess)
            {
                throw new InvalidOperationException(
                    $"Saga rejected cancellation compensation for trip {tripId:D}: {sagaOutcome.ErrorCode} {sagaOutcome.Message}");
            }
        }

        // A-W09-02: CANCELLED + CancelMatchingRound/ReleaseDriverReservation
        // intents commit atomically. The ReconcilePayment intent resolves via
        // the W10 payment outcome/void lineage, not a published command here.
        TripOutboxWriter.EnqueueNewCleanup(_db, trip, preexistingCleanup);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // A-W12-02: post-commit observation only — the committed saga status
        // transition into COMPENSATING is counted once, here, where the unit
        // of work is known durable. A failed save never reaches this line.
        if (saga is not null && sagaStatusBefore != RideSagaStatus.Compensating &&
            saga.Status == RideSagaStatus.Compensating)
        {
            TripDiagnostics.RecordSagaCompensation();
        }

        _log.LogInformation(
            "CancelRide {TripId} state {State} compensation {Compensation} saga {SagaStatus}.",
            trip.TripId,
            trip.State.ToCanonicalName(),
            outcome.Compensation,
            saga?.Status.ToCanonicalName() ?? "NONE");

        return TripLifecycleResult.Success(trip, outcome.IsReplay, outcome.Compensation);
    }
}
