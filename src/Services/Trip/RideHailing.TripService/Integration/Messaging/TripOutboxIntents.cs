// Trip semantic Outbox intents.
//
// One builder per committed business obligation. Each builder derives:
//   - a deterministic messageId from the exact business identity (never a
//     fresh Guid per attempt), so an uncertain publish/restart republishes
//     the same logical message;
//   - payload serialized from the committed aggregate/cleanup values;
//   - the canonical envelope and transport target.
//
// These intents MUST be added to the same TripDbContext.SaveChangesAsync as
// the Trip state transition they describe. The shared relay is the only
// publisher.

using RideHailing.Messaging.Contracts;
using RideHailing.Messaging.Outbox;
using RideHailing.Messaging.Topology;
using RideHailing.TripService.Domain.Trips;
using RideHailing.TripService.Integration.Matching;

namespace RideHailing.TripService.Integration.Messaging;

/// <summary>Builds Trip-owned durable intents from committed business identities.</summary>
public static class TripOutboxIntents
{
    public const string TripAggregateType = "Trip";
    public const string RideRequestedType = "RideRequestedV1";
    public const string CancelMatchingRoundType = "CancelMatchingRoundV1";
    public const string ReleaseReservationType = "ReleaseDriverReservationV1";

    public const string RideRequestedRoutingKey = "trip.ride-requested.v1";
    public const string CancelMatchingRoundRoutingKey = "matching.cancel-round.v1";
    public const string ReleaseReservationRoutingKey = "driver.release-reservation.v1";

    /// <summary>
    /// Stable RideRequested identity for one opened matching generation. The
    /// format is frozen and retained so an upgrade cannot change the logical
    /// message of an already committed round.
    /// </summary>
    public static string RideRequestedMessageId(Trip trip)
    {
        ArgumentNullException.ThrowIfNull(trip);
        return $"trip:{trip.TripId:N}:ride-requested:{trip.CurrentMatchingRound}";
    }

    /// <summary>
    /// Stable CancelMatchingRound identity for one cancelled generation.
    /// </summary>
    public static string CancelMatchingRoundMessageId(Guid tripId, int matchingRound) =>
        $"trip:{tripId:N}:cancel-round:{matchingRound}";

    /// <summary>
    /// Stable ReleaseDriverReservation identity for one exact reservation.
    /// </summary>
    public static string ReleaseReservationMessageId(Guid tripId, Guid reservationId) =>
        $"trip:{tripId:N}:release:{reservationId:N}";

    /// <summary>
    /// RideRequestedV1 for a just-applied StartMatching transition. The caller
    /// must persist the returned intent in the same SaveChanges.
    /// </summary>
    public static OutboxMessage RideRequested(Trip trip, DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(trip);
        if (trip.State != TripState.Matching)
        {
            throw new InvalidOperationException(
                $"RideRequested requires a MATCHING Trip but Trip {trip.TripId:D} is {trip.State.ToCanonicalName()}.");
        }

        var messageId = RideRequestedMessageId(trip);
        var envelope = MessageEnvelope<RideRequestedV1>.Create(
            RideRequestedType,
            trip.TripId.ToString("D"),
            RideRequestedV1.FromTrip(trip, occurredAt),
            messageId: messageId,
            occurredAt: occurredAt,
            aggregateType: TripAggregateType,
            aggregateId: trip.TripId.ToString("D"),
            aggregateVersion: trip.Version,
            operationKey: messageId);
        return OutboxMessage.Create(envelope, RideTopology.EventsExchange, RideRequestedRoutingKey, now: occurredAt);
    }

