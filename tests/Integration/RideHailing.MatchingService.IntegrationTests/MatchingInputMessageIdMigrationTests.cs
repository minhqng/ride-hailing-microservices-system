// B-W08 migration proof on REAL PostgreSQL (mandatory Path A + Path B).
// Path A (fresh): all migrations in order yield TEXT NOT NULL + unique index,
// non-UUID identities round-trip verbatim with duplicate protection.
// Path B (upgrade): a disposable database at the pre-004 UUID schema keeps
// every row through the actual 004 runner, UUIDs survive as canonical text,
// relationships/lookups/uniqueness stay effective, and 004 re-applies cleanly.
using Npgsql;
using RideHailing.MatchingService.Infrastructure.Persistence;

namespace RideHailing.MatchingService.IntegrationTests;

[Collection(MatchingPostgresCollection.Name)]
public sealed class MatchingInputMessageIdMigrationTests
{
    private readonly MatchingPostgresDatabase _db;

    public MatchingInputMessageIdMigrationTests(MatchingPostgresDatabase db)
    {
        _db = db;
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

    [Fact]
    public async Task Fresh_Database_Uses_Text_NotNull_Unique_And_RoundTrips_NonUuid()
    {
        await using var conn = new NpgsqlConnection(_db.ConnectionString);
        await conn.OpenAsync();

        await using (var type = new NpgsqlCommand(
            "SELECT data_type, is_nullable FROM information_schema.columns " +
            "WHERE table_schema = 'public' AND table_name = 'matching_rounds' " +
            "AND column_name = 'input_message_id'", conn))
        await using (var reader = await type.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            Assert.Equal("text", reader.GetString(0));
            Assert.Equal("NO", reader.GetString(1));
        }

        await using (var idx = new NpgsqlCommand(
            "SELECT COUNT(*) FROM pg_indexes WHERE schemaname = 'public' " +
            "AND tablename = 'matching_rounds' " +
            "AND indexname = 'ux_matching_rounds_input_message_id'", conn))
        {
            Assert.Equal(1L, await idx.ExecuteScalarAsync());
        }

        var trip = Guid.NewGuid();
        const string messageId = "01K8EXAMPLEMESSAGE";
        try
        {
            await ExecuteAsync(conn,
                "INSERT INTO matching_rounds (trip_id, matching_round, input_message_id, status) " +
                "VALUES (@t, 1, @m, 'PROCESSING')",
                ("t", (object)trip), ("m", (object)messageId));

            await using var read = new NpgsqlCommand(
                "SELECT input_message_id FROM matching_rounds WHERE trip_id = @t", conn);
            read.Parameters.AddWithValue("t", trip);
            Assert.Equal(messageId, await read.ExecuteScalarAsync());

            // Repository reads TEXT without GetGuid.
            var repo = new MatchingRepository(_db.ConnectionString);
            var row = await repo.LoadRoundAsync(trip, 1);
            Assert.NotNull(row);
            Assert.Equal(messageId, row!.InputMessageId);

            // Duplicate identity protection survives the type change.
            var other = Guid.NewGuid();
            var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(conn,
                "INSERT INTO matching_rounds (trip_id, matching_round, input_message_id, status) " +
                "VALUES (@o, 1, @m, 'PROCESSING')",
                ("o", (object)other), ("m", (object)messageId)));
            Assert.Equal("23505", ex.SqlState);
            await ExecuteAsync(conn,
                "DELETE FROM matching_rounds WHERE trip_id = @o", ("o", (object)other));
        }
        finally
        {
            await ExecuteAsync(conn,
                "DELETE FROM matching_rounds WHERE trip_id = @t", ("t", (object)trip));
        }
    }

