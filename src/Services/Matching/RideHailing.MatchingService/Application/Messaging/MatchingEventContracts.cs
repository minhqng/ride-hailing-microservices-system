// B-W09-03/04: Matching terminal-result event contract (Doc 02 §12.8,
// DSI-035/DSR-010). The message type, exchange and routing key mirror the
// frozen catalog in RideTopology and the executable Trip consumer
// (TripMessageHandler: MatchingFailedV1 on matching.failed.v1).
//
// The result identity is derived from the logical terminal result
// (tripId, matchingRound), not from a publish attempt. It is persisted in
// matching_rounds.result_message_id and copied verbatim into the Outbox row
// and wire envelope; a relay retry, duplicate request or process restart
// therefore reuses exactly one identity per logical NO_DRIVER result.
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace RideHailing.MatchingService.Application.Messaging;

public static class MatchingEventTypes
{
    public const string MatchingFailed = "MatchingFailedV1";
}

public static class MatchingEventRoutes
{
    public const string EventsExchange = "ride.events";
    public const string MatchingFailed = "matching.failed.v1";
}

/// <summary>
/// Minimal payload required by the existing Trip consumer: tripId plus the
/// positive matchingRound generation. No additional required fields are
/// invented; consumers may ignore extra data but this producer sends only
/// the frozen minimum.
/// </summary>
public sealed record MatchingFailedPayload(
    [property: JsonPropertyName("tripId")] Guid TripId,
    [property: JsonPropertyName("matchingRound")] int MatchingRound);

public static class MatchingResultMessageIds
{
    /// <summary>
    /// RFC 9562 UUIDv8: deterministic, versioned, domain-separated from every
    /// other identity namespace. The canonical lowercase "D" form is stored
    /// as matching_rounds.result_message_id (UUID), used as Outbox
    /// message_id, and emitted as the envelope messageId, so the three
    /// representations compare exactly.
    /// </summary>
    public static string ForNoDriver(Guid tripId, int matchingRound)
    {
        if (tripId == Guid.Empty)
        {
            throw new ArgumentException("tripId must not be empty.", nameof(tripId));
        }

        if (matchingRound < 1)
        {
            throw new ArgumentException("matchingRound must be >= 1.", nameof(matchingRound));
        }

        // Invariant formatting is part of the identity: a process restart under
        // another culture must derive exactly the same bytes.
        var input = string.Create(
            CultureInfo.InvariantCulture,
            $"matching:no-driver:v1|trip:{tripId:D}|round:{matchingRound}");
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));

        // 16 hash bytes form the UUID; stamp RFC 9562 version 8 and the
        // RFC 4122/9562 variant so the value is unambiguously a v8 UUID.
        // Guid.ToByteArray/new Guid use little-endian Data1..Data3, so the
        // canonical version nibble (time_hi_and_version, canonical byte 6)
        // lives in array byte 7; the variant bits are in array byte 8.
        var bytes = hash[..16];
        bytes[7] = (byte)((bytes[7] & 0x0F) | 0x80);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes).ToString("D");
    }
}
