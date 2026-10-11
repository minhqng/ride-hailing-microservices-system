// RideRequestedV1 payload shape: Trip-side production proposal for the
// future Trip → Matching durable workflow start (Doc 02 §6, routing key
// trip.ride-requested.v1, producer Trip Outbox).
//
// SCOPE (A-W05-07/08): payload SHAPE ONLY. No broker runtime, no envelope
// (C-owned MessageEnvelope), no publish path, no Outbox row, no consumer:
// those arrive with W8/W9 and must reuse this shape. Matching's durable
// round/attempt ledger and Driver authority stay B-owned; Trip never decides
// reservation success.
//
// FIELDS (mechanical proposal, semantic content canonical):
//   tripId         one logical ride (Doc 01 §3.3).
//   matchingRound  the open generation this request starts (DSI-016); the
//                  consumer must treat any other generation as stale.
//   pickup/dropoff ride request identity (Doc 01 §6.1); Matching needs the
//                  pickup center for GEOSEARCH candidate discovery.
//   requestedAt    when Trip opened the round (explicit timestamp; the domain
//                  performs no clock access).
// Correlation/trace identity travels in the future envelope/headers, never in
// this payload (diagnostics are not business identity).

using System.Text.Json.Serialization;
using RideHailing.TripService.Api;
using RideHailing.TripService.Domain.Trips;

namespace RideHailing.TripService.Integration.Matching;

/// <summary>
/// Trip-produced RideRequestedV1 payload (shape freeze, no transport).
/// </summary>
public sealed record RideRequestedV1
{
    /// <summary>One logical ride (canonical). CLR Guid / wire string.</summary>
    [JsonPropertyName("tripId")]
    public required Guid TripId { get; init; }

    /// <summary>
    /// The open matching generation (canonical). CLR int / wire number;
    /// visibly a generation, never an opaque Guid.
    /// </summary>
    [JsonPropertyName("matchingRound")]
    public required int MatchingRound { get; init; }

    /// <summary>Requested pickup (GEOSEARCH center).</summary>
    [JsonPropertyName("pickup")]
    public required TripLocationDto Pickup { get; init; }

    /// <summary>Requested dropoff.</summary>
    [JsonPropertyName("dropoff")]
    public required TripLocationDto Dropoff { get; init; }

    /// <summary>When Trip opened the round. Wire: string.</summary>
    [JsonPropertyName("requestedAt")]
    public required DateTimeOffset RequestedAt { get; init; }

    /// <summary>
    /// Builds the payload from a Trip that has an open round with no
    /// assignment yet. Any other shape (no open round, already assigned,
    /// terminal) is a programming error: the producer must only emit this
    /// intent atomically with the StartMatching transition (future W9
    /// Outbox), never for a stale or assigned Trip.
    /// </summary>
    public static RideRequestedV1 FromTrip(Trip trip, DateTimeOffset requestedAt)
    {
        ArgumentNullException.ThrowIfNull(trip);

        if (requestedAt == default)
        {
            throw new ArgumentException("RideRequested requires a concrete request timestamp.", nameof(requestedAt));
        }

        if (trip.State != TripState.Matching)
        {
            throw new InvalidOperationException(
                $"RideRequestedV1 requires a MATCHING Trip but Trip {trip.TripId:D} is {trip.State.ToCanonicalName()}.");
        }

        if (trip.CurrentMatchingRound < 1)
        {
            throw new InvalidOperationException(
                $"RideRequestedV1 requires an open matching round but Trip {trip.TripId:D} carries round {trip.CurrentMatchingRound}.");
        }

        if (trip.Assignment is not null)
        {
            throw new InvalidOperationException(
                $"RideRequestedV1 requires no current assignment but Trip {trip.TripId:D} already holds one.");
        }

        return new RideRequestedV1
        {
            TripId = trip.TripId,
            MatchingRound = trip.CurrentMatchingRound,
            Pickup = new TripLocationDto { Latitude = trip.Pickup.Latitude, Longitude = trip.Pickup.Longitude },
            Dropoff = new TripLocationDto { Latitude = trip.Dropoff.Latitude, Longitude = trip.Dropoff.Longitude },
            RequestedAt = requestedAt,
        };
    }
}
