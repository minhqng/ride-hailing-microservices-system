// Payment/matching-outcome system intake (A-W05-04 local leg, A-W10-03/04
// saga-aware).
//
// Applies authoritative-shaped Payment authorization success, terminal
// Payment failure and Matching terminal failure to the Trip lifecycle. Trip
// never decides payment truth: the trusted system caller supplies the
// authority's fact. Since W10 the same PaymentOutcomeIntake core drives this
// path and the broker consumer, so a saga (when present) advances with the
// Trip transition and every outgoing obligation (void/cleanup intents)
// commits atomically. A saga-less Trip keeps the W5 local lifecycle leg.

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RideHailing.TripService.Domain.Sagas;
using RideHailing.TripService.Domain.Trips;
using RideHailing.TripService.Infrastructure.Persistence;
using RideHailing.TripService.Integration.Messaging;

namespace RideHailing.TripService.Application.Trips.Lifecycle;

/// <summary>
/// System-side PaymentAuthorized intake (trusted authority fact).
/// </summary>
public sealed class MarkPaymentAuthorizedHandler
{
    private readonly TripDbContext _db;
    private readonly TimeProvider _time;
    private readonly ILogger<MarkPaymentAuthorizedHandler> _log;

    public MarkPaymentAuthorizedHandler(TripDbContext db, TimeProvider time, ILogger<MarkPaymentAuthorizedHandler> log)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(log);
        _db = db;
        _time = time;
        _log = log;
    }

    /// <summary>
    /// Records authoritative payment-authorization success for the Trip.
    /// </summary>
    public async Task<TripLifecycleResult> HandleAsync(Guid tripId, CancellationToken cancellationToken = default)
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

        if (saga is null)
        {
            var outcome = trip.State == TripState.Cancelled
                ? trip.RecordCancelledPaymentAuthorization(_time.GetUtcNow())
                : trip.MarkPaymentAuthorized(_time.GetUtcNow());
            if (!outcome.IsSuccess)
            {
                return TripLifecycleResult.Fail(trip, outcome);
            }

            if (!outcome.IsReplay)
            {
                await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }

            _log.LogInformation(
                "PaymentAuthorized {TripId} state {State} replay {IsReplay} (no saga lineage).",
                trip.TripId, trip.State.ToCanonicalName(), outcome.IsReplay);
            return TripLifecycleResult.Success(trip, outcome.IsReplay, outcome.Compensation);
        }

        var preexistingCleanup = TripOutboxWriter.CleanupKeys(trip);
        var applied = PaymentOutcomeIntake.Apply(
            trip, saga,
            new PaymentOutcomeFact("PaymentAuthorizedV1", saga.AuthorizeOperationKey,
                saga.SagaId.ToString("D"), "APPROVED"),
            _time.GetUtcNow(), _db);
        if (applied.Disposition is "NeedsReview" or "PendingWorkflow")
        {
            return TripLifecycleResult.Fail(trip, TripOutcome.Fail(
                TripErrorCode.IllegalTransition, $"Payment outcome not applicable: {applied.Reason}."));
        }

        TripOutboxWriter.EnqueueNewCleanup(_db, trip, preexistingCleanup);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        _log.LogInformation(
            "PaymentAuthorized {TripId} state {State} saga {SagaStep} result {Disposition}.",
            trip.TripId, trip.State.ToCanonicalName(), saga.CurrentStep.ToCanonicalName(), applied.Disposition);
        return TripLifecycleResult.Success(trip, applied.Disposition == "BusinessReplay", TripCompensation.None);
    }
}

/// <summary>
/// System-side terminal Payment failure intake (trusted authority fact: the
/// caller asserts a conclusive DECLINED-style outcome, never an uncertain
/// one). Pairs the Trip cancellation with the saga compensation step and the
/// exact release/cancel-round intents in one commit.
/// </summary>
public sealed class MarkPaymentFailedHandler
{
    private readonly TripDbContext _db;
    private readonly TimeProvider _time;
    private readonly ILogger<MarkPaymentFailedHandler> _log;

