// W3 migration-boundary proof (A-W03-07).
//
// Historical: migrates a fresh disposable database specifically through the
// W3 Trip migration target (not to latest), then proves the W2 baseline and
// W3 aggregate are applied with the expected trips contract. Intentionally
// valid after future W4/W5 migrations: it never asserts current head
// contains only W3 schema and never asserts no pending migrations.

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using RideHailing.TripService.Infrastructure.Persistence;

namespace RideHailing.TripService.IntegrationTests;

[Collection(TripPostgresCollection.Name)]
public sealed class TripMigrationBoundaryTests
{
    private readonly TripPostgresFixture _fixture;

    public TripMigrationBoundaryTests(TripPostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task W03_Boundary_AppliesW02Baseline_AndW03Aggregate_WithExpectedTripsContract()
    {
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
            var defined = db.Database.GetMigrations();
            var w03Target = Assert.Single(
                defined.Where(m => m.EndsWith("W03_TripAggregate", StringComparison.Ordinal)).ToList());

            await db.GetService<IMigrator>().MigrateAsync(w03Target);

            var applied = await db.Database.GetAppliedMigrationsAsync();
            Assert.Contains(applied, m => m.EndsWith("W02_TripPersistenceBaseline", StringComparison.Ordinal));
            Assert.Contains(applied, m => m.EndsWith("W03_TripAggregate", StringComparison.Ordinal));

            var connection = db.Database.GetDbConnection();
            await connection.OpenAsync();

            async Task<bool> TableExistsAsync(string table)
            {
                await using var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT 1 FROM pg_tables WHERE schemaname = 'public' AND tablename = $1";
                var p = cmd.CreateParameter();
                p.Value = table;
                cmd.Parameters.Add(p);
                return await cmd.ExecuteScalarAsync() is not null;
            }

            Assert.True(await TableExistsAsync("trips"));

            async Task<HashSet<string>> ColumnNamesAsync(string table)
            {
                await using var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT column_name FROM information_schema.columns WHERE table_schema = 'public' AND table_name = $1";
                var p = cmd.CreateParameter();
                p.Value = table;
                cmd.Parameters.Add(p);
                var names = new HashSet<string>(StringComparer.Ordinal);
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    names.Add(reader.GetString(0));
                }

                return names;
            }

            var columns = await ColumnNamesAsync("trips");
            foreach (var expected in new[]
            {
                "trip_id", "rider_id", "state", "version", "created_at",
                "pickup_latitude", "pickup_longitude",
                "dropoff_latitude", "dropoff_longitude",
                "fare_currency", "fare_amount", "fare_policy_version", "fare_created_at",
                "assigned_driver_id", "reservation_id", "matching_round",
            })
            {
                Assert.Contains(expected, columns);
            }

            // At the W3 boundary no W4+ tables exist: only trips besides history.
            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT tablename FROM pg_tables WHERE schemaname = 'public' " +
                    "AND tablename <> '__EFMigrationsHistory' ORDER BY tablename";
                var tables = new List<string>();
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    tables.Add(reader.GetString(0));
                }

                Assert.Equal(new[] { "trips" }, tables);
            }

            // Durable contract spot-checks: primary key, version non-null,
            // assignment nullable, coherence constraint present.
            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT c.conname FROM pg_constraint c " +
                    "JOIN pg_class t ON t.oid = c.conrelid " +
                    "WHERE t.relname = 'trips' AND c.contype = 'p'";
                var pk = await cmd.ExecuteScalarAsync();
                Assert.NotNull(pk);
            }

            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT is_nullable FROM information_schema.columns " +
                    "WHERE table_schema = 'public' AND table_name = 'trips' AND column_name = 'version'";
                var nullable = (await cmd.ExecuteScalarAsync())?.ToString();
                Assert.Equal("NO", nullable);
            }

            foreach (var assignmentColumn in new[] { "assigned_driver_id", "reservation_id", "matching_round" })
            {
                await using var cmd = connection.CreateCommand();
                cmd.CommandText =
                    "SELECT is_nullable FROM information_schema.columns " +
                    "WHERE table_schema = 'public' AND table_name = 'trips' AND column_name = $1";
                var p = cmd.CreateParameter();
                p.Value = assignmentColumn;
                cmd.Parameters.Add(p);
                var nullable = (await cmd.ExecuteScalarAsync())?.ToString();
                Assert.Equal("YES", nullable);
            }

            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT conname FROM pg_constraint c JOIN pg_class t ON t.oid = c.conrelid " +
                    "WHERE t.relname = 'trips' AND c.conname = 'CK_trips_assignment_coherence'";
                var coherence = await cmd.ExecuteScalarAsync();
                Assert.NotNull(coherence);
            }

            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT conname FROM pg_constraint c JOIN pg_class t ON t.oid = c.conrelid " +
                    "WHERE t.relname = 'trips' AND c.conname = 'CK_trips_state_assignment_coherence'";
                var structural = await cmd.ExecuteScalarAsync();
                Assert.NotNull(structural);
            }
        }
    }
}
