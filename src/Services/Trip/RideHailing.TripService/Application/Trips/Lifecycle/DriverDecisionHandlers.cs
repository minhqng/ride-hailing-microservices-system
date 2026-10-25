// Driver-decision system intake (A-W05-03 accept/reject proof, A-W10-02
// accept→authorize saga step).
//
// Applies a Driver accept or validated rejection for the Trip's current
// assignment. The supplied driver/reservation identity is fenced against the
// authoritative current assignment by the aggregate: a wrong driver, a stale
// reservation, or a wrong lifecycle state changes nothing. Driver transport
// authentication belongs to the future edge/middleware owner.
//
// W10: the FIRST applied acceptance starts the persisted ride saga and
// stages the AuthorizePayment(K) outbox intent in the SAME transaction
// (Trip DRIVER_ACCEPTED + saga AUTHORIZE_PENDING + intent = one commit).
// Duplicate acceptance replays and never creates a second authorization:
// the saga already holds the step, and the deterministic authorize messageId
// is a second fence at the outbox primary key.

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RideHailing.TripService.Domain.Sagas;
using RideHailing.TripService.Domain.Trips;
using RideHailing.TripService.Infrastructure.Persistence;
using RideHailing.TripService.Integration.Messaging;
using RideHailing.TripService.Integration.Payment;

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
    /// Records Driver acceptance for the exact current assignment and starts
    /// the payment authorization saga step on first application.
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

        // The payment leg must be quotable BEFORE any mutation: a frozen fare
        // that cannot produce a positive payment amount could never dispatch
        // AuthorizePayment, so acceptance is rejected up front instead of
        // mutating a Trip whose saga step would permanently fail (A-W10-02).
        try
        {
            _ = PaymentAmounts.ToMinorUnits(trip.Fare);
        }
        catch (InvalidOperationException)
        {
            return TripLifecycleResult.Fail(trip, TripOutcome.Fail(
                TripErrorCode.PaymentQuoteRequired,
                "The frozen fare cannot quote a positive payment amount; the payment saga cannot start."));
        }

        var outcome = trip.RecordDriverAccepted(driverId, reservationId, _time.GetUtcNow());
        if (!outcome.IsSuccess)
        {
            return TripLifecycleResult.Fail(trip, outcome);
        }

        // Exact acceptance replay is already current (DSI-008): no mutation,
        // so skip the write and keep Version/UpdatedAt stable. A replay
        // without a saga row is a pre-W10 Trip: its payment lineage cannot be
        // invented post-hoc from a duplicate acceptance.
        if (!outcome.IsReplay)
        {
            if (saga is not null)
            {
                throw new InvalidOperationException(
                    $"Trip {tripId:D} accepted a second assignment generation while a saga already exists; persisted state violates the one-current-saga invariant.");
            }

            var now = _time.GetUtcNow();
            saga = RideSaga.Start(Guid.NewGuid(), tripId, reservationId,
                RideSagaOperationKeys.Authorize(tripId), now);
            _db.RideSagas.Add(saga);
            TripOutboxWriter.Enqueue(_db, TripSagaIntents.AuthorizePayment(trip, saga, now));
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        _log.LogInformation(
            "DriverAccepted {TripId} state {State} replay {IsReplay} saga {SagaId}.",
            trip.TripId,
            trip.State.ToCanonicalName(),
            outcome.IsReplay,
            saga?.SagaId);

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
