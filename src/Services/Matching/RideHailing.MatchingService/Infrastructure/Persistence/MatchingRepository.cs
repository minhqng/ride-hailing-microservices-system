// B-W05-05/06: Matching round/attempt persistence (Npgsql raw, no EF).
// Round identity (trip_id, matching_round) + input_message_id uniqueness
// follow Doc 02 §12.2. All statements parameterized; fail loud on conflict.
using Npgsql;
using RideHailing.MatchingService.Domain.Matching;

namespace RideHailing.MatchingService.Infrastructure.Persistence;

public sealed class MatchingRepository
{
    private readonly string _connectionString;

    public MatchingRepository(string connectionString)
    {
        _connectionString = connectionString;
    }

    public sealed record RoundRow(
        Guid TripId, int Round, Guid InputMessageId, string Status,
        string Stage, bool CancelRequested, string RankingConfigVersion,
        string? SnapshotHash, string? SnapshotJson, int CandidateCount, int NextAttemptOrdinal);

    public sealed record AttemptRow(
        Guid TripId, int Round, int Ordinal, Guid DriverId, Guid ReservationId, string State);

    public async Task<RoundRow?> LoadRoundAsync(Guid tripId, int round, CancellationToken ct = default)
    {
        const string Sql = """
            SELECT trip_id, matching_round, input_message_id, status, current_stage,
                   cancel_requested, ranking_config_version, candidate_snapshot_hash,
                   candidate_snapshot_json, candidate_count, next_attempt_ordinal
            FROM matching_rounds WHERE trip_id = @t AND matching_round = @r;
            """;
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(Sql, conn);
        cmd.Parameters.AddWithValue("t", tripId);
        cmd.Parameters.AddWithValue("r", round);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return new RoundRow(
            reader.GetGuid(0), reader.GetInt32(1), reader.GetGuid(2),
            reader.GetString(3), reader.GetString(4), reader.GetBoolean(5),
            reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.GetInt32(9), reader.GetInt32(10));
    }

    public async Task InsertRoundAsync(
        Guid tripId, int round, Guid inputMessageId, string rankingConfigVersion,
        CancellationToken ct = default)
    {
        const string Sql = """
            INSERT INTO matching_rounds (trip_id, matching_round, input_message_id, status, ranking_config_version)
            VALUES (@t, @r, @m, 'PROCESSING', @v);
            """;
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(Sql, conn);
        cmd.Parameters.AddWithValue("t", tripId);
        cmd.Parameters.AddWithValue("r", round);
        cmd.Parameters.AddWithValue("m", inputMessageId);
        cmd.Parameters.AddWithValue("v", rankingConfigVersion);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task StoreSnapshotAsync(
        Guid tripId, int round, string hash, string json, int count, int nextOrdinal,
        CancellationToken ct = default)
    {
        const string Sql = """
            UPDATE matching_rounds
            SET candidate_snapshot_hash = @h, candidate_snapshot_json = @j,
                candidate_count = @c, next_attempt_ordinal = @n, updated_at = now()
            WHERE trip_id = @t AND matching_round = @r;
            """;
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(Sql, conn);
        cmd.Parameters.AddWithValue("h", hash);
        cmd.Parameters.AddWithValue("j", NpgsqlTypes.NpgsqlDbType.Jsonb, json);
        cmd.Parameters.AddWithValue("c", count);
        cmd.Parameters.AddWithValue("n", nextOrdinal);
        cmd.Parameters.AddWithValue("t", tripId);
        cmd.Parameters.AddWithValue("r", round);
        var rows = await cmd.ExecuteNonQueryAsync(ct);
        if (rows != 1)
        {
            throw new InvalidOperationException("Matching round disappeared during snapshot store.");
        }
    }

    public async Task<IReadOnlyList<AttemptRow>> LoadAttemptsAsync(
        Guid tripId, int round, CancellationToken ct = default)
    {
        const string Sql = """
            SELECT trip_id, matching_round, ordinal, driver_id, reservation_id, state
            FROM matching_attempts WHERE trip_id = @t AND matching_round = @r ORDER BY ordinal;
            """;
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(Sql, conn);
        cmd.Parameters.AddWithValue("t", tripId);
        cmd.Parameters.AddWithValue("r", round);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<AttemptRow>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new AttemptRow(
                reader.GetGuid(0), reader.GetInt32(1), reader.GetInt32(2),
                reader.GetGuid(3), reader.GetGuid(4), reader.GetString(5)));
        }

        return rows;
    }

    public async Task InsertAttemptAsync(
        Guid tripId, int round, int ordinal, Guid driverId, Guid reservationId,
        CancellationToken ct = default)
    {
        const string Sql = """
            INSERT INTO matching_attempts (trip_id, matching_round, ordinal, driver_id, reservation_id, state)
            VALUES (@t, @r, @o, @d, @res, 'READY');
            """;
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(Sql, conn);
        cmd.Parameters.AddWithValue("t", tripId);
        cmd.Parameters.AddWithValue("r", round);
        cmd.Parameters.AddWithValue("o", ordinal);
        cmd.Parameters.AddWithValue("d", driverId);
        cmd.Parameters.AddWithValue("res", reservationId);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
