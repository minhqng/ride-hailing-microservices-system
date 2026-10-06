// 002 repair proof: the canonical-vocabulary migration must handle the exact
// legacy dataset it was written to repair (rows written under 001's
// OPEN/SEARCHING vocabulary). The reordered 002 drops the legacy CHECK before
// backfilling, so legacy rows become PROCESSING instead of aborting the
// migration mid-script. Runs on real PostgreSQL; restores shared schema.
// All SQL is parameterized (no interpolated values, even GUIDs).
using Npgsql;

namespace RideHailing.MatchingService.IntegrationTests;

[Collection(MatchingPostgresCollection.Name)]
public sealed class MatchingMigrationRepairTests
{
    private readonly MatchingPostgresDatabase _db;

    public MatchingMigrationRepairTests(MatchingPostgresDatabase db)
    {
        _db = db;
    }

    [Fact]
    public async Task Legacy_Rows_Migrate_To_Processing_Under_Reordered_002()
    {
        await using var conn = new NpgsqlConnection(_db.ConnectionString);
        await conn.OpenAsync();

        try
        {
            // Simulate the legacy dataset: 001 vocabulary only.
            await ExecuteAsync(conn, "DROP TABLE IF EXISTS matching_attempts");
            await ExecuteAsync(conn, "DROP TABLE IF EXISTS matching_rounds");
            await ExecuteAsync(conn, MatchingPostgresDatabase.LoadMigrationScript("001_matching_baseline.sql"));

            var legacyOpen = Guid.NewGuid();
            var legacySearching = Guid.NewGuid();
            await ExecuteAsync(conn,
                "INSERT INTO matching_rounds (trip_id, matching_round, input_message_id, status) " +
                "VALUES (@a, 1, @ma, 'OPEN'), (@b, 1, @mb, 'SEARCHING')",
                ("a", (object)legacyOpen), ("ma", Guid.NewGuid()),
                ("b", (object)legacySearching), ("mb", Guid.NewGuid()));

            // The repaired 002 must not abort on legacy rows.
            await ExecuteAsync(conn, MatchingPostgresDatabase.LoadMigrationScript("002_matching_round_status.sql"));

            await using var check = new NpgsqlCommand(
                "SELECT COUNT(*) FROM matching_rounds WHERE status = 'PROCESSING'", conn);
            Assert.Equal(2L, await check.ExecuteScalarAsync());

            // And the legacy vocabulary is now rejected for new rows.
            await using var legacy = new NpgsqlCommand(
                "INSERT INTO matching_rounds (trip_id, matching_round, input_message_id, status) " +
                "VALUES (@t, 1, @m, 'OPEN')", conn);
            legacy.Parameters.AddWithValue("t", Guid.NewGuid());
            legacy.Parameters.AddWithValue("m", Guid.NewGuid());
            var ex = await Assert.ThrowsAsync<PostgresException>(() => legacy.ExecuteNonQueryAsync());
            Assert.Equal("23514", ex.SqlState);
        }
        finally
        {
            await ExecuteAsync(conn, "DROP TABLE IF EXISTS matching_attempts");
            await ExecuteAsync(conn, "DROP TABLE IF EXISTS matching_rounds");
            await _db.ReapplyScriptsAsync();
        }
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using var cmd = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        await cmd.ExecuteNonQueryAsync();
    }
}
