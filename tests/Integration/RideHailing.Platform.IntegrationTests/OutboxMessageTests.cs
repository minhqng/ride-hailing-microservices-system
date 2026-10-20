using System.Diagnostics;
using System.Text;
using System.Text.Json;
using RideHailing.Messaging.Contracts;
using RideHailing.Messaging.Outbox;

namespace RideHailing.Platform.IntegrationTests;

public class OutboxMessageTests
{
    private static MessageEnvelope<JsonElement> Envelope() => MessageEnvelope<JsonElement>.Create(
        "W9ProofV1", "correlation-1", JsonSerializer.SerializeToElement(new { value = 7 }),
        messageId: "message-1", causationId: "cause-1", aggregateType: "Trip",
        aggregateId: "trip-1", aggregateVersion: 3, sagaId: "saga-1", operationKey: "trip-1:authorize:v1",
        occurredAt: DateTimeOffset.UtcNow);

    [Fact]
    public void CreatePreservesStableIdentityAndPayloadAcrossRepublish()
    {
        var envelope = Envelope();
        var row = OutboxMessage.Create(envelope, "ride.commands", "proof.queue", new Dictionary<string, object?>
        {
            ["traceparent"] = "00-11111111111111111111111111111111-2222222222222222-01",
            ["custom"] = Encoding.UTF8.GetBytes("header-value"),
        });

        Assert.Equal(envelope.MessageId, row.MessageId);
        Assert.Equal(envelope.MessageType, row.MessageType);
        Assert.Equal(envelope.CorrelationId, row.CorrelationId);
        Assert.Equal(envelope.OccurredAt, row.OccurredAt);
        Assert.Equal(envelope.OccurredAt, row.NextAttemptAt);
        Assert.Equal(OutboxStatus.Pending, row.Status);
        Assert.Equal(0, row.AttemptCount);
        Assert.Equal("header-value", row.TransportHeaders()["custom"]);
        Assert.Equal("00-11111111111111111111111111111111-2222222222222222-01",
            row.TransportHeaders()["traceparent"]);

        var roundTripped = row.ToEnvelope();
        Assert.Equal(envelope.MessageId, roundTripped.MessageId);
        Assert.Equal(7, roundTripped.Payload.GetProperty("value").GetInt32());
        Assert.Equal("trip-1:authorize:v1", roundTripped.OperationKey);

        var body = row.SerializeBody();
        var wire = JsonSerializer.Deserialize<MessageEnvelope<JsonElement>>(body)!;
        Assert.Equal(envelope.MessageId, wire.MessageId);
        Assert.Equal(envelope.CorrelationId, wire.CorrelationId);
        Assert.Equal(7, wire.Payload.GetProperty("value").GetInt32());
    }

    [Fact]
    public void CreateInjectsCurrentTraceContextWithoutPuttingItInTheEnvelope()
    {
        using var activity = new Activity("outbox-create").SetIdFormat(ActivityIdFormat.W3C).Start();
        var row = OutboxMessage.Create(Envelope(), "ride.commands", "proof.queue");

        Assert.True(row.TransportHeaders().ContainsKey(TraceHeaderPropagation.TraceParentHeader));
        Assert.DoesNotContain("traceparent", Encoding.UTF8.GetString(row.SerializeBody()), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EmbeddedSchemaResourcesArePackaged()
    {
        var resources = typeof(OutboxMessage).Assembly.GetManifestResourceNames();
        Assert.Contains(resources, name => name.EndsWith("001_outbox_messages.sql", StringComparison.Ordinal));
        Assert.Contains(resources, name => name.EndsWith("001_processed_messages.sql", StringComparison.Ordinal));
    }

    [Fact]
    public void InvalidIdentityAndStatusAreRejectedBeforePersistence()
    {
        var envelope = Envelope();
        Assert.Throws<ArgumentException>(() => OutboxMessage.Create(envelope, "", "proof.queue"));
        Assert.Throws<ArgumentException>(() => OutboxMessage.Create(envelope, "ride.commands", " "));

        var row = OutboxMessage.Create(envelope, "ride.commands", "proof.queue");
        Assert.Throws<ArgumentException>(() => (row with { MessageId = " " }).Validate());
        Assert.Throws<ArgumentException>(() => (row with { Status = OutboxStatus.Published }).Validate());
        (row with { Status = OutboxStatus.Published, PublishedAt = DateTimeOffset.UtcNow }).Validate();
    }
}
