// B-W09-03/04: append-only upgrade proof for 006 on REAL PostgreSQL.
// A legacy database at 005 may already contain NO_DRIVER rows without a
// result identity. 006 must add the new NO_DRIVER identity CHECK as NOT VALID
// (legacy rows stay readable) while enforcing it for every new row, and the
// partial unique index must reject a second logical result reusing an
// identity. 001-005 are never rewritten.
using Npgsql;

namespace RideHailing.MatchingService.IntegrationTests;

[Collection(MatchingPostgresCollection.Name)]
public sealed class MatchingTerminalIdentityMigrationTests
{
    private readonly MatchingPostgresDatabase _db;

    public MatchingTerminalIdentityMigrationTests(MatchingPostgresDatabase db)
    {
        _db = db;
    }

    [Fact]
    public async Task Upgrade_From_005_PreservesLegacyRows_AndEnforcesNewIdentity()
    {
        var legacyNoDriver = Guid.NewGuid();
        var processing = Guid.NewGuid();
        var newNoDriver = Guid.NewGuid();
        var identity = Guid.NewGuid();

        await using var conn = new NpgsqlConnection(_db.ConnectionString);
        await conn.OpenAsync();
        try
        {
            await ExecuteAsync(conn, "DROP TABLE IF EXISTS matching_attempts");
            await ExecuteAsync(conn, "DROP TABLE IF EXISTS matching_rounds");

            foreach (var script in new[]
                     {
                         "001_matching_baseline.sql",
                         "002_matching_round_status.sql",
                         "003_matching_snapshot.sql",
                         "004_matching_dispatch_fence.sql",
                         "004_matching_input_message_id_text.sql",
                         "005_matching_attempt_driver_unique.sql",
                     })
            {
                await ExecuteAsync(conn, MatchingPostgresDatabase.LoadMigrationScript(script));
            }

            await ExecuteAsync(conn,
                "INSERT INTO matching_rounds " +
                "(trip_id, matching_round, input_message_id, status, result_message_id) " +
                "VALUES (@legacy, 1, @m1, 'NO_DRIVER', NULL), " +
                "       (@processing, 1, @m2, 'PROCESSING', NULL)",
                ("legacy", legacyNoDriver),
                ("processing", processing),
                ("m1", Guid.NewGuid().ToString("D")),
                ("m2", Guid.NewGuid().ToString("D")));

            await ExecuteAsync(conn,
                MatchingPostgresDatabase.LoadMigrationScript(
                    "006_matching_terminal_result_identity.sql"));
            await ExecuteAsync(conn,
                MatchingPostgresDatabase.LoadMigrationScript(
                    "007_matching_production_work.sql"));

            // The legacy terminal is preserved (NOT VALID is not a silent repair).
            await using (var legacy = new NpgsqlCommand(
                "SELECT result_message_id FROM matching_rounds WHERE trip_id = @trip", conn))
            {
                legacy.Parameters.AddWithValue("trip", legacyNoDriver);
                Assert.Equal(DBNull.Value, await legacy.ExecuteScalarAsync());
            }

            await using (var constraint = new NpgsqlCommand("""
                SELECT convalidated FROM pg_constraint
                WHERE conname = 'chk_matching_rounds_no_driver_result'
                  AND conrelid = 'matching_rounds'::regclass;
                """, conn))
            {
                Assert.Equal(false, await constraint.ExecuteScalarAsync());
            }

            var terminalizer = new RideHailing.MatchingService.Application.Reserve.MatchingTerminalizer(
                _db.DataSource, _db.OutboxStore);
            var replay = await terminalizer.TryTerminalizeNoDriverAsync(legacyNoDriver, 1);
            Assert.Equal(RideHailing.MatchingService.Application.Reserve.MatchingTerminalOutcome.NoDriver, replay.Outcome);
            Assert.Equal(RideHailing.MatchingService.Application.Messaging.MatchingResultMessageIds.ForNoDriver(legacyNoDriver, 1), replay.ResultMessageId);
            Assert.NotNull(await _db.OutboxStore.LoadAsync(replay.ResultMessageId!));
            Assert.Equal(replay.ResultMessageId,
                (await terminalizer.TryTerminalizeNoDriverAsync(legacyNoDriver, 1)).ResultMessageId);

            // New terminal writes must supply the identity.
            var missingIdentity = await Assert.ThrowsAsync<PostgresException>(() =>
                ExecuteAsync(conn,
                    "INSERT INTO matching_rounds " +
                    "(trip_id, matching_round, input_message_id, status) " +
                    "VALUES (@trip, 1, @msg, 'NO_DRIVER')",
                    ("trip", Guid.NewGuid()), ("msg", Guid.NewGuid().ToString("D"))));
            Assert.Equal("23514", missingIdentity.SqlState);
            Assert.Equal("chk_matching_rounds_no_driver_result", missingIdentity.ConstraintName);

            await ExecuteAsync(conn,
                "INSERT INTO matching_rounds " +
                "(trip_id, matching_round, input_message_id, status, result_message_id) " +
                "VALUES (@trip, 1, @msg, 'NO_DRIVER', @id)",
                ("trip", newNoDriver), ("msg", Guid.NewGuid().ToString("D")), ("id", identity));

            // One identity maps to exactly one logical terminal result.
            var duplicate = await Assert.ThrowsAsync<PostgresException>(() =>
                ExecuteAsync(conn,
                    "INSERT INTO matching_rounds " +
                    "(trip_id, matching_round, input_message_id, status, result_message_id) " +
                    "VALUES (@trip, 1, @msg, 'NO_DRIVER', @id)",
                    ("trip", Guid.NewGuid()), ("msg", Guid.NewGuid().ToString("D")), ("id", identity)));
            Assert.Equal("23505", duplicate.SqlState);
            Assert.Equal("ux_matching_rounds_result_message_id", duplicate.ConstraintName);

            // The untouched PROCESSING row remains legal with a NULL identity.
            await using (var pending = new NpgsqlCommand(
                "SELECT result_message_id FROM matching_rounds WHERE trip_id = @trip", conn))
            {
                pending.Parameters.AddWithValue("trip", processing);
                Assert.Equal(DBNull.Value, await pending.ExecuteScalarAsync());
            }
        }
        finally
        {
            await ExecuteAsync(conn, "DROP TABLE IF EXISTS matching_attempts");
            await ExecuteAsync(conn, "DROP TABLE IF EXISTS matching_rounds");
            await _db.ReapplyScriptsAsync();
        }
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection, string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var cmd = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        await cmd.ExecuteNonQueryAsync();
    }
}
