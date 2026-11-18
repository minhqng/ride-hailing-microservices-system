// Recipient-addressed notification intent.
//
// The authoritative owner of the recipient identity publishes this minimum
// intent through its existing Transactional Outbox in the same transaction as
// the business effect. It carries no free-form content, token, credential or
// payment secret: the Notification worker maps the bounded type/reason to safe
// content and persists a recipient-addressed inbox row.
using System.Text.Json.Serialization;

namespace RideHailing.Messaging.Contracts;

public static class NotificationMessage
{
    public const string MessageType = "NotificationRequestedV1";
    public const string TripRoutingKey = "trip.notification-requested.v1";
    public const string DriverRoutingKey = "driver.notification-requested.v1";

    public static class Roles
    {
        public const string Rider = "Rider";
        public const string Driver = "Driver";
    }

    public static class Types
    {
        public const string RiderRideRequested = "RIDER_RIDE_REQUESTED";
        public const string RiderDriverAssigned = "RIDER_DRIVER_ASSIGNED";
        public const string RiderDriverRejected = "RIDER_DRIVER_REJECTED";
        public const string RiderNoDriver = "RIDER_NO_DRIVER";
        public const string RiderTripCompleted = "RIDER_TRIP_COMPLETED";
        public const string RiderRideCancelled = "RIDER_RIDE_CANCELLED";
        public const string RiderPaymentFailed = "RIDER_PAYMENT_FAILED";
        public const string DriverTripAssigned = "DRIVER_TRIP_ASSIGNED";
        public const string DriverReservationReleased = "DRIVER_RESERVATION_RELEASED";
    }

    private static readonly IReadOnlyDictionary<string, string> TypeOwners =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [Types.RiderRideRequested] = Roles.Rider,
            [Types.RiderDriverAssigned] = Roles.Rider,
            [Types.RiderDriverRejected] = Roles.Rider,
            [Types.RiderNoDriver] = Roles.Rider,
            [Types.RiderTripCompleted] = Roles.Rider,
            [Types.RiderRideCancelled] = Roles.Rider,
            [Types.RiderPaymentFailed] = Roles.Rider,
            [Types.DriverTripAssigned] = Roles.Driver,
            [Types.DriverReservationReleased] = Roles.Driver,
        };

    public static bool IsKnownType(string type) => TypeOwners.ContainsKey(type);

    public static bool IsValidTypeForRole(string type, string role) =>
        TypeOwners.TryGetValue(type, out var owner) && string.Equals(owner, role, StringComparison.Ordinal);
}

/// <summary>
/// Minimum recipient-addressed notification intent. One intent addresses one
/// recipient; a source business event that must reach both roles publishes two
/// intents with distinct deterministic message identities.
/// </summary>
public sealed record NotificationRequestedV1
{
    [JsonPropertyName("recipientUserId")]
    public required Guid RecipientUserId { get; init; }

    [JsonPropertyName("recipientRole")]
    public required string RecipientRole { get; init; }

    [JsonPropertyName("notificationType")]
    public required string NotificationType { get; init; }

    [JsonPropertyName("tripId")]
    public required Guid TripId { get; init; }

    [JsonPropertyName("reasonCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReasonCode { get; init; }

    public void Validate()
    {
        if (RecipientUserId == Guid.Empty)
            throw new ArgumentException("recipientUserId is required.");
        if (RecipientRole is not (NotificationMessage.Roles.Rider or NotificationMessage.Roles.Driver))
            throw new ArgumentException("recipientRole must be Rider or Driver.");
        if (!NotificationMessage.IsKnownType(NotificationType))
            throw new ArgumentException("notificationType is not a supported notification type.");
        if (!NotificationMessage.IsValidTypeForRole(NotificationType, RecipientRole))
            throw new ArgumentException("notificationType does not belong to the recipient role.");
        if (TripId == Guid.Empty)
            throw new ArgumentException("tripId is required.");
        if (ReasonCode is not null &&
            (ReasonCode.Length is < 1 or > 64 ||
             !ReasonCode.All(ch => ch is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_')))
            throw new ArgumentException("reasonCode must be a bounded uppercase code.");
    }
}
