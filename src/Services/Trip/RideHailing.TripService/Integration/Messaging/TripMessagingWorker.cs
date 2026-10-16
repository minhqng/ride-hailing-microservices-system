using RabbitMQ.Client;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using RideHailing.Messaging.Reliability;
using RideHailing.Messaging.Topology;

namespace RideHailing.TripService.Integration.Messaging;

public sealed class TripMessagingWorker(ConnectionFactory factory, ConfirmedPublisher publisher,
    IServiceScopeFactory scopes, TripMessagingReadiness readiness, TripDatabaseReadiness databaseReadiness,
    ILogger<TripMessagingWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var consumers = new List<ManualAckConsumer>();
            try
            {
                // Readiness alone does not stop broker deliveries. Never register
                // consumers against an absent/outdated receipt schema.
                var database = await databaseReadiness.CheckHealthAsync(new HealthCheckContext(), stoppingToken);
                if (database.Status != HealthStatus.Healthy)
                {
                    log.LogWarning("Trip consumers waiting for owned database migrations/readiness.");
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                    continue;
                }
                await using (var connection = await factory.CreateConnectionAsync(stoppingToken))
                    await RideTopology.DeclareAsync(connection, stoppingToken);
                foreach (var subscription in TripMessageHandler.Subscriptions)
                {
                    var queue = subscription.Key;
                    consumers.Add(await ManualAckConsumer.StartAsync(factory, publisher, queue, subscription.Value,
                        async (envelope, token) =>
                        {
                            await using var scope = scopes.CreateAsyncScope();
                            await scope.ServiceProvider.GetRequiredService<TripMessageHandler>()
                                .HandleAsync(queue, envelope, token);
                        }, prefetch: 16, token: stoppingToken));
                }
                readiness.Ready = true;
                log.LogInformation("Trip messaging consumers connected.");
                var ended = await Task.WhenAny(consumers.Select(c => c.Completion)).WaitAsync(stoppingToken);
                await ended; // Surface faults; an unexpected clean stop also reconnects.
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                // Never include connection strings, credentials or message payloads.
                log.LogWarning("Trip messaging disconnected ({ErrorType}); reconnecting.", ex.GetType().Name);
            }
            finally
            {
                readiness.Ready = false;
                foreach (var consumer in consumers)
                {
                    try { await consumer.DisposeAsync(); }
                    catch (Exception ex) { log.LogWarning("Consumer disposal failed ({ErrorType}).", ex.GetType().Name); }
                }
            }
            try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
