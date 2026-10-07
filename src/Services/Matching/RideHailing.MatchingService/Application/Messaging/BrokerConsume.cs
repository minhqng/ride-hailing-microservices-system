// B-W08 minimal manual-ACK consume loop (Doc 02 §9.1 critical pattern).
// Interim B-owned glue, deliberately narrow: connect with reconnect loop
// (never crashes the host over a broker outage), declare the frozen topology
// idempotently, consume one queue with manual ACK, and apply exactly:
//   handler success → ACK (durable effect committed)
//   handler failure → bounded requeue (transient/cancel) or drop+log
//     (permanent/poison/unknown-past-budget; DLQ arrival is C/W9 scope)
// No retry headers of its own, no prefetch tuning beyond validated config,
// no breaker. Converges onto C's consumer base when that lands (same
// exchanges/queues/envelope/ACK-after-commit discipline).
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RideHailing.Messaging.Contracts;
using RideHailing.Messaging.Topology;

namespace RideHailing.MatchingService.Application.Messaging;

public sealed record BrokerDelivery(
    string RoutingKey,
    byte[] Body,
    IDictionary<string, object?> Headers,
    ulong DeliveryTag,
    bool Redelivered);

/// <summary>Handler verdict: Done acknowledges (durable effect committed);
/// Retry requeues for bounded redelivery (transient/degraded only).</summary>
public enum HandlerOutcome
{
    Done,
    Retry,
}

public static class BrokerEnvelope
{
    public static MessageEnvelope<JsonElement> Parse(byte[] body)
    {
        var envelope = JsonSerializer.Deserialize<MessageEnvelope<JsonElement>>(body)
            ?? throw new ArgumentException("Missing message envelope.");
        envelope.Validate();
        return envelope;
    }
}

public static class BrokerConsume
{
    public static async Task RunAsync(
        ConnectionFactory factory,
        string queue,
        ushort prefetch,
        int maxRedeliveries,
        Func<BrokerDelivery, CancellationToken, Task<HandlerOutcome>> handleAsync,
        ILogger logger,
        CancellationToken stoppingToken)
    {
        var attempt = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ConsumeOnceAsync(
                    factory, queue, prefetch, maxRedeliveries, handleAsync, logger, stoppingToken);
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                attempt++;
                var delay = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(attempt, 5))));
                logger.LogWarning(ex, "Broker consume loop failed; retrying in {Delay}s.", delay.TotalSeconds);
                try
                {
                    await Task.Delay(delay, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private static async Task ConsumeOnceAsync(
        ConnectionFactory factory,
        string queue,
        ushort prefetch,
        int maxRedeliveries,
        Func<BrokerDelivery, CancellationToken, Task<HandlerOutcome>> handleAsync,
        ILogger logger,
        CancellationToken stoppingToken)
    {
        await using var connection = await factory.CreateConnectionAsync(stoppingToken);
        await RideTopology.DeclareAsync(connection, stoppingToken);
        await using var channel = await connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: false,
                publisherConfirmationTrackingEnabled: false,
                consumerDispatchConcurrency: 1),
            cancellationToken: stoppingToken);
        await channel.BasicQosAsync(0, prefetch, global: false, cancellationToken: stoppingToken);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += (_, delivery) =>
            DeliverAsync(channel, queue, maxRedeliveries, handleAsync, logger, delivery, stoppingToken);
        await channel.BasicConsumeAsync(queue, autoAck: false, consumer, stoppingToken);

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var stop = stoppingToken.Register(() => done.TrySetResult());
        await done.Task;
    }

    internal static async Task DeliverAsync(
        IChannel channel,
        string queue,
        int maxRedeliveries,
        Func<BrokerDelivery, CancellationToken, Task<HandlerOutcome>> handleAsync,
        ILogger logger,
        BasicDeliverEventArgs delivery,
        CancellationToken ct)
    {
        var headers = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (delivery.BasicProperties?.Headers is not null)
        {
            foreach (var kv in delivery.BasicProperties.Headers)
            {
                headers[kv.Key] = kv.Value;
            }
        }

        var count = BrokerDecisions.DeliveryCount(headers, delivery.Redelivered);
        if (BrokerDecisions.ExceedsBudget(count, maxRedeliveries))
        {
            // Bounded poison path (DLQ arrival is C/W9 scope): drop loudly,
            // never hot-loop, never advance workflow on it.
            logger.LogError(
                "Dropping poison message after {Count} deliveries: queue={Queue}, routing={RoutingKey}.",
                count, queue, delivery.RoutingKey);
            await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: false, ct);
            return;
        }

        if (BrokerDecisions.BodyTooLarge(delivery.Body.Length))
        {
            // Oversized body: never parse (memory-exhaustion guard), drop as
            // poison. The platform publish limit is 1 MiB, so a larger body
            // is either a faulty producer or hostile input either way.
            logger.LogError(
                "Dropping oversized message ({Bytes} bytes): queue={Queue}, routing={RoutingKey}.",
                delivery.Body.Length, queue, delivery.RoutingKey);
            await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: false, ct);
            return;
        }

        try
        {
            var outcome = await handleAsync(
                new BrokerDelivery(
                    delivery.RoutingKey,
                    delivery.Body.ToArray(),
                    headers,
                    delivery.DeliveryTag,
                    delivery.Redelivered),
                ct);
            if (outcome == HandlerOutcome.Retry)
            {
                await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: true, ct);
                return;
            }

            await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, ct);
        }
        catch (Exception ex)
        {
            switch (BrokerDecisions.ClassifyFailure(ex))
            {
                case ConsumeDisposition.Requeue:
                    await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: true, ct);
                    break;
                default:
                    logger.LogError(
                        ex,
                        "Dropping unprocessable message: queue={Queue}, routing={RoutingKey}.",
                        queue, delivery.RoutingKey);
                    await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: false, ct);
                    break;
            }
        }
    }
}
