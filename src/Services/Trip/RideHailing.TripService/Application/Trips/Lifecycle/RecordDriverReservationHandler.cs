// Driver-reservation system intake (A-W05-03, A-W06 replay fencing).
//
// Applies an authoritative-shaped Driver reservation result to the Trip's
// assignment reference. Trip never decides reservation success: the caller
// supplies the Driver authority's fact (trip, driver, reservation, round)
// and the aggregate accepts it only when it is the current, legal,
// assignment-free generation. Production wiring (envelope, dedup, broker,
// Driver authority call path) belongs to W8+; this skeleton intake is
// exercised by component/system tests with authoritative-shaped fixtures.

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RideHailing.TripService.Domain.Trips;
using RideHailing.TripService.Infrastructure.Persistence;

namespace RideHailing.TripService.Application.Trips.Lifecycle;

/// <summary>
/// System-side DriverReserved intake. No HTTP dependency.
/// </summary>
public sealed class RecordDriverReservationHandler
{
    private readonly TripDbContext _db;
    private readonly TimeProvider _time;
    private readonly ILogger<RecordDriverReservationHandler> _log;

    public RecordDriverReservationHandler(TripDbContext db, TimeProvider time, ILogger<RecordDriverReservationHandler> log)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(log);
        _db = db;
        _time = time;
        _log = log;
    }

    /// <summary>
    /// Records the supplied reservation fact against the Trip's current round.
    /// </summary>
    public async Task<TripLifecycleResult> HandleAsync(
        Guid tripId,
        Guid driverId,
        Guid reservationId,
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

        var outcome = trip.State is TripState.Cancelled or TripState.MatchFailed
            ? trip.RecordPreStartTerminalReservation(driverId, reservationId, matchingRound, _time.GetUtcNow())
            : trip.ApplyDriverReserved(driverId, reservationId, matchingRound, _time.GetUtcNow());
        if (!outcome.IsSuccess)
        {
            return TripLifecycleResult.Fail(trip, outcome);
        }

        // Same authoritative reservation replay performs no mutation (DSI-008):
        // skip the write so Version/UpdatedAt stay untouched and a losing
        // concurrent writer can never turn a replay into an overwrite.
        if (!outcome.IsReplay)
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        _log.LogInformation(
            "DriverReserved {TripId} state {State} driver {DriverId} reservation {ReservationId} round {MatchingRound} replay {IsReplay}.",
            trip.TripId,
            trip.State.ToCanonicalName(),
            driverId,
            reservationId,
            matchingRound,
            outcome.IsReplay);

        return TripLifecycleResult.Success(trip, outcome.IsReplay, outcome.Compensation);
    }
}
