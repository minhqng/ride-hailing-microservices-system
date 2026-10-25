using RabbitMQ.Client;
using RideHailing.Messaging.Outbox;
using RideHailing.Messaging.Reliability;
using RideHailing.Messaging.Topology;

namespace RideHailing.PaymentService.Integration.Payment;

/// <summary>Owns the long-lived Payment command consumer and Outbox relay.</summary>
public sealed class PaymentMessagingHostedService(
    ConnectionFactory connectionFactory,
    ConfirmedPublisher publisher,
    PaymentMessageProcessor processor,
    OutboxRelay relay,
    ILogger<PaymentMessagingHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var relayTask = relay.RunAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using (var topologyConnection = await connectionFactory.CreateConnectionAsync(stoppingToken))
                    await RideTopology.DeclareAsync(topologyConnection, stoppingToken);

                await using var consumer = await ManualAckConsumer.StartAsync(
                    connectionFactory,
                    publisher,
                    "payment.commands.q",
                    PaymentMessageProcessor.SupportedMessageTypes,
                    processor.ProcessAsync,
                    prefetch: 16,
                    token: stoppingToken);

                logger.LogInformation("Payment command consumer and Outbox relay started.");
                var shutdown = Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
                var completed = await Task.WhenAny(consumer.Completion, relayTask, shutdown);
                if (completed == relayTask)
                {
                    await relayTask;
                    throw new InvalidOperationException("Payment Outbox relay stopped unexpectedly.");
                }
                if (stoppingToken.IsCancellationRequested) break;
                await consumer.Completion;
                throw new InvalidOperationException("Payment command consumer stopped unexpectedly.");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception error)
            {
                if (relayTask.IsFaulted) await relayTask;
                logger.LogWarning(error, "Payment consumer failed; reconnecting after bounded delay.");
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
    }
}
