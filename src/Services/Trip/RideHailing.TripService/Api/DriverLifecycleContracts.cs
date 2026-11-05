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

/// <summary>
/// Truthful rejection result: the terminal Trip result and cleanup intent are
/// committed; the Driver reservation release may still be pending authority
/// confirmation.
/// </summary>
public sealed record DriverRejectResponse
{
    public Guid TripId { get; init; }

    public string State { get; init; } = string.Empty;

    /// <summary>Durable MATCH_FAILED reason (DRIVER_REJECTED on success).</summary>
    public string? FailureReason { get; init; }

    /// <summary>True when the same exact rejection was already recorded.</summary>
    public bool Replayed { get; init; }

    /// <summary>True while a Trip-owned cleanup obligation is unconfirmed.</summary>
    public bool CleanupPending { get; init; }
}
