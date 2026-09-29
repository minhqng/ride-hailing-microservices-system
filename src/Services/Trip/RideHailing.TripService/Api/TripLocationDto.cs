// Trip-side HTTP transport coordinate shape (P2 freeze).
//
// CANONICAL SEMANTIC (Docs 00-01):
//   - A ride request identifies pickup/dropoff (Doc 01 §6.1 Trip authority).
//   - Coordinates must satisfy -90 <= latitude <= 90, -180 <= longitude <= 180
//     and be finite (Doc 01 §5.3). Range/finiteness validation itself is W3+
//     behavior and is NOT implemented here.
// W2 MECHANICAL FREEZE (this file):
//   - Nested object with two doubles, serialized as JSON numbers.
//   - Wire names "latitude"/"longitude" via explicit JsonPropertyName so the
//     contract does not depend on global serializer naming policy.
//   - Chosen because it is minimal, JSON-interoperable, maps cleanly to a
//     future PostgreSQL (double precision) representation, and stays
//     additive (accuracy/address can be added later as optional fields).

using System.Text.Json.Serialization;

namespace RideHailing.TripService.Api;

/// <summary>
/// Pickup or dropoff coordinate in Trip HTTP transport contracts.
/// </summary>
public sealed record TripLocationDto
{
    /// <summary>Latitude in degrees. Canonical range: -90..90, finite.</summary>
    [JsonPropertyName("latitude")]
    public required double Latitude { get; init; }

    /// <summary>Longitude in degrees. Canonical range: -180..180, finite.</summary>
    [JsonPropertyName("longitude")]
    public required double Longitude { get; init; }
}
