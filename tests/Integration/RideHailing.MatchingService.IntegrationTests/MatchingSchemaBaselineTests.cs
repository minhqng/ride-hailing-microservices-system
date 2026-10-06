// B-W02-04 (+P0 fix): Matching schema proof on REAL PostgreSQL (no fake-green).
// Applies 001 baseline + 002 canonical status fix in order, then proves the
// CHECK enforces Doc 01 §8.2 vocabulary (PROCESSING/RESERVED/NO_DRIVER/CANCELLED/FAILED).
using Npgsql;
using Xunit.Abstractions;

namespace RideHailing.MatchingService.IntegrationTests;

[Collection(MatchingPostgresCollection.Name)]
public class MatchingSchemaBaselineTests
{
    private readonly ITestOutputHelper _out;
    private readonly MatchingPostgresDatabase _db;

    public MatchingSchemaBaselineTests(ITestOutputHelper output, MatchingPostgresDatabase db)
    {
        _out = output;
        _db = db;
    }

    [Fact]
    public async Task Matching_Baseline_Tables_Exist_On_Real_Postgres()
    {
        if (!_db.Ready())
        {
            Assert.Fail("Real PostgreSQL proof required but unavailable: " + _db.SkipReason);
        }

        _out.WriteLine($"INFRA: postgres via {_db.Source} — real assertions will run.");
        var tables = await _db.GetTablesAsync();
        Assert.Contains("matching_rounds", tables);
        Assert.Contains("matching_attempts", tables);
    }

    [Theory]
    [InlineData("PROCESSING")]
    [InlineData("RESERVED")]
    [InlineData("NO_DRIVER")]
    [InlineData("CANCELLED")]
    [InlineData("FAILED")]
    public async Task Canonical_Round_Status_Is_Accepted(string status)
    {
        if (!_db.Ready())
        {
            Assert.Fail("Real PostgreSQL proof required but unavailable: " + _db.SkipReason);
        }

        await using var conn = new NpgsqlConnection(_db.ConnectionString);
        await conn.OpenAsync();
        var trip = Guid.NewGuid();
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO matching_rounds (trip_id, matching_round, input_message_id, status) " +
            "VALUES (@t, 1, @m, @s)", conn);
        cmd.Parameters.AddWithValue("t", trip);
        cmd.Parameters.AddWithValue("m", Guid.NewGuid());
        cmd.Parameters.AddWithValue("s", status);
        await cmd.ExecuteNonQueryAsync();

        await using var cleanup = new NpgsqlCommand(
            "DELETE FROM matching_rounds WHERE trip_id = @t", conn);
        cleanup.Parameters.AddWithValue("t", trip);
        await cleanup.ExecuteNonQueryAsync();
    }

    [Theory]
    [InlineData("OPEN")]
    [InlineData("SEARCHING")]
    public async Task Legacy_Round_Status_Is_Rejected(string status)
    {
        if (!_db.Ready())
        {
            Assert.Fail("Real PostgreSQL proof required but unavailable: " + _db.SkipReason);
        }

        await using var conn = new NpgsqlConnection(_db.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO matching_rounds (trip_id, matching_round, input_message_id, status) " +
            "VALUES (@t, 1, @m, @s)", conn);
        cmd.Parameters.AddWithValue("t", Guid.NewGuid());
        cmd.Parameters.AddWithValue("m", Guid.NewGuid());
        cmd.Parameters.AddWithValue("s", status);
        await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
    }
}
