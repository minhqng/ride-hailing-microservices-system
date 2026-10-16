// B-W07-05/08: Matching attempt + round durable workflow state
// (Doc 02 §12.2–12.4, DSI-015/017/018/031). Tables come from the frozen W2
// baseline (001/002); this repository is the write-ahead USAGE: persist
// IN_FLIGHT before the remote call, transition on classification, reload for
// resume. Matching owns this DB exclusively; Driver truth is only ever read
// through the Driver HTTP contract, never SQL.
using Npgsql;

namespace RideHailing.MatchingService.Infrastructure.Persistence;

public static class MatchingAttemptStates
{
    public const string Ready = "READY";
    public const string InFlight = "IN_FLIGHT";
    public const string Unknown = "UNKNOWN";
    public const string Rejected = "REJECTED";
    public const string Reserved = "RESERVED";
}

public static class MatchingRoundStatuses
{
    public const string Processing = "PROCESSING";
    public const string Reserved = "RESERVED";
    public const string NoDriver = "NO_DRIVER";
    public const string Cancelled = "CANCELLED";
    public const string Failed = "FAILED";
}

public sealed record MatchingAttemptRow(
    Guid TripId,
    int MatchingRound,
    int Ordinal,
    Guid DriverId,
    Guid ReservationId,
    string State,
    string? LastErrorCode,
    int? MaxDispatchedVersion);

public sealed record MatchingRoundRow(
    Guid TripId,
    int MatchingRound,
    string Status,
    Guid? SelectedDriverId,
    Guid? SelectedReservationId);

public sealed class MatchingAttemptRepository
{
    private readonly string _connectionString;

    public MatchingAttemptRepository(string connectionString)
    {
        _connectionString = connectionString;
    }

    /// <summary>
    /// Ensures the round row exists (PROCESSING). On PK/unique conflict
    /// (duplicate trigger, DSR-009 spirit) converges to the existing row.
    /// </summary>
    public async Task<MatchingRoundRow> EnsureRoundAsync(
        Guid tripId, int round, Guid inputMessageId, CancellationToken ct = default)
    {
        const string Insert = """
            INSERT INTO matching_rounds (trip_id, matching_round, input_message_id, status)
            VALUES (@trip, @round, @msg, 'PROCESSING');
            """;
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        try
        {
            await using var cmd = new NpgsqlCommand(Insert, conn);
            cmd.Parameters.AddWithValue("trip", tripId);
            cmd.Parameters.AddWithValue("round", round);
            cmd.Parameters.AddWithValue("msg", inputMessageId);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation
            && string.Equals(ex.ConstraintName, "matching_rounds_pkey", StringComparison.Ordinal))
        {
            // Duplicate trigger converged: the durable row already exists.
        }

        return (await LoadRoundAsync(tripId, round, ct))!;
    }

    public async Task<MatchingRoundRow?> LoadRoundAsync(
        Guid tripId, int round, CancellationToken ct = default)
    {
        const string Sql = """
            SELECT trip_id, matching_round, status, selected_driver_id, selected_reservation_id
            FROM matching_rounds WHERE trip_id = @trip AND matching_round = @round;
            """;
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(Sql, conn);
        cmd.Parameters.AddWithValue("trip", tripId);
        cmd.Parameters.AddWithValue("round", round);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return new MatchingRoundRow(
            reader.GetGuid(0), reader.GetInt32(1), reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetGuid(3),
            reader.IsDBNull(4) ? null : reader.GetGuid(4));
    }

