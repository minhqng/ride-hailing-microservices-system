using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RideHailing.Messaging.Contracts;

namespace RideHailing.MatchingService.Application.Projection;

// Derived receipts share the projection's Redis lifecycle. They are not
// authoritative consumer receipts for Driver or Trip business state.
public sealed record ProjectionReceipt(string Key, string Fingerprint)
{
    public static ProjectionReceipt ForEnvelope(MessageEnvelope<JsonElement> envelope)
    {
        var semantic = JsonSerializer.SerializeToElement(new
        {
            envelope.MessageType, envelope.SchemaVersion, envelope.AggregateType,
            envelope.AggregateId, envelope.AggregateVersion, envelope.SagaId,
            envelope.OperationKey, envelope.Payload
        });
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes))
            WriteCanonical(writer, semantic);
        return new("driver:projection:receipt:" + Hash(Encoding.UTF8.GetBytes(envelope.MessageId)),
            Hash(bytes.ToArray()));
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                if (!names.Add(property.Name)) throw new ArgumentException("Duplicate JSON property");
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
