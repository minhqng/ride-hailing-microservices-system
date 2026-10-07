using System.Text.Json.Serialization;
using System.Text.Json;

namespace RideHailing.Messaging.Contracts;

// Canonical V1 wire shape, Doc 02 sections 5-6. Trace context belongs in transport headers.
public sealed record MessageEnvelope<T>(
    [property: JsonPropertyName("messageId")] string MessageId,
    [property: JsonPropertyName("messageType")] string MessageType,
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("occurredAt")] DateTimeOffset OccurredAt,
    [property: JsonPropertyName("correlationId")] string CorrelationId,
    [property: JsonPropertyName("causationId")] string? CausationId,
    [property: JsonPropertyName("payload")] T Payload,
    [property: JsonPropertyName("aggregateType")] string? AggregateType = null,
    [property: JsonPropertyName("aggregateId")] string? AggregateId = null,
    [property: JsonPropertyName("aggregateVersion")] long? AggregateVersion = null,
    [property: JsonPropertyName("sagaId")] string? SagaId = null,
    [property: JsonPropertyName("operationKey")] string? OperationKey = null)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(MessageId)) throw new ArgumentException("messageId is required");
        if (string.IsNullOrWhiteSpace(MessageType)) throw new ArgumentException("messageType is required");
        if (SchemaVersion != 1) throw new ArgumentException("Only schemaVersion 1 is supported");
        if (OccurredAt == default) throw new ArgumentException("occurredAt is required");
        if (string.IsNullOrWhiteSpace(CorrelationId)) throw new ArgumentException("correlationId is required");
        if (Payload is null || (Payload is JsonElement json && json.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)) throw new ArgumentException("payload is required");
        if (AggregateVersion < 0) throw new ArgumentException("aggregateVersion cannot be negative");
        if ((AggregateType is null) != (AggregateId is null))
            throw new ArgumentException("aggregateType and aggregateId must be supplied together");
        if (AggregateVersion.HasValue && AggregateId is null)
            throw new ArgumentException("aggregateVersion requires aggregate identity");
        foreach (var field in new[] { CausationId, AggregateType, AggregateId, SagaId, OperationKey })
            if (field is not null && string.IsNullOrWhiteSpace(field))
                throw new ArgumentException("Optional identities must be omitted or nonblank");
    }

    public static MessageEnvelope<T> Create(
        string messageType, string correlationId, T payload,
        string? causationId = null, string? messageId = null, DateTimeOffset? occurredAt = null,
        string? aggregateType = null, string? aggregateId = null, long? aggregateVersion = null,
        string? sagaId = null, string? operationKey = null)
    {
        var envelope = new MessageEnvelope<T>(messageId ?? Guid.NewGuid().ToString("N"),
            messageType, 1, occurredAt ?? DateTimeOffset.UtcNow, correlationId, causationId, payload,
            aggregateType, aggregateId, aggregateVersion, sagaId, operationKey);
        envelope.Validate();
        return envelope;
    }
}
