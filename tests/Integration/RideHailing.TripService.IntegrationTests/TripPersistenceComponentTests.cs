// Trip W3 component proofs against disposable real PostgreSQL.
//
// Required properties (A-W02-02/04/05/06 evolved for A-W03-07):
//   A. TripApplication_Boots_WithRealPostgres
//   B. HealthLive_ReturnsSuccess
//   C. HealthReady_ReturnsSuccess
//   D. Migration_Applies_FromEmptyDatabase (current-head: trips exists, no pending)
//   E. MigrationHistory_PreservesW02Baseline_AndRecordsW03 (durable history)
//   F. TripDbContext_CanConnect_ToConfiguredRealPostgres
//
// SCOPE: component-level Trip database connectivity ONLY. A pass here proves
// component connectivity — never least-privilege trip_app or cross-database
// isolation (those need the C-owned role/database bootstrap and are evaluated
// separately).

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using RideHailing.TripService.Infrastructure.Persistence;
using System.Net;

namespace RideHailing.TripService.IntegrationTests;

[Collection(TripPostgresCollection.Name)]
public sealed class TripPersistenceComponentTests
{
    private readonly TripPostgresFixture _fixture;

    public TripPersistenceComponentTests(TripPostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private TripApplicationFactory CreateFactory() => new(_fixture.ConnectionString);

    [Fact]
    public async Task TripApplication_Boots_WithRealPostgres()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TripDbContext>();
        Assert.NotNull(db);

        var response = await client.GetAsync("/info");
        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task HealthLive_ReturnsSuccess()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/live");
        response.EnsureSuccessStatusCode();
        Assert.Contains("Live", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task HealthReady_ReturnsSuccess()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<TripDbContext>().Database.MigrateAsync();

        var response = await client.GetAsync("/health/ready");
        response.EnsureSuccessStatusCode();
        Assert.Contains("Ready", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Migration_Applies_FromEmptyDatabase()
    {
        // Current-head from-empty proof: a brand-new database on the disposable
        // server, then apply the full current migration set into it.
        var probeDb = "trip_probe_" + Guid.NewGuid().ToString("N");
        var maintenanceCs = new NpgsqlConnectionStringBuilder(_fixture.ConnectionString)
        {
            Database = "postgres",
        }.ToString();

        var maintenanceOptions = new DbContextOptionsBuilder<TripDbContext>()
            .UseNpgsql(maintenanceCs).Options;
        await using (var maintenance = new TripDbContext(maintenanceOptions))
        {
            var connection = maintenance.Database.GetDbConnection();
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE \"{probeDb}\"";
            await command.ExecuteNonQueryAsync();
        }

        var probeCs = new NpgsqlConnectionStringBuilder(_fixture.ConnectionString)
        {
            Database = probeDb,
        }.ToString();
        var probeOptions = new DbContextOptionsBuilder<TripDbContext>()
            .UseNpgsql(probeCs).Options;
        await using (var db = new TripDbContext(probeOptions))
        {
            Assert.NotEmpty(await db.Database.GetPendingMigrationsAsync());

            await db.Database.MigrateAsync();

            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            Assert.True(await db.Database.CanConnectAsync());

            // W3 scope: the authoritative trips table must exist at
            // current head. No assertion that it is the ONLY table forever.
            var connection = db.Database.GetDbConnection();
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT tablename FROM pg_tables WHERE schemaname = 'public' " +
                "AND tablename = 'trips'";
            var found = await command.ExecuteScalarAsync();
            Assert.NotNull(found);
            Assert.Equal("trips", found!.ToString());
        }
    }

    [Fact]
    public async Task MigrationHistory_PreservesW02Baseline_AndRecordsW03()
    {
        using var factory = CreateFactory();

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TripDbContext>();
        await db.Database.MigrateAsync();

        var defined = db.Database.GetMigrations();
        var applied = await db.Database.GetAppliedMigrationsAsync();

        Assert.Equal(defined, applied);
        Assert.Contains(defined, m => m.EndsWith("W02_TripPersistenceBaseline", StringComparison.Ordinal));
        Assert.Contains(applied, m => m.EndsWith("W02_TripPersistenceBaseline", StringComparison.Ordinal));
        Assert.Contains(defined, m => m.EndsWith("W03_TripAggregate", StringComparison.Ordinal));
        Assert.Contains(applied, m => m.EndsWith("W03_TripAggregate", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TripDbContext_CanConnect_ToConfiguredRealPostgres()
    {
        using var factory = CreateFactory();

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TripDbContext>();
        await db.Database.MigrateAsync();

        Assert.True(await db.Database.CanConnectAsync());

        // Prove the override took effect: the context must target the
        // disposable test server, never the compose/production host name.
        var configured = db.Database.GetConnectionString();
        Assert.NotNull(configured);
        Assert.DoesNotContain("Host=postgres", configured, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HealthReady_MalformedDatabaseConfig_Returns503_Not500()
    {
        // Readiness is a traffic gate, not a crash reporter: an unparsable
        // owned-database configuration must report NotReady (503) so the
        // orchestrator routes around the pod instead of observing a 500.
        using var factory = new TripApplicationFactory("not-a-valid-connection-string;;");
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("NotReady", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }
}
