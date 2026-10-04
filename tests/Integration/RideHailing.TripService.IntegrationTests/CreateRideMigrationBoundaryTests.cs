// W4 migration-boundary proof (A-W04-02/04).
//
// Historical: migrates a fresh disposable database specifically through the
// W4 CreateRide target (not to latest), then proves W2, W3 and W4 are applied
// with the expected contracts: request_idempotency table/columns, composite
// uniqueness, the resource FK with RESTRICT behavior, and the active-Rider
// partial unique index whose predicate holds exactly the six DSI-013 active
// states. Intentionally valid after future W5 migrations: it never asserts
// current head contains only W4 schema.

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using RideHailing.TripService.Infrastructure.Persistence;

namespace RideHailing.TripService.IntegrationTests;

[Collection(TripPostgresCollection.Name)]
public sealed class CreateRideMigrationBoundaryTests
{
    private readonly TripPostgresFixture _fixture;

    public CreateRideMigrationBoundaryTests(TripPostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task W04_Boundary_AppliesW02W03W04_WithIdempotencyAndActiveRiderDefense()
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
        try
        {
            await using (var db = new TripDbContext(probeOptions))
            {
                var defined = db.Database.GetMigrations();
            var w04Target = Assert.Single(
                defined.Where(m => m.EndsWith("W04_CreateRideIdempotency", StringComparison.Ordinal)).ToList());

            await db.GetService<IMigrator>().MigrateAsync(w04Target);

            var applied = await db.Database.GetAppliedMigrationsAsync();
            Assert.Contains(applied, m => m.EndsWith("W02_TripPersistenceBaseline", StringComparison.Ordinal));
            Assert.Contains(applied, m => m.EndsWith("W03_TripAggregate", StringComparison.Ordinal));
            Assert.Contains(applied, m => m.EndsWith("W04_CreateRideIdempotency", StringComparison.Ordinal));

            var connection = db.Database.GetDbConnection();
            await connection.OpenAsync();

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

            async Task<string?> SingleStringAsync(string sql, string? param = null)
            {
                await using var cmd = connection.CreateCommand();
                cmd.CommandText = sql;
                if (param is not null)
                {
                    var p = cmd.CreateParameter();
                    p.Value = param;
                    cmd.Parameters.Add(p);
                }

                return (await cmd.ExecuteScalarAsync())?.ToString();
            }

            // Request-idempotency contract.
            var idempotencyColumns = await ColumnNamesAsync("request_idempotency");
            Assert.Equal(5, idempotencyColumns.Count);
            foreach (var expected in new[] { "scope", "key", "request_fingerprint", "resource_id", "created_at" })
            {
                Assert.Contains(expected, idempotencyColumns);
            }

            Assert.Equal(
                "PK_request_idempotency",
                await SingleStringAsync(
                    "SELECT c.conname FROM pg_constraint c JOIN pg_class t ON t.oid = c.conrelid " +
                    "WHERE t.relname = 'request_idempotency' AND c.contype = 'p'"));

            Assert.Equal(
                "FK_request_idempotency_trips_resource_id",
                await SingleStringAsync(
                    "SELECT c.conname FROM pg_constraint c JOIN pg_class t ON t.oid = c.conrelid " +
                    "WHERE t.relname = 'request_idempotency' AND c.contype = 'f'"));

            // RESTRICT/NO ACTION: deleting a referenced Trip must be refused.
            var fkDef = await SingleStringAsync(
                "SELECT pg_get_constraintdef(c.oid) FROM pg_constraint c JOIN pg_class t ON t.oid = c.conrelid " +
                "WHERE t.relname = 'request_idempotency' AND c.conname = 'FK_request_idempotency_trips_resource_id'");
            Assert.NotNull(fkDef);
            Assert.True(
                fkDef.Contains("ON DELETE RESTRICT", StringComparison.Ordinal) ||
                fkDef.Contains("ON DELETE NO ACTION", StringComparison.Ordinal),
                $"Resource FK must refuse dangling deletes, got '{fkDef}'.");

            // Active-Rider partial unique index: exists, unique, and its
            // predicate holds exactly the six DSI-013 active states.
            var indexDef = await SingleStringAsync(
                "SELECT pg_get_indexdef(indexrelid) FROM pg_index " +
                "WHERE indexrelid = 'public.\"UX_trips_one_active_per_rider\"'::regclass");
            Assert.NotNull(indexDef);
            Assert.Contains("UNIQUE", indexDef, StringComparison.Ordinal);
            foreach (var active in new[] { "CREATED", "MATCHING", "DRIVER_RESERVED", "DRIVER_ACCEPTED", "READY_TO_START", "IN_PROGRESS" })
            {
                Assert.Contains($"'{active}'", indexDef, StringComparison.Ordinal);
            }

            foreach (var terminal in new[] { "COMPLETED", "CANCELLED", "MATCH_FAILED" })
            {
                Assert.DoesNotContain($"'{terminal}'", indexDef, StringComparison.Ordinal);
            }
            }
        }
        finally
        {
            await DropProbeDatabaseAsync(maintenanceOptions, probeDb);
        }
    }

