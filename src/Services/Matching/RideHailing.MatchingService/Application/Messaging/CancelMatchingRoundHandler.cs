// B-REC-01 adjacent gap: Matching had no CancelMatchingRoundV1 consumer,
// so a Trip cancellation never reached the durable round. The handler records
// a durable cancel intent and flags an existing round; the autonomous worker
// then starts no new attempt and reconciles any unresolved effect with the
// SAME reservation id (Doc 02 §12.9, DSI-017/031).
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RideHailing.MatchingService.Infrastructure.Persistence;
using RideHailing.MatchingService.Application.Matching;
using RideHailing.Messaging.Contracts;

namespace RideHailing.MatchingService.Application.Messaging;

public sealed class CancelMatchingRoundHandler
{
    private const string ExpectedMessageType = "CancelMatchingRoundV1";

    private readonly MatchingWorkRepository _work;
    private readonly ILogger<CancelMatchingRoundHandler> _logger;

    public CancelMatchingRoundHandler(
        MatchingWorkRepository work,
        ILogger<CancelMatchingRoundHandler> logger)
    {
        _work = work;
        _logger = logger;
    }

    public async Task<HandlerOutcome> HandleAsync(BrokerDelivery delivery, CancellationToken ct)
    {
        var envelope = BrokerEnvelope.Parse(delivery.Body);
        if (!string.Equals(envelope.MessageType, ExpectedMessageType, StringComparison.Ordinal))
        {
            throw new ArgumentException($"Unexpected message type '{envelope.MessageType}'.");
        }

        var (tripId, matchingRound) = ReadPayload(envelope);
        await _work.RecordCancelIntentAsync(tripId, matchingRound, ct);
        _logger.LogInformation(
            "Recorded {MessageType} for trip {TripId} round {Round}.",
            envelope.MessageType, tripId, matchingRound);
        return HandlerOutcome.Done;
    }

    public static (Guid TripId, int MatchingRound) ReadPayload(
        MessageEnvelope<JsonElement> envelope)
    {
        var payload = envelope.Payload;
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("tripId", out var tripEl)
            || tripEl.ValueKind != JsonValueKind.String
            || !Guid.TryParse(tripEl.GetString(), out var tripId)
            || tripId == Guid.Empty)
        {
            throw new ArgumentException("CancelMatchingRoundV1 requires a non-empty tripId.");
        }

        if (!payload.TryGetProperty("matchingRound", out var roundEl)
            || roundEl.ValueKind != JsonValueKind.Number
            || !roundEl.TryGetInt32(out var matchingRound)
            || matchingRound < 1)
        {
            throw new ArgumentException("CancelMatchingRoundV1 requires matchingRound >= 1.");
        }

        return (tripId, matchingRound);
    }
}
