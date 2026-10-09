using System.Text.Json;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RideHailing.Messaging.Reliability;
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
/// Retry transfers through the shared bounded retry/error policy (transient/degraded only).</summary>
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
        ConnectionFactory factory, string queue, ushort prefetch, int maxRedeliveries,
        Func<BrokerDelivery, CancellationToken, Task<HandlerOutcome>> handleAsync,
        ILogger logger, CancellationToken stoppingToken)
    {
        if (maxRedeliveries is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(maxRedeliveries));
        factory.AutomaticRecoveryEnabled = false;
        await using (var topologyConnection = await factory.CreateConnectionAsync(stoppingToken))
            await RideTopology.DeclareAsync(topologyConnection, stoppingToken);
        await using var publisher = new ConfirmedPublisher(factory);
        var types = new HashSet<string>(StringComparer.Ordinal)
        {
            "RideRequestedV1", "DriverAvailableV1", "DriverOfflineV1", "DriverReservedV1",
            "DriverReservationReleasedV1", "DriverLocationAcceptedV1", "DriverTripStartedV1", "DriverTripCompletedV1"
        };
        await using var consumer = await ManualAckConsumer.StartDeliveryAsync(factory, publisher, queue, types,
            async (envelope, delivery, ct) =>
            {
                var attempt = delivery.Headers.TryGetValue("x-ride-attempt", out var value) && value is int n ? n : 0;
                if (attempt >= maxRedeliveries)
                    throw new DeliveryFailureException(FailureKind.Configuration, "MATCHING_RETRY_EXHAUSTED");
                try
                {
                    var outcome = await handleAsync(new BrokerDelivery(delivery.RoutingKey, delivery.Body,
                        delivery.Headers, delivery.DeliveryTag, delivery.Redelivered), ct);
                    if (outcome == HandlerOutcome.Retry)
                        throw new DeliveryFailureException(FailureKind.Transient, "PROJECTION_DEGRADED");
                }
                catch (DeliveryFailureException) { throw; }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    var kind = ex switch
                    {
                        JsonException or ArgumentException => FailureKind.Poison,
                        Npgsql.NpgsqlException pg when pg.IsTransient => FailureKind.Transient,
                        TimeoutException or System.Net.Sockets.SocketException => FailureKind.Transient,
                        _ => FailureKind.Configuration
                    };
                    throw new DeliveryFailureException(kind, "MATCHING_HANDLER_FAILED");
                }
            }, prefetch, stoppingToken);
        await consumer.Completion.WaitAsync(stoppingToken);
    }
}
