using System.Security.Cryptography;
using System.Text.Json;
using RideHailing.Messaging.Contracts;

namespace RideHailing.DriverService.Application.Messaging;

public sealed record DriverCommand(
    Guid DriverId, Guid TripId, Guid ReservationId,
    string Fingerprint, int? MatchingRound = null)
{
    public const string Queue = "driver.commands.q";
    public const string Start = "MarkDriverTripStartedV1";
    public const string Complete = "MarkDriverTripCompletedV1";
    public const string Release = "ReleaseDriverReservationV1";
    public static IReadOnlySet<string> SupportedTypes { get; } = new HashSet<string>(StringComparer.Ordinal)
        { Start, Complete, Release };

    public static DriverCommand Parse(MessageEnvelope<JsonElement> envelope)
    {
        envelope.Validate();
        if (envelope.MessageId.Length > 256 || !SupportedTypes.Contains(envelope.MessageType))
            throw new ArgumentException("Invalid Driver command identity/type.");
        var driver = Id(envelope.Payload, "driverId");
        var trip = Id(envelope.Payload, "tripId");
        var reservation = Id(envelope.Payload, "reservationId");
        int? round = null;
        if (envelope.MessageType == Release)
        {
            if (envelope.Payload.TryGetProperty("matchingRound", out var value)
                && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed) && parsed > 0)
                round = parsed;
        }
        var semantic = JsonSerializer.SerializeToElement(new
        {
            envelope.MessageType, envelope.SchemaVersion, envelope.Payload,
            envelope.AggregateType, envelope.AggregateId, envelope.AggregateVersion,
            envelope.SagaId, envelope.OperationKey
        });
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) WriteCanonical(writer, semantic);
        return new(driver, trip, reservation,
            Convert.ToHexString(SHA256.HashData(stream.ToArray())), round);
    }

    public void ValidateForExecution(string messageType)
    {
        // Already committed three-field receipts may have a three-field payload.
        // Their exact semantic replay needs no new execution or guessed round.
        if (messageType == Release && MatchingRound is null)
            throw new ArgumentException("Release requires a positive integer matchingRound.");
    }

    private static Guid Id(JsonElement payload, string name)
    {
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.String || !value.TryGetGuid(out var id) || id == Guid.Empty)
            throw new ArgumentException("Command requires exact driverId/tripId/reservationId.");
        return id;
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                if (!names.Add(property.Name)) throw new ArgumentException("Duplicate JSON property.");
                writer.WritePropertyName(property.Name);
                WriteCanonical(writer, property.Value);
            }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in value.EnumerateArray()) WriteCanonical(writer, item);
            writer.WriteEndArray();
        }
        else value.WriteTo(writer);
    }
}
