// Trip RideRequested intake (A-W09-02/04, replaces W8 direct publication).
//
// RESPONSIBILITY. CreateRide commits the authoritative CREATED row. This
// component then opens the first matching generation and stages
// RideRequestedV1 in the SAME SaveChanges, so MATCHING can never commit
// without its durable outgoing intent. It NEVER contacts RabbitMQ.
//
// CONSEQUENCES.
//   - A broker outage cannot fail the HTTP create path: the local intent is
//     already durable and the C-owned relay publishes it later.
//   - A crash after the HTTP response leaves a pending row; relay restart
//     publishes the same stable messageId.
//   - An idempotent HTTP replay of an already-MATCHING Trip does no work and
//     therefore cannot create a second logical message.
//   - With TripMessaging disabled the historical W4/W5 behaviour is preserved:
//     the ride stays CREATED and no intent is staged.
//
// A concurrent duplicate HTTP request can lose the application-managed Version
// fence. That is not a lost intent: the winner committed MATCHING + Outbox in
// one transaction, so the loser reloads, observes the winner's committed
// state and succeeds without writing.

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RideHailing.TripService.Domain.Trips;
using RideHailing.TripService.Infrastructure.Persistence;

namespace RideHailing.TripService.Integration.Messaging;

/// <summary>Atomically opens matching and durably records RideRequestedV1.</summary>
public sealed class TripRideRequestedIntake(
    TripDbContext db,
    TripMessagingSettings settings,
    TimeProvider time,
    ILogger<TripRideRequestedIntake> log)
{
    public async Task<bool> EnsureRideRequestedAsync(Guid tripId, Guid riderId, CancellationToken token)
    {
        if (!settings.Enabled) return true;

        // A concurrent same-key request may win after this context loaded the
        // row. One reload is enough: the winner's transaction is indivisible.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            db.ChangeTracker.Clear();
            var trip = await db.Trips.SingleOrDefaultAsync(t => t.TripId == tripId, token);
            if (trip is null || trip.RiderId != riderId) return false;

            // No outgoing work remains on an already progressed/terminal ride.
            if (trip.State is not (TripState.Created or TripState.Matching)) return true;

            // MATCHING already owns its committed intent (W9) or predates the
            // upgrade and is not republished by HTTP replay.
            if (trip.State == TripState.Matching) return true;

            var outcome = trip.StartMatching(time.GetUtcNow());
            if (!outcome.IsSuccess) return false;

            if (!outcome.IsReplay)
            {
                TripOutboxWriter.Enqueue(db, TripOutboxIntents.RideRequested(trip, trip.UpdatedAt));
                try
                {
                    await db.SaveChangesAsync(token);
                    log.LogInformation(
                        "RideRequested committed to Outbox for trip {TripId} round {MatchingRound} message {MessageId}.",
                        trip.TripId, trip.CurrentMatchingRound, TripOutboxIntents.RideRequestedMessageId(trip));
                }
                catch (TripConcurrencyConflictException) when (attempt == 0)
                {
                    log.LogInformation(
                        "RideRequested intake lost the Version fence for trip {TripId}; observing the winner.",
                        tripId);
                    continue;
                }
            }

            return true;
        }

        return false;
    }
}
