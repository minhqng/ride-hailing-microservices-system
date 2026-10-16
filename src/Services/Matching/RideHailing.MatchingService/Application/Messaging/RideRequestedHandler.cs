// B-W08-01/02: RideRequestedV1 consumer logic (Doc 02 §12.7, DSR-009).
// Lookup-or-create the durable round keyed by (tripId, matchingRound);
// duplicate delivery resumes the same round with the same identities — never
// a new candidate sequence, never new reservation IDs (orchestrator owns
// attempts; this handler performs NO reserve calls — candidates arrive via a
// future pipeline, and reserve execution stays in W7-tested code paths).
// Contract (A payload unchanged, C envelope authoritative): tripId +
// matchingRound come from the payload; the input message identity comes from
// envelope.MessageId verbatim (opaque string, never parsed as GUID, never
// transformed). Unknown extra payload fields are ignored (forward-compatible
// tolerant reader); a payload-embedded inputMessageId is NOT required and,
// if present, is ignored.
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RideHailing.MatchingService.Infrastructure.Persistence;
using RideHailing.Messaging.Contracts;

namespace RideHailing.MatchingService.Application.Messaging;

public sealed class RideRequestedHandler
{
    public static readonly System.Diagnostics.ActivitySource Activities = new("RideHailing.MatchingService");

    private const string ExpectedMessageType = "RideRequestedV1";

    private readonly MatchingAttemptRepository _rounds;
    private readonly ILogger<RideRequestedHandler> _logger;

    public RideRequestedHandler(MatchingAttemptRepository rounds, ILogger<RideRequestedHandler> logger)
    {
        _rounds = rounds;
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

        var (tripId, matchingRound, inputMessageId) = ReadPayload(envelope);

        var round = await _rounds.EnsureRoundAsync(tripId, matchingRound, inputMessageId, ct);
        activity?.SetTag("matching.round", $"{tripId:N}/{matchingRound}");
        _logger.LogInformation(
            "Consumed {MessageType} for trip {TripId} round {Round}: round {Status}.",
            envelope.MessageType, tripId, matchingRound, round.Status);
        return HandlerOutcome.Done;
    }

    public static MessageEnvelope<JsonElement> ParseEnvelope(byte[] body) =>
        BrokerEnvelope.Parse(body);

    public static (Guid TripId, int MatchingRound, string InputMessageId) ReadPayload(
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

        // The input message identity is the envelope's MessageId preserved
        // byte-for-byte (decoded .NET string). No parsing, trimming, casing,
        // hashing, or UUID derivation: opaque string identity.
        return (tripId, matchingRound, envelope.MessageId);
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
