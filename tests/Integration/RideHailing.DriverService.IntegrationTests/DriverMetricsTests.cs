// B-W12-01: MeterListener proof over the REAL Driver repository and REAL
// Kestrel/HTTP reserve/release endpoints. No mocked meter, no fabricated
// count: every assertion reads an actual instrument measurement.
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using RideHailing.DriverService.Api;
using RideHailing.DriverService.Application.Location;
using RideHailing.DriverService.Application.Messaging;
using RideHailing.DriverService.Domain.Drivers;
using RideHailing.DriverService.Infrastructure.Faults;
using RideHailing.DriverService.Infrastructure.Persistence;
using RideHailing.DriverService.Infrastructure.Telemetry;
using RideHailing.Messaging.Outbox;
using RideHailing.Messaging.Contracts;

namespace RideHailing.DriverService.IntegrationTests;

[Collection(DriverPostgresCollection.Name)]
public sealed class DriverMetricsTests
{
    private readonly DriverPostgresDatabase _db;

    public DriverMetricsTests(DriverPostgresDatabase db)
    {
        _db = db;
    }

    private async Task<Driver> SeedAvailableAsync(DriverRepository repo)
    {
        var driver = Driver.Create(Guid.NewGuid(), Guid.NewGuid());
        driver.GoOnline(driver.Version);
        Assert.True(await repo.TryInsertAsync(driver));
        return driver;
    }

    [Fact]
    public async Task Repository_Counts_Committed_Effects_Domain_Conflicts_And_Stale_Release_Only()
    {
        using var capture = new MeterCapture();
        var metrics = new DriverMetrics();
        var repo = new DriverRepository(_db.DataSource, _db.OutboxStore, metrics);

        var first = await SeedAvailableAsync(repo);
        var trip = Guid.NewGuid();
        var reservation = Guid.NewGuid();

        var won = await repo.TryReserveAsync(
            first.DriverId, trip, reservation, first.Version, matchingRound: 1);
        Assert.Equal(ReserveOutcome.Won, won.Outcome);
        Assert.Equal(1, capture.Counter("driver_reserve_success_total"));
        Assert.Equal(0, capture.Counter("driver_reserve_conflict_total"));

        // Same logical reservation replayed: no new authoritative effect.
        var replayed = await repo.TryReserveAsync(
            first.DriverId, trip, reservation, first.Version, matchingRound: 1);
        Assert.Equal(ReserveOutcome.Replayed, replayed.Outcome);
        Assert.Equal(1, capture.Counter("driver_reserve_success_total"));

        // Live foreign reservation is a genuine domain conflict.
        var foreign = await repo.TryReserveAsync(
            first.DriverId, Guid.NewGuid(), Guid.NewGuid(), won.Driver!.Version, matchingRound: 1);
        Assert.Equal(ReserveOutcome.Conflict, foreign.Outcome);
        Assert.Equal(1, capture.Counter("driver_reserve_conflict_total", "reservation_conflict"));

        var second = await SeedAvailableAsync(repo);
        var fenced = await repo.TryReserveAsync(
            second.DriverId, Guid.NewGuid(), Guid.NewGuid(), second.Version + 5, matchingRound: 1);
        Assert.Equal(ReserveOutcome.Conflict, fenced.Outcome);
        Assert.Equal(1, capture.Counter("driver_reserve_conflict_total", "version_fence"));

        var missing = await repo.TryReserveAsync(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, matchingRound: 1);
        Assert.Equal(ReserveOutcome.DriverNotFound, missing.Outcome);
        Assert.Equal(1, capture.Counter("driver_reserve_conflict_total", "driver_not_found"));

        // A transport/database failure is not a domain outcome: no metric.
        await using (var dead = NpgsqlDataSource.Create(
            "Host=127.0.0.1;Port=1;Database=driver_db;Username=x;Password=x;Timeout=1;Pooling=false"))
        {
            var failing = new DriverRepository(dead, _db.OutboxStore, metrics);
            await Assert.ThrowsAnyAsync<NpgsqlException>(() =>
                failing.TryReserveAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1,
                    matchingRound: 1));
        }

