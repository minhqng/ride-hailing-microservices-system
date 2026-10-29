// B-W08-01/02 + B-REC-01: RideRequestedV1 durable intake.
// The handler commits the round, the canonical pickup center and the
// auto-execute obligation BEFORE the consumer ACKs. The autonomous worker
// (MatchingWorkDispatcher) is the production execution trigger; this handler
// deliberately makes no reserve call. Duplicate delivery resumes the same
// round with the same identities — never a new candidate sequence, never new
// reservation IDs. A CancelMatchingRoundV1 that overtook this message is
// applied from its durable tombstone in the same intake.
// Contract (A payload unchanged, C envelope authoritative): tripId +
// matchingRound + pickup come from the payload; the input message identity
// comes from envelope.MessageId verbatim (opaque string, never parsed as
// GUID, never transformed). A payload-embedded inputMessageId is ignored.
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RideHailing.MatchingService.Infrastructure.Persistence;
using RideHailing.MatchingService.Application.Matching;
using RideHailing.Messaging.Contracts;

namespace RideHailing.MatchingService.Application.Messaging;

public sealed record RideRequestedPayload(
    Guid TripId,
    int MatchingRound,
    double PickupLongitude,
    double PickupLatitude,
    string InputMessageId);

public sealed class RideRequestedHandler
{
    public static readonly System.Diagnostics.ActivitySource Activities = MatchingTelemetry.Activities;

    private const string ExpectedMessageType = "RideRequestedV1";

    private readonly MatchingAttemptRepository _rounds;
    private readonly MatchingWorkRepository? _work;
    private readonly ILogger<RideRequestedHandler> _logger;

    public RideRequestedHandler(
        MatchingAttemptRepository rounds,
        ILogger<RideRequestedHandler> logger,
        MatchingWorkRepository? work = null)
    {
        _rounds = rounds;
        _work = work;
        _logger = logger;
    }

    public async Task<HandlerOutcome> HandleAsync(BrokerDelivery delivery, CancellationToken ct)
    {
        using var activity = StartActivity(delivery.Headers);
        var envelope = ParseEnvelope(delivery.Body);
        if (!string.Equals(envelope.MessageType, ExpectedMessageType, StringComparison.Ordinal))
        {
            throw new ArgumentException($"Unexpected message type '{envelope.MessageType}'.");
        }

        var payload = ReadProductionPayload(envelope);

        var round = await _rounds.EnsureProductionRoundAsync(
            payload.TripId,
            payload.MatchingRound,
            payload.InputMessageId,
            payload.PickupLongitude,
            payload.PickupLatitude,
            ct);
        if (_work is not null)
        {
            await _work.ApplyPendingCancelAsync(payload.TripId, payload.MatchingRound, ct);
        }

        activity?.SetTag("matching.round", $"{payload.TripId:N}/{payload.MatchingRound}");
        _logger.LogInformation(
            "Consumed {MessageType} for trip {TripId} round {Round}: round {Status}, auto-execute recorded.",
            envelope.MessageType, payload.TripId, payload.MatchingRound, round.Status);
        return HandlerOutcome.Done;
    }

    public static MessageEnvelope<JsonElement> ParseEnvelope(byte[] body) =>
        BrokerEnvelope.Parse(body);

    public static (Guid TripId, int MatchingRound, string InputMessageId) ReadPayload(
        MessageEnvelope<JsonElement> envelope)
    {
        var payload = ReadProductionPayload(envelope);
        return (payload.TripId, payload.MatchingRound, payload.InputMessageId);
    }

    /// <summary>
    /// Production payload reader: identity plus the pickup center Matching
    /// needs to discover candidates after a restart. Dropoff and requestedAt
    /// remain A-owned fields Matching does not consume; extra properties stay
    /// forward-compatible.
    /// </summary>
    public static RideRequestedPayload ReadProductionPayload(
        MessageEnvelope<JsonElement> envelope)
    {
        if (string.IsNullOrWhiteSpace(envelope.MessageId))
        {
            throw new ArgumentException("RideRequestedV1 requires a non-empty envelope messageId.");
        }

        var payload = envelope.Payload;
        if (payload.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("RideRequestedV1 payload must be a JSON object.");
        }

        if (!payload.TryGetProperty("tripId", out var tripEl) || tripEl.ValueKind != JsonValueKind.String
            || !Guid.TryParse(tripEl.GetString(), out var tripId) || tripId == Guid.Empty)
        {
            throw new ArgumentException("RideRequestedV1 requires a non-empty tripId.");
        }

        if (!payload.TryGetProperty("matchingRound", out var roundEl) || roundEl.ValueKind != JsonValueKind.Number
            || !roundEl.TryGetInt32(out var matchingRound) || matchingRound < 1)
        {
            throw new ArgumentException("RideRequestedV1 requires matchingRound >= 1.");
        }

        if (!payload.TryGetProperty("pickup", out var pickupEl) || pickupEl.ValueKind != JsonValueKind.Object
            || !TryReadCoordinate(pickupEl, "longitude", -180, 180, out var longitude)
            || !TryReadCoordinate(pickupEl, "latitude", -90, 90, out var latitude))
        {
            throw new ArgumentException("RideRequestedV1 requires a finite pickup longitude/latitude.");
        }

        // The input message identity is the envelope's MessageId preserved
        // byte-for-byte (decoded .NET string). No parsing, trimming, casing,
        // hashing, or UUID derivation: opaque string identity.
        return new RideRequestedPayload(
            tripId, matchingRound, longitude, latitude, envelope.MessageId);
    }

    private static bool TryReadCoordinate(
        JsonElement parent, string name, double min, double max, out double value)
    {
        value = 0;
        if (!parent.TryGetProperty(name, out var element)
            || element.ValueKind != JsonValueKind.Number
            || !element.TryGetDouble(out value)
            || !double.IsFinite(value)
            || value < min
            || value > max)
        {
            return false;
        }

        return true;
    }

    private static Activity? StartActivity(IDictionary<string, object?> headers)
    {
        if (!TraceHeaderPropagation.TryExtract(headers, out var parent))
        {
            return null;
        }

        return Activities.StartActivity(
            "matching.consume.ride-requested",
            ActivityKind.Consumer,
            parent);
    }
}