    /// <summary>
    /// CancelMatchingRoundV1 for one persisted cleanup obligation. The caller
    /// must persist it in the same SaveChanges as the Trip transition.
    /// </summary>
    public static OutboxMessage CancelMatchingRound(Trip trip, TripCleanupIntent intent, DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(trip);
        ArgumentNullException.ThrowIfNull(intent);
        if (intent.Kind != TripCleanupKind.CancelMatching)
        {
            throw new ArgumentException("Intent is not a matching cancellation obligation.", nameof(intent));
        }
        if (intent.TripId != trip.TripId || intent.MatchingRound < 1 ||
            intent.MatchingRound > trip.CurrentMatchingRound)
        {
            throw new InvalidOperationException("Cleanup identity does not belong to the current Trip generation.");
        }

        var messageId = CancelMatchingRoundMessageId(trip.TripId, intent.MatchingRound);
        var envelope = MessageEnvelope<CancelMatchingRoundV1>.Create(
            CancelMatchingRoundType,
            trip.TripId.ToString("D"),
            new CancelMatchingRoundV1 { TripId = trip.TripId, MatchingRound = intent.MatchingRound },
            messageId: messageId,
            occurredAt: occurredAt,
            aggregateType: TripAggregateType,
            aggregateId: trip.TripId.ToString("D"),
            aggregateVersion: trip.Version,
            operationKey: messageId);
        return OutboxMessage.Create(envelope, RideTopology.CommandsExchange,
            CancelMatchingRoundRoutingKey, now: occurredAt);
    }

    /// <summary>
    /// ReleaseDriverReservationV1 for one exact persisted cleanup obligation.
    /// </summary>
    public static OutboxMessage ReleaseReservation(Trip trip, TripCleanupIntent intent, DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(trip);
        ArgumentNullException.ThrowIfNull(intent);
        if (intent.Kind != TripCleanupKind.ReleaseReservation)
        {
            throw new ArgumentException("Intent is not a reservation release obligation.", nameof(intent));
        }
        if (intent.TripId != trip.TripId || intent.DriverId is null || intent.ReservationId is null ||
            intent.DriverId == Guid.Empty || intent.ReservationId == Guid.Empty || intent.MatchingRound < 1)
        {
            throw new InvalidOperationException("Release requires the exact owned Trip/Driver/reservation identities.");
        }

        var messageId = ReleaseReservationMessageId(trip.TripId, intent.ReservationId.Value);
        var envelope = MessageEnvelope<ReleaseDriverReservationV1>.Create(
            ReleaseReservationType,
            trip.TripId.ToString("D"),
            new ReleaseDriverReservationV1
            {
                TripId = trip.TripId,
                DriverId = intent.DriverId.Value,
                ReservationId = intent.ReservationId.Value,
                MatchingRound = intent.MatchingRound,
            },
            messageId: messageId,
            occurredAt: occurredAt,
            aggregateType: TripAggregateType,
            aggregateId: trip.TripId.ToString("D"),
            aggregateVersion: trip.Version,
            operationKey: messageId);
        return OutboxMessage.Create(envelope, RideTopology.CommandsExchange,
            ReleaseReservationRoutingKey, now: occurredAt);
    }

    /// <summary>
    /// Stages every newly added cleanup obligation that has a wire contract.
    /// Payment reconciliation is durable Trip intent, but its command belongs
    /// to the payment flow, so it is intentionally not published here.
    /// </summary>
    public static void EnqueueNewCleanup(
        Trip trip,
        IReadOnlySet<string> preexistingKeys,
        Action<OutboxMessage> enqueue)
    {
        ArgumentNullException.ThrowIfNull(trip);
        ArgumentNullException.ThrowIfNull(preexistingKeys);
        ArgumentNullException.ThrowIfNull(enqueue);

        foreach (var intent in trip.CleanupIntents)
        {
            if (preexistingKeys.Contains(intent.Key)) continue;
            switch (intent.Kind)
            {
                case TripCleanupKind.CancelMatching:
                    enqueue(CancelMatchingRound(trip, intent, intent.CreatedAt));
                    break;
                case TripCleanupKind.ReleaseReservation:
                    enqueue(ReleaseReservation(trip, intent, intent.CreatedAt));
                    break;
                case TripCleanupKind.ReconcilePayment:
                    // Payment operation lineage and commands belong to the payment flow.
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(intent), intent.Kind, "Unknown cleanup kind.");
            }
        }
    }
}
