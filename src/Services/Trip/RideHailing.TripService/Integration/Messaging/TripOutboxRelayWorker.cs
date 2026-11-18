// Trip Outbox relay host.
//
// Trip owns semantic intent creation; the shared relay owns
// claim/publish/confirm/mark, retry and backlog metrics. This worker only
// supplies the Trip-owned lifetime: run the relay one cycle at a time, and
// only while the Trip schema
// is migrated and the broker publish path is actually usable.
//
// Why gate publication at all: a cycle against an unreachable broker burns
// the shared retry budget (attempt_count → MaxAttempts → FAILED_VISIBLE,
// which claims never revisit) while the business transition is perfectly
// valid. While the broker is unavailable the correct state is a durable
// PENDING row, not a failed message.
//
// Publication safety is NOT inbound consumer availability. The consumers and
// the publisher hold independent connections, so a consumer-only fault (for
// example a queue argument mismatch that keeps TripMessagingWorker in its
// reconnect loop) must not stall outgoing delivery while the broker is
// healthy. While the consumer readiness flag is down, the publish path is
// therefore verified independently: open a connection and re-declare the
// idempotent topology. That is exactly the precondition the publisher relies
// on — durable exchanges declared first in RideTopology, so mandatory
// publishes cannot be silently unroutable.
//
// The schema itself is created by EF migrations (TripDatabaseReadiness blocks
// on pending migrations). This worker never calls EnsureSchemaAsync: one
// migration authority per service database.

using Microsoft.Extensions.Diagnostics.HealthChecks;
using RabbitMQ.Client;
using RideHailing.Messaging.Outbox;
using RideHailing.Messaging.Topology;

namespace RideHailing.TripService.Integration.Messaging;

/// <summary>Runs the shared Outbox relay for the Trip-owned database.</summary>
public sealed class TripOutboxRelayWorker(
    IServiceProvider services,
    ConnectionFactory factory,
    TripDatabaseReadiness database,
    TripMessagingReadiness messaging,
    ILogger<TripOutboxRelayWorker> log) : BackgroundService
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);
    private OutboxRelay? relay;
    private bool databaseReady;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await ReadyAsync(stoppingToken)) continue;

                // Resolve after readiness so a malformed/unreachable database
                // configuration surfaces as NotReady instead of a startup crash.
                relay ??= services.GetRequiredService<OutboxRelay>();
                var claimed = await relay.RunOnceAsync(stoppingToken);
                if (claimed == 0)
                    await Task.Delay(relay.Options.PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Re-probe the owned database after any cycle fault rather than
                // trusting a stale readiness result.
                databaseReady = false;
                // Never log payloads or credentials.
                log.LogWarning("Trip Outbox relay cycle failed ({ErrorType}); retrying.", ex.GetType().Name);
                await DelayAsync(stoppingToken);
            }
        }
    }

    private async Task<bool> ReadyAsync(CancellationToken token)
    {
        // Migrations are a startup barrier, not a per-poll query.
        if (!databaseReady)
        {
            var databaseHealth = await database.CheckHealthAsync(new HealthCheckContext(), token);
            if (databaseHealth.Status != HealthStatus.Healthy)
            {
                log.LogInformation("Trip Outbox relay waiting for owned database migrations.");
                await DelayAsync(token);
                return false;
            }

            databaseReady = true;
        }

        if (messaging.Ready) return true;

        // Consumers disconnected: verify the publish path independently so a
        // consumer-only fault cannot stall outgoing delivery, while a genuinely
        // unreachable broker still blocks without burning publication attempts.
        if (await BrokerPublishPathReadyAsync(token)) return true;

        // Pending rows remain durable; this is not a publication attempt.
        await DelayAsync(token);
        return false;
    }

    private async Task<bool> BrokerPublishPathReadyAsync(CancellationToken token)
    {
        try
        {
            await using var connection = await factory.CreateConnectionAsync(token);
            await RideTopology.DeclareAsync(connection, token);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogInformation(
                "Trip Outbox relay publish path not ready ({ErrorType}); durable backlog waits.",
                ex.GetType().Name);
            return false;
        }
    }

    private static async Task DelayAsync(CancellationToken token)
    {
        try { await Task.Delay(RetryDelay, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
}
