// GetTrip HTTP transport shape freeze (P2).
//
// SCOPE: external/read DTO surface ONLY. No PostgreSQL query, no EF model,
// no Saga/Outbox exposure, no invented product fields.
//
// CANONICAL SEMANTIC (Docs 00-01):
//   - tripId: one logical ride (Doc 01 §3.3).
//   - status: Trip P0 states CREATED, MATCHING, DRIVER_RESERVED,
//     DRIVER_ACCEPTED, READY_TO_START, IN_PROGRESS, COMPLETED, CANCELLED,
//     MATCH_FAILED; terminal set COMPLETED/CANCELLED/MATCH_FAILED never
//     regresses (Doc 01 §6.2-6.4, DSI-010).
//   - pickup/dropoff: ride request identity (Doc 01 §6.1).
//   - matchingRound: one matching generation; stale-round fencing (Doc 01
//     §3.3, DSI-016). Only the current round may become the assignment.
//   - assignedDriverId + reservationId: current assignment reference
//     (Doc 01 §6.1, DSI-008/009). Coherence rule frozen here: both null when
//     there is no current assignment; both present when there is one.
// W2 MECHANICAL FREEZE (this file):
//   - status as JSON string (not numeric enum) so wire values match canonical
//     state names without requiring a global enum-converter policy.
//   - matchingRound as CLR int / JSON number: visibly a generation/version,
//     never an opaque Guid.
//   - assignedDriverId / reservationId as nullable CLR Guid / JSON string-or-
//     null. Null = no current assignment (honest absence, not a fake zero).
//   - Fare, timestamps, Saga, Outbox, MatchingAttempt, Payment internals are
//     intentionally absent. Fare belongs to W4 (A-W04-05); the rest are never
//     part of the external read surface.
//   - Intended route/method: GET /trips/{tripId:guid} (see TripRoutes).
//     Not registered in Program.cs in P2.

using System.Text.Json.Serialization;

namespace RideHailing.TripService.Api;

/// <summary>
/// GET /trips/{tripId} response body. Minimal external read surface.
/// </summary>
public sealed record GetTripResponse
{
    /// <summary>One logical ride (canonical). CLR Guid / wire string.</summary>
    [JsonPropertyName("tripId")]
    public required Guid TripId { get; init; }

    /// <summary>
    /// Canonical Trip P0 state name (e.g. "MATCHING", "CANCELLED").
    /// Wire: JSON string. New states would be additive + canonically reviewed.
    /// </summary>
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    /// <summary>Pickup coordinate (required).</summary>
    [JsonPropertyName("pickup")]
    public required TripLocationDto Pickup { get; init; }

    /// <summary>Dropoff coordinate (required).</summary>
    [JsonPropertyName("dropoff")]
    public required TripLocationDto Dropoff { get; init; }

    /// <summary>
    /// Current matching generation (canonical). CLR int / wire number.
    /// Increments only for a new logical round.
    /// </summary>
    [JsonPropertyName("matchingRound")]
    public required int MatchingRound { get; init; }

    /// <summary>
    /// Current assigned Driver, or null when there is no current assignment.
    /// CLR Guid? / wire string-or-null.
    /// </summary>
    [JsonPropertyName("assignedDriverId")]
    public Guid? AssignedDriverId { get; init; }

    /// <summary>
    /// Current reservation effect, or null when there is no current assignment.
    /// Same attempt reuses this ID across retry/reconciliation (canonical).
    /// CLR Guid? / wire string-or-null.
    /// </summary>
    [JsonPropertyName("reservationId")]
    public Guid? ReservationId { get; init; }
}
