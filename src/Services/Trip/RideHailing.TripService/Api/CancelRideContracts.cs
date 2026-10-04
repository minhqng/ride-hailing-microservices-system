// CancelRide HTTP transport shape freeze.
//
// SCOPE: transport shape ONLY. No state transition, terminal logic, Matching
// cancellation, Driver release, compensation, Outbox, or cleanup workflow
// (all W3+).
//
// CANONICAL SEMANTIC (Docs 00-01):
//   - Trip cancellation commits a terminal CANCELLED state; late
//     DriverReserved/PaymentAuthorized must not reactivate it (DSI-010,
//     DSI-032). Cleanup of an exact reservation (tripId + reservationId,
//     DSI-033) is a backend Saga/Outbox concern and is NOT part of this
//     external DTO.
//   - tripId in the route identifies which logical ride to cancel.
// W2 MECHANICAL FREEZE (this file):
//   - Request body is intentionally empty: POST carries the intent, the route
//     carries tripId. No "reason" field is frozen now; a reason would be a
//     future additive optional field, not invented here.
//   - Response echoes tripId + terminal status string ("CANCELLED").
//   - Intended route/method: POST /trips/{tripId:guid}/cancel (see TripRoutes).
//     POST (not DELETE) because cancel is a domain transition with
//     compensation side effects, not a resource deletion. Not registered in
//     Program.cs, so no fake success can be observed.

using System.Text.Json.Serialization;

namespace RideHailing.TripService.Api;

/// <summary>
/// POST /trips/{tripId}/cancel request body. Intentionally empty.
/// tripId travels in the route; idempotency (if used) travels in headers.
/// </summary>
public sealed record CancelRideRequest
{
}

/// <summary>
/// POST /trips/{tripId}/cancel response body. Terminal identity echo.
/// </summary>
public sealed record CancelRideResponse
{
    /// <summary>Cancelled logical ride (canonical). CLR Guid / wire string.</summary>
    [JsonPropertyName("tripId")]
    public required Guid TripId { get; init; }

    /// <summary>
    /// Expected terminal value "CANCELLED" (canonical terminal set).
    /// Wire: JSON string.
    /// </summary>
    [JsonPropertyName("status")]
    public required string Status { get; init; }
}
