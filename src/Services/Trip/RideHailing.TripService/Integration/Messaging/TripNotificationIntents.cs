// Rider notification intents.
//
// Each builder derives a deterministic message identity from the exact
// logical notification, serializes the recipient-addressed intent from the
// committed aggregate and targets the Trip-owned Events routing key. The
// caller MUST persist the returned intent in the same SaveChanges as the
// business transition it describes.

using RideHailing.Messaging.Contracts;
using RideHailing.Messaging.Outbox;
using RideHailing.Messaging.Topology;
using RideHailing.TripService.Domain.Trips;

namespace RideHailing.TripService.Integration.Messaging;

public static class TripNotificationIntents
{
    private const string AggregateType = "Trip";

    public static string RideRequestedMessageId(Guid tripId, int matchingRound) =>
        $"trip:{tripId:N}:notify:rider:requested:{matchingRound}";

    public static string DriverAssignedMessageId(Guid tripId, Guid reservationId) =>
        $"trip:{tripId:N}:notify:rider:assigned:{reservationId:N}";

    public static string DriverRejectedMessageId(Guid tripId, Guid reservationId) =>
        $"trip:{tripId:N}:notify:rider:rejected:{reservationId:N}";

    public static string NoDriverMessageId(Guid tripId, int matchingRound) =>
        $"trip:{tripId:N}:notify:rider:no-driver:{matchingRound}";

    public static string CompletedMessageId(Guid tripId, Guid reservationId) =>
        $"trip:{tripId:N}:notify:rider:completed:{reservationId:N}";

    public static string CancelledMessageId(Guid tripId, int matchingRound) =>
        $"trip:{tripId:N}:notify:rider:cancelled:{matchingRound}";

    public static string PaymentFailedMessageId(Guid tripId, int matchingRound) =>
        $"trip:{tripId:N}:notify:rider:payment-failed:{matchingRound}";

    /// <summary>Ride request created: the first matching round is open.</summary>
    public static OutboxMessage RideRequested(Trip trip, DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(trip);
        if (trip.State != TripState.Matching || trip.Assignment is not null)
            throw new InvalidOperationException(
                $"RideRequested notification requires an unassigned MATCHING Trip but Trip {trip.TripId:D} is {trip.State.ToCanonicalName()}.");
        return Build(trip, NotificationMessage.Types.RiderRideRequested,
            RideRequestedMessageId(trip.TripId, trip.CurrentMatchingRound), occurredAt);
    }

    /// <summary>A Driver reservation was applied to the current round.</summary>
    public static OutboxMessage DriverAssigned(Trip trip, DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(trip);
        if (trip.State != TripState.DriverReserved || trip.Assignment is null)
            throw new InvalidOperationException(
                $"DriverAssigned notification requires a DRIVER_RESERVED Trip with an assignment but Trip {trip.TripId:D} is {trip.State.ToCanonicalName()}.");
        return Build(trip, NotificationMessage.Types.RiderDriverAssigned,
            DriverAssignedMessageId(trip.TripId, trip.Assignment.ReservationId), occurredAt);
    }

    /// <summary>The exact current Driver rejected the assignment.</summary>
    public static OutboxMessage DriverRejected(
        Trip trip, Guid reservationId, DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(trip);
        if (trip.State != TripState.MatchFailed ||
            trip.FailureReason != TripFailureReason.DriverRejected ||
            reservationId == Guid.Empty)
            throw new InvalidOperationException(
                $"DriverRejected notification requires MATCH_FAILED/DRIVER_REJECTED but Trip {trip.TripId:D} is {trip.State.ToCanonicalName()}.");
        return Build(trip, NotificationMessage.Types.RiderDriverRejected,
            DriverRejectedMessageId(trip.TripId, reservationId), occurredAt,
            reasonCode: "DRIVER_REJECTED");
    }

    /// <summary>Matching exhausted its bounded candidates without a Driver.</summary>
    public static OutboxMessage NoDriver(Trip trip, DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(trip);
        if (trip.State != TripState.MatchFailed)
            throw new InvalidOperationException(
                $"NoDriver notification requires MATCH_FAILED but Trip {trip.TripId:D} is {trip.State.ToCanonicalName()}.");
        return Build(trip, NotificationMessage.Types.RiderNoDriver,
            NoDriverMessageId(trip.TripId, trip.CurrentMatchingRound), occurredAt,
            reasonCode: trip.FailureReason is { } reason ? reason.ToCanonicalName() : null);
    }

    /// <summary>The ride reached COMPLETED for the current assignment.</summary>
    public static OutboxMessage Completed(Trip trip, DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(trip);
        if (trip.State != TripState.Completed || trip.Assignment is null)
            throw new InvalidOperationException(
                $"Completed notification requires a COMPLETED Trip with an assignment but Trip {trip.TripId:D} is {trip.State.ToCanonicalName()}.");
        return Build(trip, NotificationMessage.Types.RiderTripCompleted,
            CompletedMessageId(trip.TripId, trip.Assignment.ReservationId), occurredAt);
    }

    /// <summary>The Rider cancelled the ride (before start).</summary>
    public static OutboxMessage Cancelled(Trip trip, DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(trip);
        if (trip.State != TripState.Cancelled)
            throw new InvalidOperationException(
                $"Cancelled notification requires CANCELLED but Trip {trip.TripId:D} is {trip.State.ToCanonicalName()}.");
        return Build(trip, NotificationMessage.Types.RiderRideCancelled,
            CancelledMessageId(trip.TripId, trip.CurrentMatchingRound), occurredAt);
    }

    /// <summary>A terminal payment authorization failure cancelled the ride.</summary>
    public static OutboxMessage PaymentFailed(Trip trip, string? reasonCode, DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(trip);
        if (trip.State != TripState.Cancelled)
            throw new InvalidOperationException(
                $"PaymentFailed notification requires CANCELLED but Trip {trip.TripId:D} is {trip.State.ToCanonicalName()}.");
        return Build(trip, NotificationMessage.Types.RiderPaymentFailed,
            PaymentFailedMessageId(trip.TripId, trip.CurrentMatchingRound), occurredAt,
            reasonCode: reasonCode);
    }

    private static OutboxMessage Build(
        Trip trip, string notificationType, string messageId, DateTimeOffset occurredAt,
        string? reasonCode = null)
    {
        if (occurredAt == default)
            throw new ArgumentException("A concrete notification timestamp is required.", nameof(occurredAt));

        var envelope = MessageEnvelope<NotificationRequestedV1>.Create(
            NotificationMessage.MessageType,
            trip.TripId.ToString("D"),
            new NotificationRequestedV1
            {
                RecipientUserId = trip.RiderId,
                RecipientRole = NotificationMessage.Roles.Rider,
                NotificationType = notificationType,
                TripId = trip.TripId,
                ReasonCode = reasonCode,
            },
            messageId: messageId,
            occurredAt: occurredAt,
            aggregateType: AggregateType,
            aggregateId: trip.TripId.ToString("D"),
            aggregateVersion: trip.Version,
            operationKey: messageId);
        return OutboxMessage.Create(
            envelope, RideTopology.EventsExchange, NotificationMessage.TripRoutingKey, now: occurredAt);
    }
}