    [Fact]
    public async Task Existing_Uuid_Database_Upgrades_Without_Loss()
    {
        await using var conn = new NpgsqlConnection(_db.ConnectionString);
        await conn.OpenAsync();

        var tripA = Guid.NewGuid();
        var tripB = Guid.NewGuid();
        var msgA = Guid.NewGuid();
        var msgB = Guid.NewGuid();
        var driver = Guid.NewGuid();
        var reservation = Guid.NewGuid();
        try
        {
            // Disposable old-schema dataset: 001 UUID vocabulary only.
            await ExecuteAsync(conn, "DROP TABLE IF EXISTS matching_attempts");
            await ExecuteAsync(conn, "DROP TABLE IF EXISTS matching_rounds");
            await ExecuteAsync(conn, MatchingPostgresDatabase.LoadMigrationScript("001_matching_baseline.sql"));

            await ExecuteAsync(conn,
                "INSERT INTO matching_rounds (trip_id, matching_round, input_message_id, status) " +
                "VALUES (@a, 1, @ma, 'OPEN'), (@b, 1, @mb, 'OPEN')",
                ("a", (object)tripA), ("ma", (object)msgA),
                ("b", (object)tripB), ("mb", (object)msgB));
            await ExecuteAsync(conn,
                "INSERT INTO matching_attempts (trip_id, matching_round, ordinal, driver_id, reservation_id, state) " +
                "VALUES (@a, 1, 0, @d, @r, 'READY')",
                ("a", (object)tripA), ("d", (object)driver), ("r", (object)reservation));

            // Upgrade through the actual runner scripts in order.
            await ExecuteAsync(conn, MatchingPostgresDatabase.LoadMigrationScript("002_matching_round_status.sql"));
            await ExecuteAsync(conn, MatchingPostgresDatabase.LoadMigrationScript("003_matching_snapshot.sql"));
            await ExecuteAsync(conn, MatchingPostgresDatabase.LoadMigrationScript("004_matching_input_message_id_text.sql"));

            // Column is now TEXT NOT NULL.
            await using (var type = new NpgsqlCommand(
                "SELECT data_type, is_nullable FROM information_schema.columns " +
                "WHERE table_schema = 'public' AND table_name = 'matching_rounds' " +
                "AND column_name = 'input_message_id'", conn))
            await using (var reader = await type.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal("text", reader.GetString(0));
                Assert.Equal("NO", reader.GetString(1));
            }

            // Old UUIDs preserved as canonical text (PostgreSQL UUID::text).
            var expectedA = msgA.ToString("D");
            var expectedB = msgB.ToString("D");
            await using (var read = new NpgsqlCommand(
                "SELECT input_message_id FROM matching_rounds WHERE trip_id = @t", conn))
            {
                read.Parameters.AddWithValue("t", tripA);
                Assert.Equal(expectedA, (string)(await read.ExecuteScalarAsync())!);
            }

            await using (var read = new NpgsqlCommand(
                "SELECT input_message_id FROM matching_rounds WHERE trip_id = @t", conn))
            {
                read.Parameters.AddWithValue("t", tripB);
                Assert.Equal(expectedB, (string)(await read.ExecuteScalarAsync())!);
            }

            // Relationships survive: attempt FK + round lookups + status repair.
            await using (var count = new NpgsqlCommand(
                "SELECT COUNT(*) FROM matching_attempts WHERE trip_id = @t", conn))
            {
                count.Parameters.AddWithValue("t", tripA);
                Assert.Equal(1L, await count.ExecuteScalarAsync());
            }

            var repo = new MatchingRepository(_db.ConnectionString);
            var rowA = await repo.LoadRoundAsync(tripA, 1);
            Assert.NotNull(rowA);
            Assert.Equal(expectedA, rowA!.InputMessageId);
            Assert.Equal("PROCESSING", rowA.Status);

            // Existing uniqueness + round lookups remain effective.
            var dup = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(conn,
                "INSERT INTO matching_rounds (trip_id, matching_round, input_message_id, status) " +
                "VALUES (@o, 1, @m, 'PROCESSING')",
                ("o", (object)Guid.NewGuid()), ("m", (object)expectedA)));
            Assert.Equal("23505", dup.SqlState);

            // New non-UUID identities insert and round-trip after upgrade.
            var freshTrip = Guid.NewGuid();
            const string freshMsg = "ride-requested-event-42";
            await ExecuteAsync(conn,
                "INSERT INTO matching_rounds (trip_id, matching_round, input_message_id, status) " +
                "VALUES (@t, 9, @m, 'PROCESSING')",
                ("t", (object)freshTrip), ("m", (object)freshMsg));
            await using (var read = new NpgsqlCommand(
                "SELECT input_message_id FROM matching_rounds WHERE trip_id = @t", conn))
            {
                read.Parameters.AddWithValue("t", freshTrip);
                Assert.Equal(freshMsg, (string)(await read.ExecuteScalarAsync())!);
            }

            await ExecuteAsync(conn,
                "DELETE FROM matching_rounds WHERE trip_id = @t", ("t", (object)freshTrip));

            // Repeat migration handling follows normal runner behavior: re-apply is a clean no-op.
            await ExecuteAsync(conn, MatchingPostgresDatabase.LoadMigrationScript("004_matching_input_message_id_text.sql"));
            await using (var read = new NpgsqlCommand(
                "SELECT input_message_id FROM matching_rounds WHERE trip_id = @t", conn))
            {
                read.Parameters.AddWithValue("t", tripA);
                Assert.Equal(expectedA, (string)(await read.ExecuteScalarAsync())!);
            }
        }
        finally
        {
            await ExecuteAsync(conn, "DROP TABLE IF EXISTS matching_attempts");
            await ExecuteAsync(conn, "DROP TABLE IF EXISTS matching_rounds");
            await _db.ReapplyScriptsAsync();
        }
    }
}
