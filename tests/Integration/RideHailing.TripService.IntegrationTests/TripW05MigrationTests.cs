// W05 migration-boundary proofs (A-W05 persistence decision).
//
// Proves the single W5 migration upgrades both from empty and from the
// current-main (W04) migration state with an honest backfill, exposes the
// expected schema/constraints, and leaves no pending model changes. Probe
// databases keep these destructive migration tests isolated from the shared
// component database.

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using RideHailing.TripService.Domain.Trips;
using RideHailing.TripService.Infrastructure.Persistence;

namespace RideHailing.TripService.IntegrationTests;

[Collection(TripPostgresCollection.Name)]
public sealed class TripW05MigrationTests
{
    private readonly TripPostgresFixture _fixture;

    public TripW05MigrationTests(TripPostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private static readonly string[] ExpectedW05Constraints =
    [
        "CK_trips_current_round_non_negative",
        "CK_trips_created_without_round",
        "CK_trips_post_created_have_round",
        "CK_trips_assignment_round_positive",
        "CK_trips_assignment_round_agreement",
    ];

    private async Task<string> CreateProbeDatabaseAsync()
    {
        var probeDb = "trip_w05_probe_" + Guid.NewGuid().ToString("N");
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

        return new NpgsqlConnectionStringBuilder(_fixture.ConnectionString)
        {
            Database = probeDb,
        }.ToString();
    }

    private static TripDbContext ProbeContext(string connectionString) =>
        new(new DbContextOptionsBuilder<TripDbContext>().UseNpgsql(connectionString).Options);

    private static async Task<HashSet<string>> ConstraintNamesAsync(TripDbContext db)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT conname FROM pg_constraint c JOIN pg_class t ON t.oid = c.conrelid " +
            "WHERE t.relname = 'trips'";
        var names = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    [Fact]
    public async Task FromEmpty_MigratesToLatest_WithExpectedW05Schema()
    {
        var probeCs = await CreateProbeDatabaseAsync();
        await using var db = ProbeContext(probeCs);
        await db.Database.MigrateAsync();

        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await connection.OpenAsync();

        async Task<string> NullabilityAsync(string column)
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText =
                "SELECT is_nullable FROM information_schema.columns " +
                "WHERE table_schema = 'public' AND table_name = 'trips' AND column_name = $1";
            cmd.Parameters.Add(new NpgsqlParameter { Value = column });
            return (await cmd.ExecuteScalarAsync())?.ToString() ?? string.Empty;
        }

        Assert.Equal("NO", await NullabilityAsync("current_matching_round"));
        Assert.Equal("NO", await NullabilityAsync("updated_at"));

        var constraints = await ConstraintNamesAsync(db);
        foreach (var expected in ExpectedW05Constraints)
        {
            Assert.Contains(expected, constraints);
        }

        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    [Fact]
    public async Task Upgrade_FromW04_BackfillsRoundAndUpdatedAt_WithNoPendingMigrations()
    {
        var probeCs = await CreateProbeDatabaseAsync();
        await using var db = ProbeContext(probeCs);

        var defined = db.Database.GetMigrations();
        var w04Target = Assert.Single(
            defined.Where(m => m.EndsWith("W04_IdempotencyKeyCollation", StringComparison.Ordinal)).ToList());
        await db.GetService<IMigrator>().MigrateAsync(w04Target);

        // W4-shaped CREATED row: old column set only.
        var tripId = Guid.NewGuid();
        var riderId = Guid.NewGuid();
        var createdAt = new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await connection.OpenAsync();
        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText =
                "INSERT INTO trips (trip_id, rider_id, state, created_at, version, " +
                "pickup_latitude, pickup_longitude, dropoff_latitude, dropoff_longitude, " +
                "fare_currency, fare_amount, fare_policy_version, fare_created_at) " +
                "VALUES ($1, $2, 'CREATED', $3, 0, 10.0, 106.0, 10.1, 106.1, 'VND', 50000, 'p0-w4-v1', $3)";
            insert.Parameters.Add(new NpgsqlParameter { Value = tripId });
            insert.Parameters.Add(new NpgsqlParameter { Value = riderId });
            insert.Parameters.Add(new NpgsqlParameter { Value = createdAt });
            await insert.ExecuteNonQueryAsync();
        }

        await db.Database.MigrateAsync();

        await using var verify = ProbeContext(probeCs);
        var reloaded = await verify.Trips.AsNoTracking().SingleAsync(t => t.TripId == tripId);
        Assert.Equal(TripState.Created, reloaded.State);
        Assert.Equal(0, reloaded.CurrentMatchingRound);
        Assert.Null(reloaded.Assignment);
        Assert.Equal(0, reloaded.Version);
        Assert.True(
            Math.Abs((reloaded.UpdatedAt.ToUniversalTime() - createdAt).TotalMilliseconds) < 1,
            $"Backfilled UpdatedAt drifted: {reloaded.UpdatedAt:o} vs {createdAt:o}");

        Assert.Empty(await verify.Database.GetPendingMigrationsAsync());
    }
}
