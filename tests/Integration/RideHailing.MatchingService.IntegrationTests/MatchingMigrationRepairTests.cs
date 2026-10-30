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
            // Pre-004 UUID schema (001 only): inserts MUST use Guid values here.
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

    [Fact]
    public async Task RepeatedMigration_KeepsUnrelatedStateSentinels_AndDropsOnlyLegacyCheck()
    {
        await using var conn = new NpgsqlConnection(_db.ConnectionString);
        await conn.OpenAsync();

        try
        {
            await ExecuteAsync(conn,
                "ALTER TABLE matching_attempts DROP CONSTRAINT IF EXISTS chk_w12_state_sentinel");
            await ExecuteAsync(conn,
                "ALTER TABLE matching_attempts ADD CONSTRAINT chk_w12_state_sentinel " +
                "CHECK (state IS NULL OR state <> 'SENTINEL_STATE')");

            // Re-apply the entire forward-only chain twice (007 is idempotent).
            await _db.ReapplyScriptsAsync();
            await _db.ReapplyScriptsAsync();

            Assert.Equal(1L, await ConstraintCountAsync(conn, "chk_w12_state_sentinel"));
            Assert.Equal(1L, await ConstraintCountAsync(conn, "chk_matching_attempts_state"));
            Assert.Equal(0L, await ConstraintCountAsync(conn, "matching_attempts_state_check"));

            // The canonical vocabulary still accepts CANCELLED (007's reason
            // for replacing the 001 constraint) and rejects an unknown state.
            var trip = Guid.NewGuid();
            var reservation = Guid.NewGuid();
            await ExecuteAsync(conn, """
                INSERT INTO matching_rounds (trip_id, matching_round, input_message_id, status)
                VALUES (@trip, 1, @message, 'PROCESSING');
                INSERT INTO matching_attempts
                    (trip_id, matching_round, ordinal, driver_id, reservation_id, state)
                VALUES (@trip, 1, 0, @driver, @reservation, 'CANCELLED');
                """,
                ("trip", trip), ("message", $"l5:{trip:N}"),
                ("driver", Guid.NewGuid()), ("reservation", reservation));
            await using (var bad = new NpgsqlCommand(
                "UPDATE matching_attempts SET state = 'NOT_A_STATE' " +
                "WHERE trip_id = @trip AND matching_round = 1", conn))
            {
                bad.Parameters.AddWithValue("trip", trip);
                var ex = await Assert.ThrowsAsync<PostgresException>(() => bad.ExecuteNonQueryAsync());
                Assert.Equal("23514", ex.SqlState);
            }
        }
        finally
        {
            await ExecuteAsync(conn,
                "ALTER TABLE matching_attempts DROP CONSTRAINT IF EXISTS chk_w12_state_sentinel");
        }
    }

    private static async Task<long> ConstraintCountAsync(NpgsqlConnection conn, string name)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT count(*) FROM pg_constraint
            WHERE conname = @name AND conrelid = 'matching_attempts'::regclass;
            """, conn);
        cmd.Parameters.AddWithValue("name", name);
        return (long)(await cmd.ExecuteScalarAsync())!;
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
