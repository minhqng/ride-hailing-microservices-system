// Hardening: chk_drivers_coherence (DSI-002 at the persistence boundary).
// Proves every legal state inserts and every incoherent combination is rejected
// with 23514, independent of the domain guards. Also proves the 002 pre-flight
// fails LOUD (clear message) on a dirty 001 dataset instead of an obscure abort.
using Npgsql;

namespace RideHailing.DriverService.IntegrationTests;

[Collection(DriverPostgresCollection.Name)]
public sealed class DriverCoherenceTests
{
    private readonly DriverPostgresDatabase _db;

    public DriverCoherenceTests(DriverPostgresDatabase db)
    {
        _db = db;
    }

    public static TheoryData<string, string?> LegalStates() => new()
    {
        { "OFFLINE", null },
        { "AVAILABLE", null },
        { "RESERVED", "refs" },
        { "ON_TRIP", "active" },
    };

    [Theory]
    [MemberData(nameof(LegalStates))]
    public async Task Legal_State_Inserts(string status, string? shape)
    {
        var id = Guid.NewGuid();
        await using var conn = new NpgsqlConnection(_db.ConnectionString);
        await conn.OpenAsync();
        try
        {
            await using var cmd = new NpgsqlCommand(
                "INSERT INTO drivers (driver_id, user_id, status, reserved_trip_id, reservation_id, active_trip_id) " +
                "VALUES (@id, @user, @status, @rt, @r, @at)", conn);
            cmd.Parameters.AddWithValue("id", id);
            cmd.Parameters.AddWithValue("user", Guid.NewGuid());
            cmd.Parameters.AddWithValue("status", status);
            cmd.Parameters.AddWithValue("rt", shape == "refs" ? (object)Guid.NewGuid() : DBNull.Value);
            cmd.Parameters.AddWithValue("r", shape == "refs" ? (object)Guid.NewGuid() : DBNull.Value);
            cmd.Parameters.AddWithValue("at", shape == "active" ? (object)Guid.NewGuid() : DBNull.Value);
            await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            await using var cleanup = new NpgsqlCommand(
                "DELETE FROM drivers WHERE driver_id = @id", conn);
            cleanup.Parameters.AddWithValue("id", id);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    public static TheoryData<string, string> IllegalStates() => new()
    {
        { "AVAILABLE", "reserved-ref" },
        { "AVAILABLE", "active-ref" },
        { "OFFLINE", "reservation-ref" },
        { "OFFLINE", "active-ref" },
        { "RESERVED", "missing-reservation" },
        { "RESERVED", "with-active" },
        { "ON_TRIP", "missing-active" },
        { "ON_TRIP", "with-reservation" },
    };

    [Theory]
    [MemberData(nameof(IllegalStates))]
    public async Task Incoherent_State_Rejected(string status, string shape)
    {
        var (rt, r, at) = shape switch
        {
            "reserved-ref" => ((object)Guid.NewGuid(), (object)DBNull.Value, (object)DBNull.Value),
            "active-ref" => (DBNull.Value, DBNull.Value, (object)Guid.NewGuid()),
            "reservation-ref" => (DBNull.Value, (object)Guid.NewGuid(), DBNull.Value),
            "trip-only" => ((object)Guid.NewGuid(), DBNull.Value, DBNull.Value),
            "missing-reservation" => ((object)Guid.NewGuid(), DBNull.Value, DBNull.Value),
            "with-active" => ((object)Guid.NewGuid(), (object)Guid.NewGuid(), (object)Guid.NewGuid()),
            "missing-active" => (DBNull.Value, DBNull.Value, DBNull.Value),
            "with-reservation" => (DBNull.Value, (object)Guid.NewGuid(), (object)Guid.NewGuid()),
            _ => throw new InvalidOperationException("Unknown shape."),
        };

        await using var conn = new NpgsqlConnection(_db.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO drivers (driver_id, user_id, status, reserved_trip_id, reservation_id, active_trip_id) " +
            "VALUES (@id, @user, @status, @rt, @r, @at)", conn);
        cmd.Parameters.AddWithValue("id", Guid.NewGuid());
        cmd.Parameters.AddWithValue("user", Guid.NewGuid());
        cmd.Parameters.AddWithValue("status", status);
        cmd.Parameters.AddWithValue("rt", (object)rt);
        cmd.Parameters.AddWithValue("r", (object)r);
        cmd.Parameters.AddWithValue("at", (object)at);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Equal("23514", ex.SqlState);
    }

    [Fact]
    public async Task Dirty_Dataset_Fails_002_With_Clear_Message()
    {
        await using var conn = new NpgsqlConnection(_db.ConnectionString);
        await conn.OpenAsync();

        static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
        {
            await using var cmd = new NpgsqlCommand(sql, connection);
            await cmd.ExecuteNonQueryAsync();
        }

        var dirty = Guid.NewGuid();
        try
        {
            // Simulate a dirty 001 dataset: drop the guard, plant incoherence.
            await ExecuteAsync(conn, "ALTER TABLE drivers DROP CONSTRAINT IF EXISTS chk_drivers_coherence");
            var badUser = Guid.NewGuid();
            var badTrip = Guid.NewGuid();
            await using (var plant = new NpgsqlCommand(
                "INSERT INTO drivers (driver_id, user_id, status, reserved_trip_id) " +
                "VALUES (@id, @user, 'AVAILABLE', @trip)", conn))
            {
                plant.Parameters.AddWithValue("id", dirty);
                plant.Parameters.AddWithValue("user", badUser);
                plant.Parameters.AddWithValue("trip", badTrip);
                await plant.ExecuteNonQueryAsync();
            }

            var ex = await Assert.ThrowsAsync<PostgresException>(() =>
                ExecuteAsync(conn, DriverPostgresDatabase.LoadMigrationScript("002_driver_coherence.sql")));
            Assert.Contains("pre-check failed", ex.MessageText);
        }
        finally
        {
            await using (var wipe = new NpgsqlCommand(
                "DELETE FROM drivers WHERE driver_id = @id", conn))
            {
                wipe.Parameters.AddWithValue("id", dirty);
                await wipe.ExecuteNonQueryAsync();
            }

            await _db.ReapplyScriptsAsync();
        }
    }
}
