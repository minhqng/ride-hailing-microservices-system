using System.Text.Json.Serialization;

namespace RideHailing.TripService.Api;

/// <summary>
/// Exact assignment identity supplied by the Driver client. The driver id is
/// verified against the authenticated principal through the Driver-owned
/// directory; the reservation id is the capability minted by Matching and
/// checked by the Trip aggregate.
/// </summary>
public sealed record DriverLifecycleRequest
{
    [property: JsonRequired]
    public Guid DriverId { get; init; }

    [property: JsonRequired]
    public Guid ReservationId { get; init; }
}

public sealed record DriverLifecycleResponse
{
    public Guid TripId { get; init; }

    public string State { get; init; } = string.Empty;

    /// <summary>True when the same legal transition was already current.</summary>
    public bool Replayed { get; init; }
}
