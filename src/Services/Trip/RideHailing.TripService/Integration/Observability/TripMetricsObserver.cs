// Durable-state observer for saga.active (A-W12-02).
//
// WHY A BACKGROUND REFRESHER. The canonical saga_active{step} gauge must
// represent CURRENT durable workflow backlog, not in-memory call counting:
// after a process restart any in-memory count is fiction. This service
// periodically runs one bounded, indexed GROUP BY over ride_sagas
// (status has an index; the non-terminal subset is small) and publishes the
// snapshot to TripDiagnostics. The scrape-time ObservableGauge only reads
// that snapshot, so telemetry never adds a database write to any business
// transaction and never queries per-scrape.
//
// Observation is best-effort: readiness-gated like the Outbox relay (the
// schema must exist), failures log and retry on the next interval, and the
// previous snapshot survives a failed refresh. This worker never starts a
// business transaction and never mutates state.

using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using RideHailing.TripService.Integration.Messaging;
using RideHailing.TripService.Domain.Sagas;

namespace RideHailing.TripService.Integration.Observability;

/// <summary>Reads the durable saga-backlog aggregate behind saga.active.</summary>
public static class TripSagaBacklog
{
    // One indexed aggregate; non-terminal statuses only, grouped by the
    // bounded canonical step vocabulary.
    private const string SagaActiveSql = """
        SELECT current_step, COUNT(*)::bigint
        FROM ride_sagas
        WHERE status IN ('ACTIVE', 'COMPENSATING', 'REQUIRES_REVIEW')
        GROUP BY current_step
        """;

    public static async Task<IReadOnlyDictionary<string, long>> QueryAsync(
        NpgsqlDataSource dataSource, CancellationToken token = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(token);
        await using var command = new NpgsqlCommand(SagaActiveSql, connection);
        await using var reader = await command.ExecuteReaderAsync(token);
        // A successful empty aggregate proves zero backlog for each known step.
        // No snapshot is published when opening or reading the database fails.
        var counts = Enum.GetValues<RideSagaStep>().ToDictionary(
            step => step.ToCanonicalName(), _ => 0L, StringComparer.Ordinal);
        while (await reader.ReadAsync(token))
            counts[reader.GetString(0)] = reader.GetInt64(1);
        return counts;
    }
}

/// <summary>Refreshes the durable saga-backlog snapshot behind saga.active.</summary>
public sealed class TripMetricsObserver(
    IServiceProvider services,
    TripDatabaseReadiness database,
    IConfiguration configuration,
    ILogger<TripMetricsObserver> log) : BackgroundService
{
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(5);

    private readonly TimeSpan interval = ValidatedInterval(configuration);
    private NpgsqlDataSource? dataSource;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var databaseReady = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!databaseReady)
                {
                    var health = await database.CheckHealthAsync(new HealthCheckContext(), stoppingToken);
                    if (health.Status != HealthStatus.Healthy)
                    {
                        await DelayAsync(stoppingToken);
                        continue;
                    }

                    databaseReady = true;
                }

                // Resolve after readiness so a malformed owned-database
                // configuration surfaces as NotReady instead of a startup
                // crash (same lifetime contract as the Outbox relay worker).
                dataSource ??= services.GetRequiredService<NpgsqlDataSource>();
                TripDiagnostics.ObserveSagaActive(await TripSagaBacklog.QueryAsync(dataSource, stoppingToken));
            }
            catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or ArgumentException
                or OperationCanceledException)
            {
                // A schema-pending or unreachable database re-probes next tick;
                // the last durable snapshot stays visible instead of a fake 0.
                databaseReady = false;
                log.LogInformation("Saga backlog observation skipped ({ErrorType}); last snapshot retained.",
                    ex.GetType().Name);
            }

            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    private static TimeSpan ValidatedInterval(IConfiguration configuration)
    {
        var seconds = configuration.GetValue<int?>("TripMetrics:RefreshSeconds");
        if (seconds is null) return DefaultInterval;
        if (seconds is < 5 or > 300)
        {
            throw new InvalidOperationException(
                "TripMetrics:RefreshSeconds must be between 5 and 300 when present.");
        }

        return TimeSpan.FromSeconds(seconds.Value);
    }

    private async Task DelayAsync(CancellationToken token)
    {
        try { await Task.Delay(MinInterval, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
}
