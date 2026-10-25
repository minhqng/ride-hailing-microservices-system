// Trip Outbox relay host (A-W09-04).
//
// A owns Trip semantic intent creation; C owns claim/publish/confirm/mark,
// retry and backlog metrics. This worker only supplies the Trip-owned
// lifetime: run C's relay one cycle at a time, and only while the Trip schema
// is migrated and the broker/topology consumers are actually connected.
//
// Why check readiness every cycle: a single long-lived RunAsync would keep
// claiming rows after the broker disappeared, burning the shared retry budget
// and eventually moving a recoverable intent to FAILED_VISIBLE while the
// business transition is perfectly valid. While the broker is unavailable the
// correct state is a durable PENDING row, not a failed message.
//
// Topology: RideTopology is declared by TripMessagingWorker on every
// connection. Publishing with mandatory=true before the exchange/queue exists
// would be returned as unroutable, which the shared relay also counts as a
// failed attempt.
//
// The schema itself is created by EF migrations (TripDatabaseReadiness blocks
// on pending migrations). This worker never calls EnsureSchemaAsync: one
// migration authority per service database.

using Microsoft.Extensions.Diagnostics.HealthChecks;
using RideHailing.Messaging.Outbox;

namespace RideHailing.TripService.Integration.Messaging;

/// <summary>Runs the shared Outbox relay for the Trip-owned database.</summary>
public sealed class TripOutboxRelayWorker(
    IServiceProvider services,
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

        if (!messaging.Ready)
        {
            // Pending rows remain durable; this is not a publication attempt.
            await DelayAsync(token);
            return false;
        }

        return true;
    }

    private static async Task DelayAsync(CancellationToken token)
    {
        try { await Task.Delay(RetryDelay, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
}
