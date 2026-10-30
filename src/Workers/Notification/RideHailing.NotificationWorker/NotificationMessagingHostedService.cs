using RabbitMQ.Client;
using RideHailing.Messaging.Reliability;
using RideHailing.Messaging.Topology;

namespace RideHailing.NotificationWorker;

public sealed class NotificationMessagingHostedService(
    ConnectionFactory factory,
    ConfirmedPublisher publisher,
    NotificationEventProcessor processor,
    ILogger<NotificationMessagingHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using (var topology = await factory.CreateConnectionAsync(stoppingToken))
                    await RideTopology.DeclareAsync(topology, stoppingToken);

                await using var consumer = await ManualAckConsumer.StartAsync(
                    factory, publisher, "notification.business-events.q",
                    NotificationEventTypes.Supported,
                    async (envelope, token) => { await processor.ProcessAsync(envelope, token); },
                    prefetch: 16, token: stoppingToken);

                logger.LogInformation("Notification consumer connected; delivery remains non-authoritative.");
                var shutdown = Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
                await Task.WhenAny(consumer.Completion, shutdown);
                if (stoppingToken.IsCancellationRequested) break;
                await consumer.Completion;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception error)
            {
                logger.LogWarning(error, "Notification consumer stopped; reconnecting after bounded delay.");
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
    }
}
