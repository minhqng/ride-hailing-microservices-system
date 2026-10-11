// StartMatching application handler (A-W05-01).
//
// Rider-scoped: only the owning Rider can open the matching round for their
// ride in the pre-broker skeleton (fail closed; missing and not-owned share
// NotFound per SEC-03). Loads the Trip, calls the single aggregate
// operation with the deterministic clock, persists the authoritative result,
// maps the domain outcome. No FSM logic here.

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RideHailing.TripService.Domain.Trips;
using RideHailing.TripService.Infrastructure.Persistence;

namespace RideHailing.TripService.Application.Trips.Lifecycle;

/// <summary>
/// Rider-scoped StartMatching intake. No HTTP dependency; the Rider identity
/// arrives as explicit trusted application input.
/// </summary>
public sealed class StartMatchingHandler
{
    private readonly TripDbContext _db;
    private readonly TimeProvider _time;
    private readonly ILogger<StartMatchingHandler> _log;

    public StartMatchingHandler(TripDbContext db, TimeProvider time, ILogger<StartMatchingHandler> log)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(log);
        _db = db;
        _time = time;
        _log = log;
    }

    /// <summary>
    /// Opens the first matching round for the owned Trip.
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

        var outcome = trip.StartMatching(_time.GetUtcNow());
        if (!outcome.IsSuccess)
        {
            return TripLifecycleResult.Fail(trip, outcome);
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _log.LogInformation(
            "StartMatching {TripId} state {State} round {MatchingRound} replay {IsReplay}.",
            trip.TripId,
            trip.State.ToCanonicalName(),
            trip.CurrentMatchingRound,
            outcome.IsReplay);

        return TripLifecycleResult.Success(trip, outcome.IsReplay, outcome.Compensation);
    }
}
