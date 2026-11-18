// Start/complete ride system intake.
//
// Applies Driver-actor start/completion evidence to the Trip lifecycle. The
// supplied driver/reservation identity is fenced against the authoritative
// current assignment by the aggregate; authorization
// ordering is enforced by lifecycle state (READY_TO_START is the
// local proof that authorization was recorded) and, when a saga exists, by
// the saga's authorized-ride phase as a second guard. Driver transport
// authentication and profile binding are
// enforced by the public lifecycle endpoint; this handler stays HTTP-independent.
//
// Saga obligations committed atomically with each FIRST transition:
//   Start:    Trip IN_PROGRESS + Outbox MarkDriverTripStartedV1 (the Driver
//             service owns the RESERVED→ON_TRIP authority).
//   Complete: Trip COMPLETED + Saga CAPTURE_PENDING + Outbox CapturePayment
//  (stable K_capture) + Outbox MarkDriverTripCompletedV1.
// Replays stage nothing: a duplicate fact must never mint a second logical
// payment effect or driver command.

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RideHailing.TripService.Domain.Sagas;
using RideHailing.TripService.Domain.Trips;
using RideHailing.TripService.Infrastructure.Persistence;
using RideHailing.TripService.Integration.Messaging;

namespace RideHailing.TripService.Application.Trips.Lifecycle;

/// <summary>
/// System-side StartTrip intake. No HTTP dependency.
/// </summary>
public sealed class StartTripHandler
{
    private readonly TripDbContext _db;
    private readonly TimeProvider _time;
    private readonly ILogger<StartTripHandler> _log;

    public StartTripHandler(TripDbContext db, TimeProvider time, ILogger<StartTripHandler> log)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(log);
        _db = db;
        _time = time;
        _log = log;
    }

    /// <summary>
    /// Starts the ride for the exact current assignment, after the saga (when
    /// present) confirms authoritative authorization.
    /// </summary>
    public async Task<TripLifecycleResult> HandleAsync(
        Guid tripId,
        Guid driverId,
        Guid reservationId,
        CancellationToken cancellationToken = default)
    {
        var trip = await _db.Trips
            .SingleOrDefaultAsync(t => t.TripId == tripId, cancellationToken)
            .ConfigureAwait(false);

        if (trip is null)
        {
            return TripLifecycleResult.NotFound();
        }

        var saga = await _db.RideSagas
            .SingleOrDefaultAsync(s => s.TripId == tripId, cancellationToken)
            .ConfigureAwait(false);

        // Pre-check BEFORE the aggregate mutates so a saga disagreement can
        // never leave a mutated tracked entity behind on failure.
        if (trip.State == TripState.ReadyToStart && saga is not null && !saga.MayStartRide)
        {
            return TripLifecycleResult.Fail(trip, TripOutcome.Fail(
                TripErrorCode.IllegalTransition,
                $"Start requires the saga's authoritative authorization, but the saga is {saga.Status.ToCanonicalName()}/{saga.CurrentStep.ToCanonicalName()}."));
        }

        var outcome = trip.Start(driverId, reservationId, _time.GetUtcNow());
        if (!outcome.IsSuccess)
        {
            return TripLifecycleResult.Fail(trip, outcome);
        }

        // Start replay is already IN_PROGRESS: no mutation, skip the write.
        if (!outcome.IsReplay)
        {
            var now = _time.GetUtcNow();
            TripOutboxWriter.Enqueue(_db, TripSagaIntents.MarkDriverTripStarted(trip, now));
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        _log.LogInformation(
            "StartTrip {TripId} state {State} replay {IsReplay}.",
            trip.TripId,
            trip.State.ToCanonicalName(),
            outcome.IsReplay);

        return TripLifecycleResult.Success(trip, outcome.IsReplay, outcome.Compensation);
    }
}

/// <summary>
/// System-side CompleteTrip intake. No HTTP dependency.
/// </summary>
public sealed class CompleteTripHandler
{
    private readonly TripDbContext _db;
    private readonly TimeProvider _time;
    private readonly ILogger<CompleteTripHandler> _log;

    public CompleteTripHandler(TripDbContext db, TimeProvider time, ILogger<CompleteTripHandler> log)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(log);
        _db = db;
        _time = time;
        _log = log;
    }

    /// <summary>
    /// Completes the ride for the exact current assignment and dispatches the
    /// capture obligation (forward recovery begins).
    /// </summary>
    public async Task<TripLifecycleResult> HandleAsync(
        Guid tripId,
        Guid driverId,
        Guid reservationId,
        CancellationToken cancellationToken = default)
    {
        var trip = await _db.Trips
            .SingleOrDefaultAsync(t => t.TripId == tripId, cancellationToken)
            .ConfigureAwait(false);

        if (trip is null)
        {
            return TripLifecycleResult.NotFound();
        }

        var saga = await _db.RideSagas
            .SingleOrDefaultAsync(s => s.TripId == tripId, cancellationToken)
            .ConfigureAwait(false);

        // Pre-check BEFORE the aggregate mutates: capture follows only the
        // authorized, non-compensating ride phase.
        if (trip.State == TripState.InProgress && saga is not null && !saga.MayCompleteRide)
        {
            return TripLifecycleResult.Fail(trip, TripOutcome.Fail(
                TripErrorCode.IllegalTransition,
                $"Completion requires the saga's authorized ride phase but the saga is {saga.Status.ToCanonicalName()}/{saga.CurrentStep.ToCanonicalName()}."));
        }

        var outcome = trip.Complete(driverId, reservationId, _time.GetUtcNow());
        if (!outcome.IsSuccess)
        {
            return TripLifecycleResult.Fail(trip, outcome);
        }

        if (!outcome.IsReplay)
        {
            var now = _time.GetUtcNow();
            if (saga is not null)
            {
                var sagaOutcome = saga.RecordRideCompleted(RideSagaOperationKeys.Capture(tripId), now);
                if (!sagaOutcome.IsSuccess)
                {
                    throw new InvalidOperationException(
                        $"Saga rejected the completion step after phase verification: {sagaOutcome.ErrorCode} {sagaOutcome.Message}");
                }

                TripOutboxWriter.Enqueue(_db, TripSagaIntents.CapturePayment(trip, saga, now));
            }

            TripOutboxWriter.Enqueue(_db, TripSagaIntents.MarkDriverTripCompleted(trip, now));
            TripOutboxWriter.Enqueue(_db, TripNotificationIntents.Completed(trip, now));
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        _log.LogInformation(
            "CompleteTrip {TripId} state {State} capture {CaptureDispatched}.",
            trip.TripId,
            trip.State.ToCanonicalName(),
            saga?.CurrentStep.ToCanonicalName() ?? "NO_SAGA");

        return TripLifecycleResult.Success(trip, outcome.IsReplay, outcome.Compensation);
    }
}
