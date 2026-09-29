// Trip W2 component proofs against disposable real PostgreSQL (P3).
//
// Required properties (A-W02-02/04/05/06):
//   A. TripApplication_Boots_WithRealPostgres
//   B. HealthLive_ReturnsSuccess
//   C. HealthReady_ReturnsSuccess
//   D. Migration_Applies_FromEmptyDatabase (+ no leaked business tables)
//   E. LatestMigration_IsRecorded (via __EFMigrationsHistory)
//   F. TripDbContext_CanConnect_ToConfiguredRealPostgres
//
// SCOPE: component-level Trip database connectivity ONLY. A pass here is
// reported as COMPONENT POSTGRES CONNECTION PASS — never as least-privilege
// trip_app or cross-database isolation proof (those need the C-owned
// role/database bootstrap and are evaluated separately).

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using RideHailing.TripService.Infrastructure.Persistence;

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

        var response = await client.GetAsync("/health/ready");
        response.EnsureSuccessStatusCode();
        Assert.Contains("Ready", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Migration_Applies_FromEmptyDatabase()
    {
        // Genuine from-empty proof: a brand-new database on the disposable
        // server, then apply the W2 migration set into it.
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

            // W2 honesty: no business tables may exist yet. Only the EF
            // migration-history relation is allowed.
            var connection = db.Database.GetDbConnection();
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT tablename FROM pg_tables WHERE schemaname = 'public' " +
                "AND tablename <> '__EFMigrationsHistory'";
            var leaked = new List<string>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                leaked.Add(reader.GetString(0));
            }

            Assert.Empty(leaked);
        }
    }

    [Fact]
    public async Task LatestMigration_IsRecorded()
    {
        using var factory = CreateFactory();

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TripDbContext>();
        await db.Database.MigrateAsync();

        var defined = db.Database.GetMigrations();
        var applied = await db.Database.GetAppliedMigrationsAsync();

        Assert.Equal(defined, applied);
        var latest = Assert.Single(applied);
        Assert.EndsWith("W02_TripPersistenceBaseline", latest, StringComparison.Ordinal);
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
}