        Assert.Equal(1, capture.Counter("driver_reserve_success_total"));
        Assert.Equal(3, capture.Counter("driver_reserve_conflict_total"));

        // Fenced stale release is blocked once; exact release applies; the
        // idempotent AVAILABLE-no-refs replay is not a second mutation.
        var staleRelease = await repo.TryReleaseAsync(
            first.DriverId, trip, Guid.NewGuid());
        Assert.Equal(DriverMutationOutcome.Conflict, staleRelease.Outcome);
        Assert.Equal(1, capture.Counter("driver_stale_release_blocked_total", "foreign_reservation"));

        var applied = await repo.TryReleaseAsync(first.DriverId, trip, reservation);
        Assert.Equal(DriverMutationOutcome.Applied, applied.Outcome);
        Assert.Equal(1, capture.Counter("driver_stale_release_blocked_total"));

        var idempotent = await repo.TryReleaseAsync(first.DriverId, trip, reservation);
        Assert.Equal(DriverMutationOutcome.Unchanged, idempotent.Outcome);
        Assert.Equal(1, capture.Counter("driver_stale_release_blocked_total"));
    }

    [Fact]
    public async Task Http_Reserve_And_Release_Emit_The_Same_Truthful_Counts()
    {
        using var capture = new MeterCapture();
        var metrics = new DriverMetrics();
        var repo = new DriverRepository(_db.DataSource, _db.OutboxStore, metrics);
        var driver = await SeedAvailableAsync(repo);

        await using var host = BuildHost(_db.DataSource, metrics);
        await host.StartAsync();
        var address = host.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var http = new HttpClient { BaseAddress = new Uri(address + "/") };

        var trip = Guid.NewGuid();
        var reservation = Guid.NewGuid();
        var success = await http.PostAsJsonAsync($"drivers/{driver.DriverId:N}/reserve", new
        {
            tripId = trip,
            reservationId = reservation,
            expectedVersion = driver.Version,
            matchingRound = 1,
        });
        Assert.Equal(HttpStatusCode.OK, success.StatusCode);
        Assert.Equal(1, capture.Counter("driver_reserve_success_total"));

        var replay = await http.PostAsJsonAsync($"drivers/{driver.DriverId:N}/reserve", new
        {
            tripId = trip,
            reservationId = reservation,
            expectedVersion = driver.Version,
            matchingRound = 1,
        });
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(1, capture.Counter("driver_reserve_success_total"));

        var conflict = await http.PostAsJsonAsync($"drivers/{driver.DriverId:N}/reserve", new
        {
            tripId = Guid.NewGuid(),
            reservationId = Guid.NewGuid(),
            expectedVersion = driver.Version + 1,
            matchingRound = 1,
        });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal(1, capture.Counter("driver_reserve_conflict_total"));

        var staleRelease = await http.PostAsJsonAsync($"drivers/{driver.DriverId:N}/release", new
        {
            tripId = trip,
            reservationId = Guid.NewGuid(),
        });
        Assert.Equal(HttpStatusCode.Conflict, staleRelease.StatusCode);
        Assert.Equal(1, capture.Counter("driver_stale_release_blocked_total",
            "foreign_reservation"));

        var release = await http.PostAsJsonAsync($"drivers/{driver.DriverId:N}/release", new
        {
            tripId = trip,
            reservationId = reservation,
        });
        Assert.Equal(HttpStatusCode.OK, release.StatusCode);
        var noop = await http.PostAsJsonAsync($"drivers/{driver.DriverId:N}/release", new
        {
            tripId = trip,
            reservationId = reservation,
        });
        Assert.Equal(HttpStatusCode.OK, noop.StatusCode);
        Assert.Equal(1, capture.Counter("driver_stale_release_blocked_total"));

        await host.StopAsync();
    }

    [Fact]
    public async Task Broker_Path_Counts_Fenced_Stale_Release_But_Not_Idempotent_Provenance_Refusal()
    {
        using var capture = new MeterCapture();
        var metrics = new DriverMetrics();
        var repo = new DriverRepository(_db.DataSource, _db.OutboxStore, metrics);
        var driver = await SeedAvailableAsync(repo);
        var trip = Guid.NewGuid();
        var reservation = Guid.NewGuid();
        var reserved = await repo.TryReserveAsync(
            driver.DriverId, trip, reservation, driver.Version, matchingRound: 1);
        Assert.Equal(ReserveOutcome.Won, reserved.Outcome);

        // Broker compensation with a foreign reservation is fenced stale.
        var staleCommand = ReleaseCommand(
            driver.DriverId, trip, Guid.NewGuid(), "w12-broker-stale-" + Guid.NewGuid().ToString("N"));
        var stale = await repo.HandleCommandAsync(staleCommand);
        Assert.Equal("Rejected", stale.Disposition);
        Assert.Equal(1, capture.Counter(
            "driver_stale_release_blocked_total", "foreign_reservation"));

        // Exact HTTP release, then a broker compensation for the now-available
        // Driver: it is refused for missing provenance, not counted as stale.
        Assert.Equal(DriverMutationOutcome.Applied,
            (await repo.TryReleaseAsync(driver.DriverId, trip, reservation)).Outcome);
        var provenanceCommand = ReleaseCommand(
            driver.DriverId, trip, reservation, "w12-broker-provenance-" + Guid.NewGuid().ToString("N"));
        var provenance = await repo.HandleCommandAsync(provenanceCommand);
        Assert.Equal("Rejected", provenance.Disposition);
        Assert.Equal(1, capture.Counter("driver_stale_release_blocked_total"));
    }

    private static RideHailing.Messaging.Contracts.MessageEnvelope<JsonElement> ReleaseCommand(
        Guid driverId, Guid tripId, Guid reservationId, string messageId) =>
        RideHailing.Messaging.Contracts.MessageEnvelope<JsonElement>.Create(
            RideHailing.DriverService.Application.Messaging.DriverCommand.Release,
            tripId.ToString("N"),
            JsonSerializer.SerializeToElement(new
            {
                driverId,
                tripId,
                reservationId,
                matchingRound = 1,
            }),
            messageId: messageId);

    private static WebApplication BuildHost(NpgsqlDataSource dataSource, DriverMetrics metrics)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var outbox = new NpgsqlOutboxStore(dataSource);
        builder.Services.AddSingleton(dataSource);
        builder.Services.AddSingleton(outbox);
        builder.Services.AddSingleton<IOutboxStore>(outbox);
        builder.Services.AddSingleton(new DriverRepository(dataSource, outbox, metrics));
        builder.Services.AddSingleton(DriverFaultPoint.Disabled());
        builder.Services.AddOptions<LocationIngressOptions>();
        builder.Services.AddOptions<DriverBrokerOptions>()
            .Configure(options => options.PublishingEnabled = false);
        builder.Services.AddSingleton<LocationAcceptor>();
        builder.Services.AddSingleton<DriverEventPublisher>();

        var app = builder.Build();
        DriverEndpoints.Map(app);
        return app;
    }

    private sealed class MeterCapture : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly ConcurrentDictionary<(string Name, string Tags), long> _counters = new();

        public MeterCapture()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == DriverMetrics.MeterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
                _counters.AddOrUpdate(
                    (instrument.Name, TagString(tags)), value, (_, current) => current + value));
            _listener.Start();
        }

        public long Counter(string name, string? reasonClass = null) =>
            _counters
                .Where(pair => pair.Key.Name == name
                    && (reasonClass is null || pair.Key.Tags.Contains(
                        $"reason_class={reasonClass}", StringComparison.Ordinal)))
                .Sum(pair => pair.Value);

        public void Dispose() => _listener.Dispose();

        private static string TagString(ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var values = new List<string>(tags.Length);
            foreach (var tag in tags)
            {
                values.Add($"{tag.Key}={tag.Value}");
            }

            return string.Join(",", values);
        }
    }
}
