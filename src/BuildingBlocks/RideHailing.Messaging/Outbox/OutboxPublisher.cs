using System.Diagnostics;
using RabbitMQ.Client;
using RideHailing.Messaging.Contracts;
using RideHailing.Messaging.Reliability;

namespace RideHailing.Messaging.Outbox;

public interface IOutboxPublisher
{
    Task PublishAsync(OutboxMessage message, CancellationToken token = default);
}

/// <summary>Publishes the durable row through the confirmed publisher.</summary>
public sealed class RabbitOutboxPublisher(ConfirmedPublisher publisher) : IOutboxPublisher
{
    public async Task PublishAsync(OutboxMessage message, CancellationToken token = default)
    {
        message.Validate();
        var headers = message.TransportHeaders();
        TraceHeaderPropagation.TryExtract(headers, out var parent);
        using var activity = ConfirmedPublisher.Activities.StartActivity("outbox.publish", ActivityKind.Producer, parent);
        activity?.SetTag("messaging.message.id", message.MessageId);
        activity?.SetTag("messaging.correlation.id", message.CorrelationId);
        activity?.SetTag("messaging.message.type", message.MessageType);

        var properties = new BasicProperties
        {
            Persistent = true,
            ContentType = "application/json",
            MessageId = message.MessageId,
            Type = message.MessageType,
            CorrelationId = message.CorrelationId,
            Headers = headers,
        };
        await publisher.PublishRawAsync(message.Exchange, message.RoutingKey, properties,
            message.SerializeBody(), token);
    }
}
