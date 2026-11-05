// DS03 Rider notification intent proofs (Doc 02 §43): recipient authority,
// deterministic message identity and state misuse guards.

using System.Text.Json;
using RideHailing.Messaging.Contracts;
using RideHailing.TripService.Domain.Trips;
using RideHailing.TripService.Integration.Messaging;
using RideHailing.TripService.UnitTests.Domain.Trips;

namespace RideHailing.TripService.UnitTests;

public sealed class TripNotificationIntentTests
{
    private static NotificationRequestedV1 Payload(RideHailing.Messaging.Outbox.OutboxMessage message) =>
        JsonSerializer.Deserialize<NotificationRequestedV1>(message.PayloadJson)!;

    [Fact]
    public void RideRequested_UsesRiderIdentityAndOpenRound()
    {
        var trip = TripTestBuilders.MatchingTrip();

        var message = TripNotificationIntents.RideRequested(trip, TripTestBuilders.ActionMoment);

        var payload = Payload(message);
        Assert.Equal(NotificationMessage.MessageType, message.MessageType);
        Assert.Equal(NotificationMessage.TripRoutingKey, message.RoutingKey);
        Assert.Equal("ride.events", message.Exchange);
        Assert.Equal(trip.RiderId, payload.RecipientUserId);
        Assert.Equal(NotificationMessage.Roles.Rider, payload.RecipientRole);
        Assert.Equal(NotificationMessage.Types.RiderRideRequested, payload.NotificationType);
        Assert.Equal(trip.TripId, payload.TripId);
        Assert.Equal(TripNotificationIntents.RideRequestedMessageId(trip.TripId, 1), message.MessageId);
        Assert.Equal(message.MessageId, message.OperationKey);
        Assert.Equal("Trip", message.AggregateType);
        Assert.Equal(trip.TripId.ToString("D"), message.CorrelationId);
    }

    [Fact]
    public void DriverAssigned_UsesCurrentReservationIdentity()
    {
        var trip = TripTestBuilders.AtState(TripState.DriverReserved);

        var message = TripNotificationIntents.DriverAssigned(trip, TripTestBuilders.ActionMoment);
        var payload = Payload(message);

        Assert.Equal(NotificationMessage.Types.RiderDriverAssigned, payload.NotificationType);
        Assert.Equal(trip.RiderId, payload.RecipientUserId);
        Assert.Equal(
            TripNotificationIntents.DriverAssignedMessageId(trip.TripId, TripTestBuilders.ReservationId),
            message.MessageId);
    }

    [Fact]
    public void DriverRejected_CarriesBoundedReason()
    {
        var trip = Trip.Reconstitute(
            TripTestBuilders.TripId,
            TripTestBuilders.RiderId,
            TripTestBuilders.Location(),
            TripTestBuilders.Location(10.776889, 106.700806),
            TripTestBuilders.Fare(),
            TripState.MatchFailed,
            1,
            null,
            TripTestBuilders.CreatedMoment,
            TripTestBuilders.ActionMoment,
            8,
            TripFailureReason.DriverRejected);

        var message = TripNotificationIntents.DriverRejected(
            trip, TripTestBuilders.ReservationId, TripTestBuilders.ActionMoment);
        var payload = Payload(message);

        Assert.Equal(NotificationMessage.Types.RiderDriverRejected, payload.NotificationType);
        Assert.Equal("DRIVER_REJECTED", payload.ReasonCode);
        Assert.Equal(trip.RiderId, payload.RecipientUserId);
        Assert.Equal(
            TripNotificationIntents.DriverRejectedMessageId(trip.TripId, TripTestBuilders.ReservationId),
            message.MessageId);
    }

    [Fact]
    public void NoDriver_CarriesNoDriverReason()
    {
        var trip = TripTestBuilders.MatchingTrip();
        trip.MarkMatchingFailed(1, TripTestBuilders.ActionMoment);

        var message = TripNotificationIntents.NoDriver(trip, TripTestBuilders.ActionMoment);
        var payload = Payload(message);

        Assert.Equal(NotificationMessage.Types.RiderNoDriver, payload.NotificationType);
        Assert.Equal("NO_DRIVER_AVAILABLE", payload.ReasonCode);
    }

    [Fact]
    public void CompletedAndCancelled_UseTheirOwnLogicalIdentities()
    {
        var completed = TripTestBuilders.AtState(TripState.Completed);
        var completedMessage = TripNotificationIntents.Completed(completed, TripTestBuilders.ActionMoment);
        Assert.Equal(NotificationMessage.Types.RiderTripCompleted, Payload(completedMessage).NotificationType);

        var cancelled = TripTestBuilders.AtState(TripState.Cancelled);
        var cancelledMessage = TripNotificationIntents.Cancelled(cancelled, TripTestBuilders.ActionMoment);
        Assert.Equal(NotificationMessage.Types.RiderRideCancelled, Payload(cancelledMessage).NotificationType);
        Assert.NotEqual(completedMessage.MessageId, cancelledMessage.MessageId);
    }

    [Fact]
    public void PaymentFailed_RequiresCancelledTrip()
    {
        var cancelled = TripTestBuilders.AtState(TripState.Cancelled);
        var message = TripNotificationIntents.PaymentFailed(cancelled, "DECLINED", TripTestBuilders.ActionMoment);
        var payload = Payload(message);
        Assert.Equal(NotificationMessage.Types.RiderPaymentFailed, payload.NotificationType);
        Assert.Equal("DECLINED", payload.ReasonCode);

        var accepted = TripTestBuilders.AtState(TripState.DriverAccepted);
        Assert.Throws<InvalidOperationException>(() =>
            TripNotificationIntents.PaymentFailed(accepted, "DECLINED", TripTestBuilders.ActionMoment));
    }

    [Fact]
    public void Builders_RejectWrongState()
    {
        var reserved = TripTestBuilders.AtState(TripState.DriverReserved);
        Assert.Throws<InvalidOperationException>(() =>
            TripNotificationIntents.RideRequested(reserved, TripTestBuilders.ActionMoment));

        var matching = TripTestBuilders.MatchingTrip();
        Assert.Throws<InvalidOperationException>(() =>
            TripNotificationIntents.DriverAssigned(matching, TripTestBuilders.ActionMoment));
    }

    [Fact]
    public void MessageIdentity_IsDeterministicAcrossCalls()
    {
        var trip = TripTestBuilders.MatchingTrip();
        var first = TripNotificationIntents.RideRequested(trip, TripTestBuilders.ActionMoment);
        var second = TripNotificationIntents.RideRequested(trip, TripTestBuilders.ActionMoment);
        Assert.Equal(first.MessageId, second.MessageId);
        Assert.Equal(first.PayloadJson, second.PayloadJson);
    }
}
