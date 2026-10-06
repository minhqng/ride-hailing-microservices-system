using System.Diagnostics;
using System.Text.Json;
using RabbitMQ.Client;
using RideHailing.Messaging.Contracts;

namespace RideHailing.Messaging.Reliability;

/// <summary>Long-lived, serialized channel. A failed/uncertain publish is never silently retried.</summary>
public sealed class ConfirmedPublisher(ConnectionFactory factory) : IAsyncDisposable
{
    public static readonly ActivitySource Activities = new("RideHailing.Messaging");
    private readonly SemaphoreSlim gate = new(1, 1);
    private IConnection? connection;
    private IChannel? channel;
    private bool disposed;

    public async Task PublishAsync<T>(string exchange, string route, MessageEnvelope<T> envelope, CancellationToken token = default)
    {
        envelope.Validate();
        using var activity = Activities.StartActivity("publish", ActivityKind.Producer);
        activity?.SetTag("messaging.message.id", envelope.MessageId);
        activity?.SetTag("messaging.correlation.id", envelope.CorrelationId);
        var properties = MessageProperties.Create(envelope, activity?.Context ?? Activity.Current?.Context ?? default);
        await PublishRawAsync(exchange, route, properties, JsonSerializer.SerializeToUtf8Bytes(envelope), token);
    }

    public async Task PublishRawAsync(string exchange, string route, BasicProperties properties,
        ReadOnlyMemory<byte> body, CancellationToken token = default)
    {
        if (body.Length > 1024 * 1024) throw new ArgumentException("Message exceeds 1 MiB limit");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await gate.WaitAsync(timeout.Token);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (connection is null || !connection.IsOpen)
            {
                if (channel is not null) await channel.DisposeAsync();
                channel = null;
                if (connection is not null) await connection.DisposeAsync();
                connection = await factory.CreateConnectionAsync(timeout.Token);
            }
            if (channel is null || !channel.IsOpen)
            {
                if (channel is not null) await channel.DisposeAsync();
                channel = await connection.CreateChannelAsync(new CreateChannelOptions(
                    publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), timeout.Token);
            }
            properties.Persistent = true;
            // With confirmation tracking enabled, await covers ACK/NACK and mandatory Basic.Return.
            await channel.BasicPublishAsync(exchange, route, mandatory: true, properties, body, timeout.Token);
        }
        finally { gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (disposed) return;
            disposed = true;
            if (channel is not null) await channel.DisposeAsync();
            if (connection is not null) await connection.DisposeAsync();
        }
        finally { gate.Release(); }
    }
}
