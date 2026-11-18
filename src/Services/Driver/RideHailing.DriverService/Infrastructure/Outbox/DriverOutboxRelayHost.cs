// Hosting adapter for the shared OutboxRelay. This class adds only
// process lifecycle, topology bootstrap and bounded restart backoff; claim,
// publish-with-confirms, mark and retry semantics all remain in
// RideHailing.Messaging.Outbox.
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RideHailing.Messaging.Outbox;
using RideHailing.Messaging.Topology;

namespace RideHailing.DriverService.Infrastructure.Outbox;

public sealed class DriverOutboxRelayHost(
    OutboxRelay relay,
    ConnectionFactory connectionFactory,
    ILogger<DriverOutboxRelayHost> logger) : BackgroundService
{
    private static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // The confirmed publisher does not declare topology. Declare
                // the canonical durable exchanges/queues before the relay can
                // publish; duplicate declarations are idempotent.
                await using (var connection = await connectionFactory.CreateConnectionAsync(stoppingToken))
                {
                    await RideTopology.DeclareAsync(connection, stoppingToken);
                }

                await relay.RunAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A failed claim/mark (database outage) or a lost broker
                // connection must not terminate the host permanently: pending
                // rows stay durable and the next loop retries. Publication
                // failures inside OutboxRelay are already handled per row.
                logger.LogError(ex,
                    "Driver Outbox relay loop failed; retrying after {Delay}. Pending rows remain durable.",
                    RestartDelay);
                try
                {
                    await Task.Delay(RestartDelay, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }
}
