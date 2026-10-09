// Payment/matching-outcome system intake (A-W05-04, local authorization leg).
//
// Applies authoritative-shaped Payment authorization success, terminal
// Payment failure and Matching terminal failure to the Trip lifecycle. Trip
// never decides payment or matching truth: the caller supplies the
// authority's fact and the aggregate accepts it only from the legal source
// state for the current generation. Operation-key lineage, Saga steps,
// retry/reconciliation and broker wiring belong to W10+; this skeleton proves
// local lifecycle ordering (notably authorization-before-start, DSI-026, and
// terminal payment-failure compensation exposure) without them.

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RideHailing.TripService.Domain.Trips;
using RideHailing.TripService.Infrastructure.Persistence;

namespace RideHailing.TripService.Application.Trips.Lifecycle;

/// <summary>
/// System-side PaymentAuthorized intake (local lifecycle leg only).
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

        var outcome = trip.MarkPaymentAuthorized(_time.GetUtcNow());
        if (!outcome.IsSuccess)
        {
            return TripLifecycleResult.Fail(trip, outcome);
        }

        // Authorization replay is already current: no mutation, skip the write.
        if (!outcome.IsReplay)
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        _log.LogInformation(
            "PaymentAuthorized {TripId} state {State} replay {IsReplay}.",
            trip.TripId,
            trip.State.ToCanonicalName(),
            outcome.IsReplay);

        return TripLifecycleResult.Success(trip, outcome.IsReplay, outcome.Compensation);
    }
}

/// <summary>
/// System-side terminal Payment failure intake (local lifecycle leg only).
/// No HTTP route, no broker consumer, no operationKey: the caller supplies
/// the Payment authority's terminal-failure fact and the aggregate accepts
/// it only from DRIVER_ACCEPTED. The exact cleanup requirement travels on
/// the result for the future workflow layer; no release, void or dispatch
/// is executed here. Payment operation lineage and Saga compensation belong
/// to W10+.
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

        var outcome = trip.MarkPaymentFailed(_time.GetUtcNow());
        if (!outcome.IsSuccess)
        {
            return TripLifecycleResult.Fail(trip, outcome);
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _log.LogInformation(
            "PaymentFailed {TripId} state {State} compensation {Compensation}.",
            trip.TripId,
            trip.State.ToCanonicalName(),
            outcome.Compensation);

        return TripLifecycleResult.Success(trip, outcome.IsReplay, outcome.Compensation);
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

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _log.LogInformation(
            "MatchingFailed {TripId} state {State} round {MatchingRound}.",
            trip.TripId,
            trip.State.ToCanonicalName(),
            matchingRound);

        return TripLifecycleResult.Success(trip, outcome.IsReplay, outcome.Compensation);
    }
}
