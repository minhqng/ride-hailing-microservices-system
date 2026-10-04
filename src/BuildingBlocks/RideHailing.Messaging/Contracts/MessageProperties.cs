using System.Diagnostics;
using RabbitMQ.Client;

namespace RideHailing.Messaging.Contracts;

public static class MessageProperties
{
    // Property convention only. Publishing, confirms and ACK lifecycle arrive in W8.
    public static BasicProperties Create<T>(MessageEnvelope<T> envelope, ActivityContext context = default)
    {
        envelope.Validate();
        var headers = new Dictionary<string, object?>();
        TraceHeaderPropagation.Inject(context, headers);
        return new BasicProperties
        {
            Persistent = true,
            ContentType = "application/json",
            MessageId = envelope.MessageId,
            Type = envelope.MessageType,
            CorrelationId = envelope.CorrelationId,
            Headers = headers
        };
    }
}
