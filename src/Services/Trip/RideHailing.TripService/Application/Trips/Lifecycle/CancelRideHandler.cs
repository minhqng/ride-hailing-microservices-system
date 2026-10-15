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
using RideHailing.TripService.Domain.Trips;
using RideHailing.TripService.Infrastructure.Persistence;

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

        var outcome = trip.Cancel(_time.GetUtcNow());
        if (!outcome.IsSuccess)
        {
            return TripLifecycleResult.Fail(trip, outcome);
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _log.LogInformation(
            "CancelRide {TripId} state {State} compensation {Compensation}.",
            trip.TripId,
            trip.State.ToCanonicalName(),
            outcome.Compensation);

        return TripLifecycleResult.Success(trip, outcome.IsReplay, outcome.Compensation);
    }
}
