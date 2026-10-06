// B-W02-03: Driver schema baseline proof on REAL PostgreSQL (no fake-green).
// Infra comes from the shared DriverPostgresDatabase: explicit env (fail loud)
// or disposable Testcontainers. Skips only when Docker is provably absent.
using Npgsql;
using Xunit.Abstractions;

namespace RideHailing.DriverService.IntegrationTests;

[Collection(DriverPostgresCollection.Name)]
public class DriverSchemaBaselineTests
{
    private readonly ITestOutputHelper _out;
    private readonly DriverPostgresDatabase _db;

    public DriverSchemaBaselineTests(ITestOutputHelper output, DriverPostgresDatabase db)
    {
        _out = output;
        _db = db;
    }

    [Fact]
    public async Task Driver_Baseline_Tables_Exist_On_Real_Postgres()
    {
        if (!_db.Ready())
        {
            Assert.Fail("Real PostgreSQL proof required but unavailable: " + _db.SkipReason);
        }

        _out.WriteLine($"INFRA: postgres via {_db.Source} — real assertions will run.");
        var tables = await _db.GetTablesAsync();
        Assert.Contains("drivers", tables);
        Assert.Contains("vehicles", tables);
    }

    [Fact]
    public async Task Driver_Baseline_Scripts_Are_Idempotent()
    {
        if (!_db.Ready())
        {
            Assert.Fail("Real PostgreSQL proof required but unavailable: " + _db.SkipReason);
        }

        // Second full apply must not throw (IF NOT EXISTS / guarded DDL).
        await _db.ReapplyScriptsAsync();
    }

    [Fact]
    public async Task Driver_Coherence_Check_Rejects_Incoherent_Row()
    {
        if (!_db.Ready())
        {
            Assert.Fail("Real PostgreSQL proof required but unavailable: " + _db.SkipReason);
        }

        // DSI-002 defense-in-depth: AVAILABLE carrying a reservation ref must
        // be rejected at the persistence boundary (23514), mirroring the
        // domain AssertCoherence that guards every in-memory transition.
        await using var conn = new NpgsqlConnection(_db.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO drivers (driver_id, user_id, status, version, reserved_trip_id) " +
            "VALUES (@id, @u, 'AVAILABLE', 1, @t)", conn);
        cmd.Parameters.AddWithValue("id", Guid.NewGuid());
        cmd.Parameters.AddWithValue("u", Guid.NewGuid());
        cmd.Parameters.AddWithValue("t", Guid.NewGuid());

        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Equal("23514", ex.SqlState);
    }
}