    [Fact]
    public async Task W04_Collation_AppliesOnLatest_WithDownUpRoundTrip()
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
        try
        {
            await using (var db = new TripDbContext(probeOptions))
            {
                var defined = db.Database.GetMigrations();
            var w04Target = Assert.Single(
                defined.Where(m => m.EndsWith("W04_CreateRideIdempotency", StringComparison.Ordinal)).ToList());
            var migrator = db.GetService<IMigrator>();

            async Task<string?> SingleStringAsync(string sql)
            {
                var connection = db.Database.GetDbConnection();
                if (connection.State != System.Data.ConnectionState.Open)
                {
                    await connection.OpenAsync();
                }

                await using var cmd = connection.CreateCommand();
                cmd.CommandText = sql;
                return (await cmd.ExecuteScalarAsync())?.ToString();
            }

            async Task<string?> ColumnCollationAsync(string column)
            {
                var connection = db.Database.GetDbConnection();
                if (connection.State != System.Data.ConnectionState.Open)
                {
                    await connection.OpenAsync();
                }

                await using var cmd = connection.CreateCommand();
                cmd.CommandText =
                    "SELECT collation_name FROM information_schema.columns " +
                    "WHERE table_schema = 'public' AND table_name = 'request_idempotency' AND column_name = $1";
                var p = cmd.CreateParameter();
                p.Value = column;
                cmd.Parameters.Add(p);
                return (await cmd.ExecuteScalarAsync())?.ToString();
            }

            // Latest: opaque-exact key columns pin bytewise collation, and the
            // resource lookup index exists.
            await migrator.MigrateAsync();
            Assert.Equal("C", await ColumnCollationAsync("scope"));
            Assert.Equal("C", await ColumnCollationAsync("key"));
            Assert.Equal(
                "IX_request_idempotency_resource_id",
                await SingleStringAsync(
                    "SELECT indexname FROM pg_indexes WHERE schemaname = 'public' " +
                    "AND tablename = 'request_idempotency' AND indexname = 'IX_request_idempotency_resource_id'"));

            // Down to the W04 target: collation pin is removed, table contract stays.
            await migrator.MigrateAsync(w04Target);
            Assert.NotEqual("C", await ColumnCollationAsync("scope"));
            Assert.Equal(
                "5",
                await SingleStringAsync("SELECT count(*) FROM information_schema.columns " +
                    "WHERE table_schema = 'public' AND table_name = 'request_idempotency'"));

            // Back to latest: pin is re-applied idempotently.
            await migrator.MigrateAsync();
            Assert.Equal("C", await ColumnCollationAsync("scope"));
            Assert.Equal("C", await ColumnCollationAsync("key"));
            }
        }
        finally
        {
            await DropProbeDatabaseAsync(maintenanceOptions, probeDb);
        }
    }

    private static async Task DropProbeDatabaseAsync(
        DbContextOptions<TripDbContext> maintenanceOptions, string probeDb)
    {
        await using var cleanup = new TripDbContext(maintenanceOptions);
        var connection = cleanup.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP DATABASE IF EXISTS \"{probeDb}\" WITH (FORCE)";
        await command.ExecuteNonQueryAsync();
    }
}
