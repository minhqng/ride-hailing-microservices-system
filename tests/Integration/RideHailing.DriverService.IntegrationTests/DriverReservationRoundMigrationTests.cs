// W08 migration proof on REAL PostgreSQL (reservation round lineage).
// Path A (fresh): the applied baseline carries the reserved_round provenance
// column with its range and coherence constraints.
// Path B (upgrade): a disposable pre-004 database (001-003 only) keeps a
// historical RESERVED row (no provenance) through the actual 004 runner:
// the extended coherence accepts NULL, a recorded generation round-trips,
// crafted violations fail loud, and 004 re-applies cleanly (idempotent).
using Npgsql;
using RideHailing.DriverService.Infrastructure.Persistence;

namespace RideHailing.DriverService.IntegrationTests;

[Collection(DriverPostgresCollection.Name)]
public sealed class DriverReservationRoundMigrationTests
{
    private readonly DriverPostgresDatabase _db;

    public DriverReservationRoundMigrationTests(DriverPostgresDatabase db)
    {
        _db = db;
    }

    private static async Task<int> ExecuteAsync(
        NpgsqlConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using var cmd = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        return await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<object?> QueryScalarAsync(
        NpgsqlConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using var cmd = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        // SQL NULL surfaces as DBNull.Value, not C# null; normalize so the
        // nullability assertions below test the column, not ADO.NET.
        var scalar = await cmd.ExecuteScalarAsync();
        return scalar == DBNull.Value ? null : scalar;
    }

    [Fact]
    public async Task Applied_Baseline_Has_Provenance_Column_And_Constraints()
    {
        await using var conn = new NpgsqlConnection(_db.ConnectionString);
        await conn.OpenAsync();

        Assert.Equal("integer", (await QueryScalarAsync(conn,
            "SELECT data_type FROM information_schema.columns " +
            "WHERE table_schema = 'public' AND table_name = 'drivers' AND column_name = 'reserved_round'"))!.
            ToString());

        Assert.Equal(1L, await QueryScalarAsync(conn,
            "SELECT COUNT(*) FROM pg_constraint WHERE conname = 'chk_drivers_reserved_round'"));
        Assert.Equal(1L, await QueryScalarAsync(conn,
            "SELECT COUNT(*) FROM pg_constraint WHERE conname = 'chk_drivers_coherence'"));
    }

    [Fact]
    public async Task Pre004_Database_Upgrades_Keeping_Historical_Rows()
    {
        var admin = new NpgsqlConnection(_db.ConnectionString);
        await admin.OpenAsync();
        var upgrade = "round_upgrade_" + Guid.NewGuid().ToString("N");
        var target = new NpgsqlConnectionStringBuilder(_db.ConnectionString)
        {
            Database = upgrade,
            Pooling = false,
        }.ToString();
        try
        {
            await ExecuteAsync(admin, $"CREATE DATABASE \"{upgrade}\"");

            await using var conn = new NpgsqlConnection(target);
            await conn.OpenAsync();

            // Pre-004 dataset: schema through 003, one historical reservation
            // written before the provenance column existed.
            await ExecuteAsync(conn, DriverPostgresDatabase.LoadMigrationScript("001_driver_baseline.sql"));
            await ExecuteAsync(conn, DriverPostgresDatabase.LoadMigrationScript("002_driver_coherence.sql"));
            await ExecuteAsync(conn, DriverPostgresDatabase.LoadMigrationScript("003_driver_identity_nonempty.sql"));
            await ExecuteAsync(conn, DriverPostgresDatabase.LoadMigrationScript("003_driver_reserve_identity_nonempty.sql"));

            var driverId = Guid.NewGuid();
            var trip = Guid.NewGuid();
            var reservation = Guid.NewGuid();
            await ExecuteAsync(conn,
                "INSERT INTO drivers (driver_id, user_id, status, version, reserved_trip_id, reservation_id, active_trip_id) " +
                "VALUES (@d, @u, 'RESERVED', 2, @t, @r, NULL)",
                ("d", (object)driverId), ("u", (object)Guid.NewGuid()),
                ("t", (object)trip), ("r", (object)reservation));

            // Upgrade through the actual runner script.
            await ExecuteAsync(conn, DriverPostgresDatabase.LoadMigrationScript("004_driver_reservation_round.sql"));

            // Historical provenance stays unknown (never fabricated).
            Assert.Null(await QueryScalarAsync(conn,
                "SELECT reserved_round FROM drivers WHERE driver_id = @d", ("d", (object)driverId)));

            // A hardened write records the generation; the column round-trips.
            await ExecuteAsync(conn,
                "INSERT INTO drivers (driver_id, user_id, status, version, reserved_trip_id, reservation_id, active_trip_id, updated_at, reserved_round) " +
                "VALUES (@d, @u, 'RESERVED', 1, @t, @r, NULL, now(), 7)",
                ("d", (object)Guid.NewGuid()), ("u", (object)Guid.NewGuid()),
                ("t", (object)Guid.NewGuid()), ("r", (object)Guid.NewGuid()));
            Assert.NotNull(await QueryScalarAsync(conn,
                "SELECT reserved_round FROM drivers WHERE reserved_round = 7"));

            // The extended coherence rejects a round without reservation refs.
            var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(conn,
                "INSERT INTO drivers (driver_id, user_id, status, version, reserved_trip_id, reservation_id, active_trip_id, updated_at, reserved_round) " +
                "VALUES (@d, @u, 'AVAILABLE', 1, NULL, NULL, NULL, now(), 7)",
                ("d", (object)Guid.NewGuid()), ("u", (object)Guid.NewGuid())));
            Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);

            // The range constraint rejects a non-positive generation.
            var range = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(conn,
                "INSERT INTO drivers (driver_id, user_id, status, version, reserved_trip_id, reservation_id, active_trip_id, updated_at, reserved_round) " +
                "VALUES (@d, @u, 'RESERVED', 1, @t, @r, NULL, now(), 0)",
                ("d", (object)Guid.NewGuid()), ("u", (object)Guid.NewGuid()),
                ("t", (object)Guid.NewGuid()), ("r", (object)Guid.NewGuid())));
            Assert.Equal(PostgresErrorCodes.CheckViolation, range.SqlState);

            // Re-application is idempotent (guarded DDL, pre-check passes).
            await ExecuteAsync(conn, DriverPostgresDatabase.LoadMigrationScript("004_driver_reservation_round.sql"));
        }
        finally
        {
            await ExecuteAsync(admin, $"DROP DATABASE IF EXISTS \"{upgrade}\" WITH (FORCE)");
            await admin.DisposeAsync();
        }
    }
}
