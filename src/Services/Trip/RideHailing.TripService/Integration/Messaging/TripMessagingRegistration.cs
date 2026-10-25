using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using RabbitMQ.Client;
using RideHailing.Messaging.Outbox;
using RideHailing.Messaging.Reliability;
using RideHailing.TripService.Infrastructure.Persistence;

namespace RideHailing.TripService.Integration.Messaging;

public sealed record TripMessagingSettings(bool Enabled);

public sealed class TripDatabaseReadiness(IServiceScopeFactory scopes) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken token = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<TripDbContext>();
            var pending = await db.Database.GetPendingMigrationsAsync(timeout.Token);
            return pending.Any() ? HealthCheckResult.Unhealthy("Trip migrations must be applied.") : HealthCheckResult.Healthy();
        }
        catch (Exception) when (!token.IsCancellationRequested)
        { return HealthCheckResult.Unhealthy("Trip database is unavailable."); }
    }
}

public sealed class TripMessagingReadiness : IHealthCheck
{
    private int ready;
    public bool Ready { get => Volatile.Read(ref ready) == 1; internal set => Volatile.Write(ref ready, value ? 1 : 0); }
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken token = default) =>
        Task.FromResult(Ready ? HealthCheckResult.Healthy() :
            HealthCheckResult.Unhealthy("Trip messaging consumers are not connected."));
}

public static class TripMessagingRegistration
{
    public static IServiceCollection AddTripMessaging(this IServiceCollection services, IConfiguration config)
    {
        var enabled = config.GetValue<bool>("TripMessaging:Enabled");
        services.AddSingleton(new TripMessagingSettings(enabled));
        services.AddScoped<TripRideRequestedIntake>();
        services.AddSingleton<TripDatabaseReadiness>();
        services.AddHealthChecks().AddCheck<TripDatabaseReadiness>("trip-database", tags: ["ready"]);
        if (!enabled) return services;

        string Required(string key) => !string.IsNullOrWhiteSpace(config[key]) ? config[key]! :
            throw new InvalidOperationException($"Missing messaging configuration '{key}'.");
        services.AddSingleton(new ConnectionFactory
        {
            HostName = Required("RabbitMq:Host"),
            UserName = Required("RabbitMq:Username"),
            Password = Required("RabbitMq:Password"),
            Port = config.GetValue<int?>("RabbitMq:Port") ?? 5672,
            VirtualHost = config["RabbitMq:VirtualHost"] ?? "/",
            // The worker supervises connection/channel replacement explicitly.
            AutomaticRecoveryEnabled = false,
            RequestedConnectionTimeout = TimeSpan.FromSeconds(10)
        });
        services.AddSingleton<ConfirmedPublisher>();
        services.AddSingleton<TripMessagingReadiness>();
        services.AddHealthChecks().AddCheck<TripMessagingReadiness>("trip-messaging", tags: ["ready"]);
        services.AddScoped<TripMessageHandler>();

        // W9: reuse C's shared relay against the Trip-owned database. A
        // supplies only semantic intents; claim/publish/confirm/mark/retry and
        // outbox.pending / outbox.oldest_pending.age metrics stay shared.
        services.AddSingleton<IOutboxStore>(sp =>
            new NpgsqlOutboxStore(sp.GetRequiredService<NpgsqlDataSource>()));
        services.AddSingleton<IOutboxPublisher, RabbitOutboxPublisher>();
        services.AddSingleton(_ => new OutboxRelayOptions
        {
            RelayId = $"{Environment.MachineName}:{Guid.NewGuid():N}",
            BatchSize = config.GetValue<int?>("Outbox:BatchSize") ?? 8,
            ClaimLease = TimeSpan.FromSeconds(config.GetValue<int?>("Outbox:ClaimLeaseSeconds") ?? 300),
            PollInterval = TimeSpan.FromMilliseconds(config.GetValue<int?>("Outbox:PollIntervalMs") ?? 1000),
            MaxAttempts = config.GetValue<int?>("Outbox:MaxAttempts") ?? 5,
            RetryDelay = TimeSpan.FromSeconds(config.GetValue<int?>("Outbox:RetryDelaySeconds") ?? 1),
        });
        services.AddSingleton<OutboxRelay>();
        services.AddHostedService<TripOutboxRelayWorker>();
        services.AddHostedService<TripMessagingWorker>();
        return services;
    }
}
