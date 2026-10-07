// B-W08-05: Driver authoritative event contracts (Doc 02 §12 catalog,
// 05B §6.1; producer = Driver Service only). Payloads are B-defined on both
// ends (Driver publishes, Matching consumes); the Matching DriverClient
// README records the frozen wire form plus remaining A/C open questions.
// Routing keys mirror the frozen RideTopology catalog exactly.
// Wire form is explicit camelCase via JsonPropertyName (same style as the
// shared MessageEnvelope): payload shape never depends on ambient serializer
// options, so producer and consumer agree byte-for-byte.
using System.Text.Json.Serialization;

namespace RideHailing.DriverService.Application.Messaging;

public static class DriverEventTypes
{
    public const string Reserved = "DriverReservedV1";
    public const string Available = "DriverAvailableV1";
    public const string Offline = "DriverOfflineV1";
    public const string ReservationReleased = "DriverReservationReleasedV1";
    public const string LocationAccepted = "DriverLocationAcceptedV1";
}

public static class DriverEventRoutes
{
    public const string EventsExchange = "ride.events";
    public const string Reserved = "driver.reserved.v1";
    public const string Available = "driver.available.v1";
    public const string Offline = "driver.offline.v1";
    public const string ReservationReleased = "driver.reservation-released.v1";
    public const string LocationAccepted = "driver.location-accepted.v1";
}

/// <summary>Authoritative Driver state fact (status + aggregate version).
/// tripId/reservationId present only when the state carries them.</summary>
public sealed record DriverStateEventPayload(
    [property: JsonPropertyName("driverId")] Guid DriverId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("version")] long Version,
    [property: JsonPropertyName("tripId")] Guid? TripId,
    [property: JsonPropertyName("reservationId")] Guid? ReservationId);

/// <summary>Accepted location sample fact (freshness already validated by
/// the ingress path; the projection Lua re-validates ordering).</summary>
public sealed record DriverLocationEventPayload(
    [property: JsonPropertyName("driverId")] Guid DriverId,
    [property: JsonPropertyName("longitude")] double Longitude,
    [property: JsonPropertyName("latitude")] double Latitude,
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("sequenceNo")] long SequenceNo,
    [property: JsonPropertyName("capturedAt")] DateTimeOffset CapturedAt);

public static class DriverEventMessageIds
{
    // Deterministic per logical effect (NOT per publish attempt): retries,
    // replays and re-publication after publish loss recompute the SAME id,
    // so downstream dedup/version guards converge. New effects (new R or new
    // version) necessarily produce new ids. W9 Outbox replaces this scheme
    // with row-generated identities; until then this is the stability story.
    public static string ForDriverEffect(Guid driverId, Guid? reservationId, int version, string status)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var input = $"driver:{driverId:N}|res:{reservationId:N}|v{version}|{status}";
        var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(input));
        return new Guid(hash[..16]).ToString("N");
    }

    public static string ForLocationSample(Guid driverId, string sessionId, long sequenceNo)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var input = $"driver:{driverId:N}|session:{sessionId}|seq{sequenceNo}";
        var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(input));
        return new Guid(hash[..16]).ToString("N");
    }
}
