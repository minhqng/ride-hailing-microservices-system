using RabbitMQ.Client;
using Npgsql;
using RideHailing.DriverService.Infrastructure.Outbox;
using RideHailing.DriverService.Infrastructure.Persistence;
using RideHailing.Messaging.Reliability;
using RideHailing.Messaging.Topology;

namespace RideHailing.DriverService.Application.Messaging;

public sealed class DriverCommandConsumerHost(ConnectionFactory factory, ConfirmedPublisher publisher,
    DriverCommandHandler handler, DriverRepository repository, NpgsqlDataSource dataSource,
    ILogger<DriverCommandConsumerHost> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var driverSchemaReady = false;
        var outboxSchemaReady = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Startup may have encountered an unavailable database. Do not
                // drain valid commands until both owned and shared schemas exist.
                // Each successful bootstrap runs once per host lifetime.
                // A broker reconnect must never rerun table-locking migrations.
                if (!driverSchemaReady)
                {
                    await repository.EnsureTablesAsync(stoppingToken);
                    driverSchemaReady = true;
                }
                if (!outboxSchemaReady)
                {
                    await DriverOutboxSchema.EnsureAsync(dataSource, stoppingToken);
                    outboxSchemaReady = true;
                }
                await using (var connection = await factory.CreateConnectionAsync(stoppingToken))
                    await RideTopology.DeclareAsync(connection, stoppingToken);
                await using var consumer = await ManualAckConsumer.StartAsync(factory, publisher,
                    DriverCommand.Queue, DriverCommand.SupportedTypes, handler.HandleAsync, token: stoppingToken);
                await consumer.Completion.WaitAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Driver command consumer stopped; reconnecting. Unacknowledged deliveries remain retryable.");
                try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
        }
    }
}