    /// <summary>
    /// Marks the round RESERVED with the winning identity. Conditional on
    /// PROCESSING: a settled round never advances silently (DSI-015).
    /// </summary>
    public async Task<bool> TryMarkRoundReservedAsync(
        Guid tripId, int round, Guid driverId, Guid reservationId,
        CancellationToken ct = default)
    {
        const string Sql = """
            UPDATE matching_rounds SET status = 'RESERVED',
                selected_driver_id = @driver, selected_reservation_id = @reservation,
                updated_at = now()
            WHERE trip_id = @trip AND matching_round = @round AND status = 'PROCESSING';
            """;
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(Sql, conn);
        cmd.Parameters.AddWithValue("trip", tripId);
        cmd.Parameters.AddWithValue("round", round);
        cmd.Parameters.AddWithValue("driver", driverId);
        cmd.Parameters.AddWithValue("reservation", reservationId);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    /// <summary>
    /// Persists the write-ahead IN_FLIGHT attempt (Doc 02 §12.4). On PK
    /// conflict (duplicate worker, same ordinal) converges to the existing
    /// row so both workers share one reservation identity (DSI-018).
    /// Returns (insertedNew, row).
    /// </summary>
    public async Task<(bool Inserted, MatchingAttemptRow Row)> EnsureInflightAttemptAsync(
        Guid tripId, int round, int ordinal, Guid driverId, Guid reservationId,
        CancellationToken ct = default)
    {
        const string Insert = """
            INSERT INTO matching_attempts
                (trip_id, matching_round, ordinal, driver_id, reservation_id, state)
            VALUES (@trip, @round, @ordinal, @driver, @reservation, 'IN_FLIGHT');
            """;
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        try
        {
            await using var cmd = new NpgsqlCommand(Insert, conn);
            cmd.Parameters.AddWithValue("trip", tripId);
            cmd.Parameters.AddWithValue("round", round);
            cmd.Parameters.AddWithValue("ordinal", ordinal);
            cmd.Parameters.AddWithValue("driver", driverId);
            cmd.Parameters.AddWithValue("reservation", reservationId);
            await cmd.ExecuteNonQueryAsync(ct);
            return (true, new MatchingAttemptRow(
                tripId, round, ordinal, driverId, reservationId,
                MatchingAttemptStates.InFlight, null, null));
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation
            && string.Equals(ex.ConstraintName, "matching_attempts_pkey", StringComparison.Ordinal))
        {
            // Same ordinal already attempted (duplicate worker / redelivery):
            // converge to the existing row and its reservation identity.
            var existing = await LoadAttemptAsync(tripId, round, ordinal, ct);
            return (false, existing!);
        }
    }

    public async Task<MatchingAttemptRow?> LoadAttemptAsync(
        Guid tripId, int round, int ordinal, CancellationToken ct = default)
    {
        const string Sql = """
            SELECT trip_id, matching_round, ordinal, driver_id, reservation_id, state, last_error_code, max_dispatched_version
            FROM matching_attempts
            WHERE trip_id = @trip AND matching_round = @round AND ordinal = @ordinal;
            """;
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(Sql, conn);
        cmd.Parameters.AddWithValue("trip", tripId);
        cmd.Parameters.AddWithValue("round", round);
        cmd.Parameters.AddWithValue("ordinal", ordinal);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return ReadAttempt(reader);
    }

    public async Task<IReadOnlyList<MatchingAttemptRow>> LoadAttemptsAsync(
        Guid tripId, int round, CancellationToken ct = default)
    {
        const string Sql = """
            SELECT trip_id, matching_round, ordinal, driver_id, reservation_id, state, last_error_code, max_dispatched_version
            FROM matching_attempts
            WHERE trip_id = @trip AND matching_round = @round
            ORDER BY ordinal;
            """;
        var rows = new List<MatchingAttemptRow>();
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(Sql, conn);
        cmd.Parameters.AddWithValue("trip", tripId);
        cmd.Parameters.AddWithValue("round", round);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(ReadAttempt(reader));
        }

        return rows;
    }

    /// <summary>
    /// DSI-017 guard read: any IN_FLIGHT/UNKNOWN attempt except the identified
    /// (ordinal, driver) pair blocks a NEW reserve. The pair itself may resume.
    /// </summary>
    public async Task<bool> HasOtherUnresolvedAsync(
        Guid tripId, int round, int ordinal, Guid driverId,
        CancellationToken ct = default)
    {
        const string Sql = """
            SELECT COUNT(*) FROM matching_attempts
            WHERE trip_id = @trip AND matching_round = @round
              AND state IN ('IN_FLIGHT', 'UNKNOWN')
              AND NOT (ordinal = @ordinal AND driver_id = @driver);
            """;
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(Sql, conn);
        cmd.Parameters.AddWithValue("trip", tripId);
        cmd.Parameters.AddWithValue("round", round);
        cmd.Parameters.AddWithValue("ordinal", ordinal);
        cmd.Parameters.AddWithValue("driver", driverId);
        return (long)(await cmd.ExecuteScalarAsync(ct))! > 0;
    }

