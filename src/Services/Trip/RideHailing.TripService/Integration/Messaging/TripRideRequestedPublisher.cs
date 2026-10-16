using Microsoft.EntityFrameworkCore;
using RideHailing.Messaging.Contracts;
using RideHailing.Messaging.Reliability;
using RideHailing.Messaging.Topology;
using RideHailing.TripService.Domain.Trips;
using RideHailing.TripService.Infrastructure.Persistence;
using RideHailing.TripService.Integration.Matching;

namespace RideHailing.TripService.Integration.Messaging;

/// <summary>W8 direct publication. W9 Outbox must close the commit/publish gap.</summary>
public sealed class TripRideRequestedPublisher(TripDbContext db, TripMessagingSettings settings,
    IServiceProvider services, TimeProvider time, ILogger<TripRideRequestedPublisher> log)
{
    public async Task<bool> PublishForOwnedRideAsync(Guid tripId, Guid riderId, CancellationToken token)
    {
        if (!settings.Enabled) return true;
        if (!services.GetRequiredService<TripMessagingReadiness>().Ready) return false;
        try
        {
            // CreateRide has already committed. Reload on both new requests and
            // idempotent HTTP retries, including concurrent lifecycle changes.
            db.ChangeTracker.Clear();
            var trip = await db.Trips.SingleOrDefaultAsync(t => t.TripId == tripId, token);
            if (trip is null || trip.RiderId != riderId) return false;
            if (trip.State == TripState.Created)
            {
                var outcome = trip.StartMatching(time.GetUtcNow());
                if (!outcome.IsSuccess) return false;
                await db.SaveChangesAsync(token);
                // PostgreSQL stores microsecond precision. Re-read persisted time
                // before the first publish so a retry after restart has identical bytes.
                db.ChangeTracker.Clear();
                trip = await db.Trips.SingleAsync(t => t.TripId == tripId, token);
            }
            // A progressed/terminal Trip must not start a new round on HTTP replay.
            if (trip.State != TripState.Matching) return true;
            var envelope = CreateEnvelope(trip);
            await services.GetRequiredService<ConfirmedPublisher>().PublishAsync(
                RideTopology.EventsExchange, "trip.ride-requested.v1", envelope, token);
            log.LogInformation("RideRequested confirmed {MessageId} trip {TripId} correlation {CorrelationId}",
                envelope.MessageId, tripId, envelope.CorrelationId);
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is TripConcurrencyConflictException or Npgsql.NpgsqlException or
            DbUpdateException or IOException or TimeoutException or OperationCanceledException or RabbitMQ.Client.Exceptions.RabbitMQClientException)
        {
            log.LogWarning("RideRequested publication unconfirmed for {TripId} ({ErrorType}); replay the same request key.",
                tripId, ex.GetType().Name);
            return false;
        }
    }

    public static MessageEnvelope<RideRequestedV1> CreateEnvelope(Trip trip)
    {
        // W8 only opens round 1. While MATCHING, no other accepted transition
        // changes UpdatedAt; therefore HTTP retries reconstruct identical content.
        // A future rematch must persist a new publication intent in the W9 Outbox.
        return MessageEnvelope<RideRequestedV1>.Create("RideRequestedV1", trip.TripId.ToString("D"),
            RideRequestedV1.FromTrip(trip, trip.UpdatedAt),
            messageId: $"trip:{trip.TripId:N}:ride-requested:{trip.CurrentMatchingRound}",
            occurredAt: trip.UpdatedAt, aggregateType: "Trip", aggregateId: trip.TripId.ToString("D"),
            aggregateVersion: trip.Version);
    }
}
