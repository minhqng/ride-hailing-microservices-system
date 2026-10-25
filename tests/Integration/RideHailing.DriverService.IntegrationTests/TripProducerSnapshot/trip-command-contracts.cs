// Trip-produced command payloads (Doc 02 §6, §12.9).
//
// SHAPE ONLY for the W9 Outbox producer leg. Matching/Driver own the consumer
// semantics; Trip owns the exact identities and emits these commands only
// atomically with the terminal Trip transition that created the obligation.
//
// ReleaseDriverReservationV1 never carries a Driver id alone: the exact
// (tripId, driverId, reservationId, matchingRound) tuple is required so a
// stale or foreign reservation can never be released (A-W07-02).

using System.Text.Json.Serialization;

namespace RideHailing.TripService.Integration.Matching;

/// <summary>Stop a matching round; Matching must not start new attempts.</summary>
public sealed record CancelMatchingRoundV1
{
    [JsonPropertyName("tripId")]
    public required Guid TripId { get; init; }

    [JsonPropertyName("matchingRound")]
    public required int MatchingRound { get; init; }
}

/// <summary>Release the exact reservation effect owned by a cancelled Trip.</summary>
public sealed record ReleaseDriverReservationV1
{
    [JsonPropertyName("tripId")]
    public required Guid TripId { get; init; }

    [JsonPropertyName("driverId")]
    public required Guid DriverId { get; init; }

    [JsonPropertyName("reservationId")]
    public required Guid ReservationId { get; init; }

    [JsonPropertyName("matchingRound")]
    public required int MatchingRound { get; init; }
}
