// Hardening: attempt-ledger guards (Doc 01 §8.3, §26).
// Proves the legal attempt vocabulary and the uniqueness defenses behind
// DSI-015/017/018: UNIQUE(input_message_id) stops duplicate round creation,
// UNIQUE(reservation_id) keeps one identity per logical attempt.
using Npgsql;

namespace RideHailing.MatchingService.IntegrationTests;

[Collection(MatchingPostgresCollection.Name)]
public sealed class MatchingAttemptGuardTests
{
    private readonly MatchingPostgresDatabase _db;

    public MatchingAttemptGuardTests(MatchingPostgresDatabase db)
    {
        _db = db;
    }

    public static TheoryData<string> LegalAttemptStates() => new()
    {
        "READY",
        "IN_FLIGHT",
        "UNKNOWN",
        "REJECTED",
        "RESERVED",
    };

    [Theory]
    [MemberData(nameof(LegalAttemptStates))]
    public async Task Legal_Attempt_State_Inserts(string state)
    {
        var trip = Guid.NewGuid();
        await using var conn = new NpgsqlConnection(_db.ConnectionString);
        await conn.OpenAsync();
        try
        {
            await ExecuteAsync(conn,
                "INSERT INTO matching_rounds (trip_id, matching_round, input_message_id, status) " +
                "VALUES (@t, 1, @m, 'PROCESSING')",
                ("t", (object)trip), ("m", (object)Guid.NewGuid().ToString("D")));
            await ExecuteAsync(conn,
                "INSERT INTO matching_attempts (trip_id, matching_round, ordinal, driver_id, reservation_id, state) " +
                "VALUES (@t, 1, 0, @d, @r, @s)",
                ("t", (object)trip), ("d", Guid.NewGuid()), ("r", Guid.NewGuid()), ("s", state));
        }
        finally
        {
            await ExecuteAsync(conn,
                "DELETE FROM matching_rounds WHERE trip_id = @t", ("t", (object)trip));
        }
    }

    [Theory]
    [InlineData("OPEN")]
    [InlineData("DONE")]
    [InlineData("ready")]
    [InlineData("unknown")]
    [InlineData("")]
    public async Task Illegal_Attempt_State_Rejected(string state)
    {
        var trip = Guid.NewGuid();
        await using var conn = new NpgsqlConnection(_db.ConnectionString);
        await conn.OpenAsync();
        try
        {
            await ExecuteAsync(conn,
                "INSERT INTO matching_rounds (trip_id, matching_round, input_message_id, status) " +
                "VALUES (@t, 1, @m, 'PROCESSING')",
                ("t", (object)trip), ("m", (object)Guid.NewGuid().ToString("D")));
            var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(conn,
                "INSERT INTO matching_attempts (trip_id, matching_round, ordinal, driver_id, reservation_id, state) " +
                "VALUES (@t, 1, 0, @d, @r, @s)",
                ("t", (object)trip), ("d", Guid.NewGuid()), ("r", Guid.NewGuid()), ("s", state)));
            Assert.Equal("23514", ex.SqlState);
        }
        finally
        {
            await ExecuteAsync(conn,
                "DELETE FROM matching_rounds WHERE trip_id = @t", ("t", (object)trip));
        }
    }

    [Fact]
    public async Task Duplicate_Input_Message_Id_Rejected()
    {
        var message = Guid.NewGuid().ToString("D");
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        await using var conn = new NpgsqlConnection(_db.ConnectionString);
        await conn.OpenAsync();
        try
        {
            await ExecuteAsync(conn,
                "INSERT INTO matching_rounds (trip_id, matching_round, input_message_id, status) " +
                "VALUES (@t, 1, @m, 'PROCESSING')",
                ("t", (object)first), ("m", (object)message));
            var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(conn,
                "INSERT INTO matching_rounds (trip_id, matching_round, input_message_id, status) " +
                "VALUES (@t, 1, @m, 'PROCESSING')",
                ("t", (object)second), ("m", (object)message)));
            Assert.Equal("23505", ex.SqlState);
        }
        finally
        {
            await ExecuteAsync(conn,
                "DELETE FROM matching_rounds WHERE trip_id IN (@a, @b)",
                ("a", (object)first), ("b", (object)second));
        }
    }

    [Fact]
    public async Task Duplicate_Reservation_Id_Rejected()
    {
        var trip = Guid.NewGuid();
        var reservation = Guid.NewGuid();
        await using var conn = new NpgsqlConnection(_db.ConnectionString);
        await conn.OpenAsync();
        try
        {
            await ExecuteAsync(conn,
                "INSERT INTO matching_rounds (trip_id, matching_round, input_message_id, status) " +
                "VALUES (@t, 1, @m, 'PROCESSING')",
                ("t", (object)trip), ("m", (object)Guid.NewGuid().ToString("D")));
            await ExecuteAsync(conn,
                "INSERT INTO matching_attempts (trip_id, matching_round, ordinal, driver_id, reservation_id, state) " +
                "VALUES (@t, 1, 0, @d, @r, 'READY')",
                ("t", (object)trip), ("d", Guid.NewGuid()), ("r", reservation));
            var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(conn,
                "INSERT INTO matching_attempts (trip_id, matching_round, ordinal, driver_id, reservation_id, state) " +
                "VALUES (@t, 1, 1, @d, @r, 'READY')",
                ("t", (object)trip), ("d", Guid.NewGuid()), ("r", reservation)));
            Assert.Equal("23505", ex.SqlState);
        }
        finally
        {
            await ExecuteAsync(conn,
                "DELETE FROM matching_rounds WHERE trip_id = @t", ("t", (object)trip));
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
