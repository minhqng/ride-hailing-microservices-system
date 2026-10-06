// Start/complete ride system intake (local lifecycle close-out).
//
// Applies Driver-actor start/completion evidence to the Trip lifecycle. The
// supplied driver/reservation identity is fenced against the authoritative
// current assignment by the aggregate (DSI-011, DSI-012); authorization
// ordering is enforced by lifecycle state (DSI-026: READY_TO_START is the
// local proof that authorization was recorded). Driver transport
// authentication belongs to the future edge/middleware owner; this skeleton
// proves assignment fencing and terminal monotonicity, not actor
// authentication.

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RideHailing.TripService.Domain.Trips;
using RideHailing.TripService.Infrastructure.Persistence;

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
    /// Starts the ride for the exact current assignment.
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

        var outcome = trip.Start(driverId, reservationId, _time.GetUtcNow());
        if (!outcome.IsSuccess)
        {
            return TripLifecycleResult.Fail(trip, outcome);
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

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
    /// Completes the ride for the exact current assignment. Trip completion
    /// never mutates Payment authority (forward recovery belongs to W10+).
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

        var outcome = trip.Complete(driverId, reservationId, _time.GetUtcNow());
        if (!outcome.IsSuccess)
        {
            return TripLifecycleResult.Fail(trip, outcome);
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _log.LogInformation(
            "CompleteTrip {TripId} state {State}.",
            trip.TripId,
            trip.State.ToCanonicalName());

        return TripLifecycleResult.Success(trip, outcome.IsReplay, outcome.Compensation);
    }
}
