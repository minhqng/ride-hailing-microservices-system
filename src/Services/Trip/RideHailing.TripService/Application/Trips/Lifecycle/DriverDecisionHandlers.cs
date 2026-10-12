// Driver-decision system intake (A-W05-03 accept/reject proof).
//
// Applies a Driver accept or validated rejection for the Trip's current
// assignment. The supplied driver/reservation identity is fenced against the
// authoritative current assignment by the aggregate: a wrong driver, a stale
// reservation, or a wrong lifecycle state changes nothing. Driver transport
// authentication belongs to the future edge/middleware owner; this skeleton
// proves assignment fencing, not actor authentication.

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RideHailing.TripService.Domain.Trips;
using RideHailing.TripService.Infrastructure.Persistence;

namespace RideHailing.TripService.Application.Trips.Lifecycle;

/// <summary>
/// System-side DriverAccepted intake. No HTTP dependency.
/// </summary>
public sealed class RecordDriverAcceptedHandler
{
    private readonly TripDbContext _db;
    private readonly TimeProvider _time;
    private readonly ILogger<RecordDriverAcceptedHandler> _log;

    public RecordDriverAcceptedHandler(TripDbContext db, TimeProvider time, ILogger<RecordDriverAcceptedHandler> log)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(log);
        _db = db;
        _time = time;
        _log = log;
    }

    /// <summary>
    /// Records Driver acceptance for the exact current assignment.
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

        var outcome = trip.RecordDriverAccepted(driverId, reservationId, _time.GetUtcNow());
        if (!outcome.IsSuccess)
        {
            return TripLifecycleResult.Fail(trip, outcome);
        }

        // Exact acceptance replay is already current (DSI-008): no mutation,
        // so skip the write and keep Version/UpdatedAt stable.
        if (!outcome.IsReplay)
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        _log.LogInformation(
            "DriverAccepted {TripId} state {State} replay {IsReplay}.",
            trip.TripId,
            trip.State.ToCanonicalName(),
            outcome.IsReplay);

        return TripLifecycleResult.Success(trip, outcome.IsReplay, outcome.Compensation);
    }
}

/// <summary>
/// System-side DriverRejected intake. Validates the rejection against the
/// current assignment and exposes the guarded cleanup requirement; Trip
/// state is intentionally unchanged (guarded release + rematch belong to
/// future workflow work).
/// </summary>
public sealed class RecordDriverRejectedHandler
{
    private readonly TripDbContext _db;
    private readonly ILogger<RecordDriverRejectedHandler> _log;

    public RecordDriverRejectedHandler(TripDbContext db, ILogger<RecordDriverRejectedHandler> log)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(log);
        _db = db;
        _log = log;
    }

    /// <summary>
    /// Validates a Driver rejection for the exact current assignment.
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

        var outcome = trip.RecordDriverRejected(driverId, reservationId);
        if (!outcome.IsSuccess)
        {
            return TripLifecycleResult.Fail(trip, outcome);
        }

        _log.LogInformation(
            "DriverRejected {TripId} state {State} compensation {Compensation}.",
            trip.TripId,
            trip.State.ToCanonicalName(),
            outcome.Compensation);

        return TripLifecycleResult.Success(trip, outcome.IsReplay, outcome.Compensation);
    }
}
