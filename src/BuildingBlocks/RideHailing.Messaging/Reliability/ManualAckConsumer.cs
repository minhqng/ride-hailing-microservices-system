using System.Diagnostics;
using System.Text.Json;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RideHailing.Messaging.Contracts;

namespace RideHailing.Messaging.Reliability;

/// <summary>Handler must return only after durable commit; caller supervises Completion and disposes on fault.</summary>
public sealed class ManualAckConsumer : IAsyncDisposable
{
    private readonly IConnection connection;
    private readonly IChannel channel;
    private readonly ConfirmedPublisher publisher;
    private readonly CancellationTokenSource stopping = new();
    private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string consumerTag = "";
    private long acknowledged;
    private int disposed;
    public Task Completion => completion.Task;
    public long AcknowledgedCount => Interlocked.Read(ref acknowledged);

    private ManualAckConsumer(IConnection connection, IChannel channel, ConfirmedPublisher publisher)
    { this.connection = connection; this.channel = channel; this.publisher = publisher; }

    public static async Task<ManualAckConsumer> StartAsync(ConnectionFactory factory, ConfirmedPublisher publisher,
        string queue, IReadOnlySet<string> supportedTypes,
        Func<MessageEnvelope<JsonElement>, CancellationToken, Task> commitHandler,
        ushort prefetch = 16, CancellationToken token = default)
    {
        if (prefetch is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(prefetch));
        if (supportedTypes.Count == 0) throw new ArgumentException("Explicit supported types required");
        var connection = await factory.CreateConnectionAsync(token);
        IChannel? channel = null;
        try
        {
            channel = await connection.CreateChannelAsync(new CreateChannelOptions(false, false, consumerDispatchConcurrency: 1), token);
            await channel.BasicQosAsync(0, prefetch, global: false, token);
            var runtime = new ManualAckConsumer(connection, channel, publisher);
            var consumer = new AsyncEventingBasicConsumer(channel);
            consumer.ReceivedAsync += (_, delivery) => runtime.ReceiveAsync(queue, supportedTypes, commitHandler, delivery);
            channel.ChannelShutdownAsync += (_, args) =>
            {
                if (!runtime.stopping.IsCancellationRequested)
                    runtime.completion.TrySetException(new IOException($"Consumer channel closed: {args.ReplyCode}"));
                return Task.CompletedTask;
            };
            runtime.consumerTag = await channel.BasicConsumeAsync(queue, autoAck: false, consumer, token);
            return runtime;
        }
        catch
        {
            if (channel is not null) await channel.DisposeAsync();
            await connection.DisposeAsync();
            throw;
        }
    }

    private async Task ReceiveAsync(string queue, IReadOnlySet<string> supportedTypes,
        Func<MessageEnvelope<JsonElement>, CancellationToken, Task> handler, BasicDeliverEventArgs delivery)
    {
        // RabbitMQ owns delivery memory. Copy before any asynchronous work.
        var oversized = delivery.Body.Length > 1024 * 1024;
        var body = oversized ? [] : delivery.Body.ToArray();
        var properties = new BasicProperties(delivery.BasicProperties);
        var attempt = 0;
        MessageEnvelope<JsonElement>? envelope = null;
        try
        {
            DeliveryFailureException? failure = null;
            try
            {
                if (oversized) throw new ArgumentException("Message too large");
                if (properties.Headers?.TryGetValue("x-ride-attempt", out var value) == true)
                    attempt = value is int number ? number : throw new ArgumentException("Invalid retry counter");
                new RetryMetadata(attempt).Validate();
                envelope = JsonSerializer.Deserialize<MessageEnvelope<JsonElement>>(body) ?? throw new ArgumentException("Missing envelope");
                envelope.Validate();
                if (!supportedTypes.Contains(envelope.MessageType)) throw new ArgumentException("Unsupported message type");
                if (properties.MessageId != envelope.MessageId || properties.CorrelationId != envelope.CorrelationId || properties.Type != envelope.MessageType)
                    throw new ArgumentException("Transport identities mismatch envelope");
            }
            catch (Exception error) when (error is JsonException or ArgumentException)
            { failure = new DeliveryFailureException(FailureKind.Poison, "INVALID_MESSAGE"); }

            TraceHeaderPropagation.TryExtract(properties.Headers, out var parent);
            using var activity = ConfirmedPublisher.Activities.StartActivity("consume", ActivityKind.Consumer, parent);
            activity?.SetTag("messaging.message.id", properties.MessageId);
            activity?.SetTag("messaging.correlation.id", properties.CorrelationId);
            if (failure is null)
            {
                try { await handler(envelope!, stopping.Token); }
                catch (DeliveryFailureException error) { failure = error; }
                catch (OperationCanceledException) when (stopping.IsCancellationRequested) { return; }
                catch (Exception) { failure = new DeliveryFailureException(FailureKind.Configuration, "UNCLASSIFIED_HANDLER_FAILURE"); }
            }
            if (failure is not null)
            {
                var action = FailurePolicy.Decide(failure.Kind, new RetryMetadata(Math.Clamp(attempt, 0, 3)));
                if (action == RecoveryAction.Retry)
                {
                    await Task.Delay(FailurePolicy.Delay(attempt), stopping.Token);
                    properties.Headers ??= new Dictionary<string, object?>();
                    properties.Headers["x-ride-attempt"] = attempt + 1;
                    await publisher.PublishRawAsync("", queue, properties, body, stopping.Token);
                }
                else
                {
                    // UNKNOWN is sent to visible error handling for owner reconciliation, never normal retry.
                    static string Identity(string? value) => string.IsNullOrWhiteSpace(value) ? "missing" : value[..Math.Min(value.Length, 256)];
                    var code = System.Text.RegularExpressions.Regex.IsMatch(failure.Code, "^[A-Z0-9_]{1,64}$") ? failure.Code : "HANDLER_FAILURE";
                    var error = new DeliveryError(Identity(properties.MessageId), Identity(properties.CorrelationId),
                        Identity(properties.Type), code, failure.Kind, Math.Clamp(attempt, 0, 3), DateTimeOffset.UtcNow);
                    var errorEnvelope = MessageEnvelope<DeliveryError>.Create("DeliveryErrorV1", error.CorrelationId, error,
                        causationId: string.IsNullOrWhiteSpace(properties.MessageId) ? null : properties.MessageId);
                    await publisher.PublishAsync("ride.deadletter", queue, errorEnvelope, stopping.Token);
                }
            }
            // Success: handler committed. Failure: retry/error transfer was confirmed. Only now ACK.
            await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, stopping.Token);
            Interlocked.Increment(ref acknowledged);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        catch (Exception error)
        {
            // No ACK after failed/uncertain handoff. Supervisor disposes this connection, allowing redelivery.
            completion.TrySetException(error);
            stopping.Cancel();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stopping.Cancel();
        try
        {
            if (channel.IsOpen && consumerTag.Length > 0)
                await channel.BasicCancelAsync(consumerTag);
        }
        finally
        {
            await channel.DisposeAsync();
            await connection.DisposeAsync();
            completion.TrySetResult();
        }
    }
}
