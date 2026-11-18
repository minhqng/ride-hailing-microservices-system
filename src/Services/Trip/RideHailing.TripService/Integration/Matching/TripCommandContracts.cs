// Trip-produced command payloads.
//
// SHAPE ONLY for the Outbox producer leg. Matching/Driver own the consumer
// semantics; Trip owns the exact identities and emits these commands only
// atomically with the terminal Trip transition that created the obligation.
//
// ReleaseDriverReservationV1 never carries a Driver id alone: the exact
// (tripId, driverId, reservationId, matchingRound) tuple is required so a
// stale or foreign reservation can never be released.

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

/// <summary>
/// Driver-actor command: mark the exact reservation's trip as started (the
/// Driver service owns the FSM transition; Trip only emits the durable
/// command).
/// </summary>
public sealed record MarkDriverTripStartedV1
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

/// <summary>
/// Driver-actor command: mark the exact reservation's trip as completed (B
/// owns the Driver FSM transition; Trip only emits the durable command).
/// </summary>
public sealed record MarkDriverTripCompletedV1
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