    /// <summary>
    /// Guarded state transition: only moves when the row is still in one of
    /// the expected states. Returns false when the row moved underneath
    /// (caller reloads and re-decides; never blind-overwrites).
    /// </summary>
    public async Task<bool> TryTransitionAsync(
        Guid tripId, int round, int ordinal,
        string[] expectedFrom, string to, string? errorCode,
        CancellationToken ct = default)
    {
        const string Sql = """
            UPDATE matching_attempts SET state = @to, last_error_code = @err,
                updated_at = now()
            WHERE trip_id = @trip AND matching_round = @round AND ordinal = @ordinal
              AND state = ANY (@from);
            """;
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(Sql, conn);
        cmd.Parameters.AddWithValue("trip", tripId);
        cmd.Parameters.AddWithValue("round", round);
        cmd.Parameters.AddWithValue("ordinal", ordinal);
        cmd.Parameters.AddWithValue("to", to);
        cmd.Parameters.AddWithValue("err", (object?)errorCode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("from", expectedFrom);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    /// <summary>
    /// Records a durable review/error signal on an unresolved attempt without
    /// changing its state (e.g. reconcile-exhaustion marker). The row stays
    /// UNKNOWN: visibility without inventing a terminal.
    /// </summary>
    public async Task SetErrorCodeAsync(
        Guid tripId, int round, int ordinal, string errorCode,
        CancellationToken ct = default)
    {
        const string Sql = """
            UPDATE matching_attempts SET last_error_code = @err, updated_at = now()
            WHERE trip_id = @trip AND matching_round = @round AND ordinal = @ordinal;
            """;
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(Sql, conn);
        cmd.Parameters.AddWithValue("trip", tripId);
        cmd.Parameters.AddWithValue("round", round);
        cmd.Parameters.AddWithValue("ordinal", ordinal);
        cmd.Parameters.AddWithValue("err", errorCode);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Write-ahead fence record: persists the Driver version about to be used
    /// for a Reserve(R) dispatch BEFORE the remote call is sent. Uses
    /// GREATEST so concurrent duplicate workers converge to the maximum ever
    /// dispatched (safe: Vx &gt; max proves all outstanding stale; Vx &lt;= max
    /// stays UNKNOWN, possibly conservative but never unsafe). Persistence
    /// failure must prevent dispatch (caller returns UNKNOWN without calling).
    /// NULL means legacy row without provenance: never safe to advance on AVAILABLE.
    /// </summary>
    public async Task RecordDispatchAsync(
        Guid tripId, int round, int ordinal, int dispatchedVersion,
        CancellationToken ct = default)
    {
        const string Sql = """
            UPDATE matching_attempts
            SET max_dispatched_version = GREATEST(COALESCE(max_dispatched_version, 0), @v),
                updated_at = now()
            WHERE trip_id = @trip AND matching_round = @round AND ordinal = @ordinal;
            """;
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(Sql, conn);
        cmd.Parameters.AddWithValue("trip", tripId);
        cmd.Parameters.AddWithValue("round", round);
        cmd.Parameters.AddWithValue("ordinal", ordinal);
        cmd.Parameters.AddWithValue("v", dispatchedVersion);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static MatchingAttemptRow ReadAttempt(NpgsqlDataReader reader) => new(
        reader.GetGuid(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetGuid(3),
        reader.GetGuid(4), reader.GetString(5),
        reader.IsDBNull(6) ? null : reader.GetString(6),
        reader.FieldCount > 7 && !reader.IsDBNull(7) ? reader.GetInt32(7) : null);
}
