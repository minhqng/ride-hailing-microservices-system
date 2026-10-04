// CreateRide HTTP transport shape freeze.
//
// SCOPE: transport SHAPES ONLY. No handler, persistence, idempotency-record,
// fingerprint, active-trip guard, or FareSnapshot calculation (W3/W4).
//
// CANONICAL SEMANTIC (Docs 00-02):
//   - Trip Service is authoritative for ride-request identity, pickup/dropoff,
//     FareSnapshot, lifecycle, assignment reference, cancellation (Doc 01 §6.1).
//   - Rider identity comes from the authenticated context, NOT the body
//     (Doc 03 SEC-04 actor-ID spoofing). Hence no riderId in this DTO.
//   - Idempotency is a domain property backed by durable identity, not an HTTP
//     method property (Doc 00 §2.11). Transport uses POST; idempotency key (if
//     present) travels in the Idempotency-Key header (see TripRoutes /
//     StableIdentities). Same-key/same-request vs same-key/conflict semantics
//     belong to W4 and are NOT proven here.
// W2 MECHANICAL FREEZE (this file):
//   - Namespace RideHailing.TripService.Api: Trip-local transport contracts.
//     HTTP DTO != Domain aggregate != Persistence entity != Integration message.
//     Nothing added to BuildingBlocks.
//   - Request: required pickup + dropoff only. No fare, no timestamps, no
//     assignment refs — those are future-additive, not invented now.
//   - Response: tripId only. Status/assignment/fare are GetTrip concerns.
//   - JSON camelCase via explicit JsonPropertyName; CLR Guid for tripId.
//   - Intended route/method: POST /trips (see TripRoutes). Routes are
//     documented constants and are NOT registered in Program.cs, so no
//     placeholder behavior can be mistaken for success.

using System.Text.Json.Serialization;

namespace RideHailing.TripService.Api;

/// <summary>
/// POST /trips request body. Pickup + dropoff only.
/// </summary>
public sealed record CreateRideRequest
{
    /// <summary>Pickup coordinate (required).</summary>
    [JsonPropertyName("pickup")]
    public required TripLocationDto Pickup { get; init; }

    /// <summary>Dropoff coordinate (required).</summary>
    [JsonPropertyName("dropoff")]
    public required TripLocationDto Dropoff { get; init; }
}

/// <summary>
/// POST /trips response body. The created logical ride identity.
/// </summary>
public sealed record CreateRideResponse
{
    /// <summary>
    /// One logical ride; stable for ride lifetime (canonical).
    /// CLR Guid / wire JSON string (W2 mechanical).
    /// </summary>
    [JsonPropertyName("tripId")]
    public required Guid TripId { get; init; }
}