    public MarkPaymentFailedHandler(TripDbContext db, TimeProvider time, ILogger<MarkPaymentFailedHandler> log)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(log);
        _db = db;
        _time = time;
        _log = log;
    }

    /// <summary>
    /// Records authoritative terminal Payment failure for the Trip.
    /// </summary>
    public async Task<TripLifecycleResult> HandleAsync(Guid tripId, CancellationToken cancellationToken = default)
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

        if (saga is null)
        {
            var outcome = trip.MarkPaymentFailed(_time.GetUtcNow());
            if (!outcome.IsSuccess)
            {
                return TripLifecycleResult.Fail(trip, outcome);
            }

            if (!outcome.IsReplay)
            {
                TripOutboxWriter.Enqueue(_db,
                    TripNotificationIntents.PaymentFailed(trip, "DECLINED", trip.UpdatedAt));
                await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            _log.LogInformation(
                "PaymentFailed {TripId} state {State} compensation {Compensation} (no saga lineage).",
                trip.TripId, trip.State.ToCanonicalName(), outcome.Compensation);
            return TripLifecycleResult.Success(trip, outcome.IsReplay, outcome.Compensation);
        }

        var preexistingCleanup = TripOutboxWriter.CleanupKeys(trip);
        var wasAccepted = trip.State == TripState.DriverAccepted;
        var applied = PaymentOutcomeIntake.Apply(
            trip, saga,
            new PaymentOutcomeFact("PaymentAuthorizationFailedV1", saga.AuthorizeOperationKey,
                saga.SagaId.ToString("D"), "DECLINED"),
            _time.GetUtcNow(), _db);
        if (applied.Disposition is "NeedsReview" or "PendingWorkflow")
        {
            return TripLifecycleResult.Fail(trip, TripOutcome.Fail(
                TripErrorCode.IllegalTransition, $"Payment failure not applicable: {applied.Reason}."));
        }

        // The cancellation created by the intake adds the exact release and
        // cancel-round wire obligations; stage them in this same unit of work.
        // Only a failure that actually cancelled an accepted ride produces the
        // Rider-facing payment-failure notification; a late failure on a ride
        // the Rider already cancelled does not relabel that cancellation.
        TripOutboxWriter.EnqueueNewCleanup(_db, trip, preexistingCleanup);
        if (wasAccepted && trip.State == TripState.Cancelled)
        {
            TripOutboxWriter.Enqueue(_db,
                TripNotificationIntents.PaymentFailed(trip, "DECLINED", trip.UpdatedAt));
        }
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        _log.LogInformation(
            "PaymentFailed {TripId} state {State} saga {SagaStatus}/{SagaStep}.",
            trip.TripId, trip.State.ToCanonicalName(), saga.Status.ToCanonicalName(), saga.CurrentStep.ToCanonicalName());
        return TripLifecycleResult.Success(
            trip,
            applied.Disposition == "BusinessReplay",
            TripCompensation.ReleaseReservation | TripCompensation.CancelMatching);
    }
}

/// <summary>
/// System-side MatchingFailed intake (current-round fenced).
/// </summary>
public sealed class MarkMatchingFailedHandler
{
    private readonly TripDbContext _db;
    private readonly TimeProvider _time;
    private readonly ILogger<MarkMatchingFailedHandler> _log;

    public MarkMatchingFailedHandler(TripDbContext db, TimeProvider time, ILogger<MarkMatchingFailedHandler> log)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(log);
        _db = db;
        _time = time;
        _log = log;
    }

    /// <summary>
    /// Records terminal matching failure for the Trip's current round. Stale
    /// rounds never fail an advanced Trip.
    /// </summary>
    public async Task<TripLifecycleResult> HandleAsync(
        Guid tripId,
        int matchingRound,
        CancellationToken cancellationToken = default)
    {
        var trip = await _db.Trips
            .SingleOrDefaultAsync(t => t.TripId == tripId, cancellationToken)
            .ConfigureAwait(false);

        if (trip is null)
        {
            return TripLifecycleResult.NotFound();
        }

        var outcome = trip.MarkMatchingFailed(matchingRound, _time.GetUtcNow());
        if (!outcome.IsSuccess)
        {
            return TripLifecycleResult.Fail(trip, outcome);
        }

        TripOutboxWriter.Enqueue(_db, TripNotificationIntents.NoDriver(trip, trip.UpdatedAt));
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _log.LogInformation(
            "MatchingFailed {TripId} state {State} round {MatchingRound}.",
            trip.TripId,
            trip.State.ToCanonicalName(),
            matchingRound);

        return TripLifecycleResult.Success(trip, outcome.IsReplay, outcome.Compensation);
    }
}
