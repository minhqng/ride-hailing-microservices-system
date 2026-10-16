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
    /// Dispatch authorization + write-ahead fence record, atomically guarded
    /// (S1: authorized dispatch). Persists the Driver version about to be used
    /// for a Reserve(R) dispatch BEFORE the remote call is sent, and returns
    /// whether the caller may send. Uses GREATEST so concurrent duplicate
    /// workers converge to the maximum ever dispatched.
    ///
    /// Authorization is a single UPDATE predicate evaluated at the statement's
    /// linearization point (READ COMMITTED re-evaluates WHERE on the latest
    /// committed row after waiting on a concurrent updater):
    ///   - the attempt row itself must still be dispatch-eligible
    ///     (READY/IN_FLIGHT/UNKNOWN: never REJECTED/RESERVED);
    ///   - the round must still be PROCESSING (a settled round takes no new
    ///     effects; replay converges on durable rows instead).
    /// A zero-row update is NOT authorization: the caller must fail closed
    /// (no remote send) and re-read durable truth. Persistence failure also
    /// prevents dispatch (an undocumented outstanding fence would break later
    /// AVAILABLE fencing). Crash between fence write and dispatch only
    /// over-approximates max (conservative UNKNOWN).
    /// NULL means legacy row without provenance: a fresh fence still dominates
    /// it (version reads are monotonic), but no negative conclusion may use a
    /// NULL fence (see the fenced conclude methods).
    /// </summary>
    /// <returns>True when the fence was recorded and dispatch is authorized.</returns>
    public async Task<bool> TryRecordDispatchAsync(
        Guid tripId, int round, int ordinal, int dispatchedVersion,
        CancellationToken ct = default)
    {
        if (dispatchedVersion < 1)
        {
            throw new ArgumentException("dispatchedVersion must be >= 1.", nameof(dispatchedVersion));
        }

        const string Sql = """
            UPDATE matching_attempts AS a
            SET max_dispatched_version = GREATEST(COALESCE(a.max_dispatched_version, 0), @v),
                updated_at = now()
            WHERE a.trip_id = @trip AND a.matching_round = @round AND a.ordinal = @ordinal
              AND a.state IN ('READY', 'IN_FLIGHT', 'UNKNOWN')
              AND EXISTS (
                  SELECT 1 FROM matching_rounds AS r
                  WHERE r.trip_id = @trip AND r.matching_round = @round AND r.status = 'PROCESSING');
            """;
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(Sql, conn);
        cmd.Parameters.AddWithValue("trip", tripId);
        cmd.Parameters.AddWithValue("round", round);
        cmd.Parameters.AddWithValue("ordinal", ordinal);
        cmd.Parameters.AddWithValue("v", dispatchedVersion);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    /// <summary>
    /// Atomic version-fenced negative conclusion for an AVAILABLE observation
    /// (S2: atomic negative finality). Concludes UNKNOWN -&gt; REJECTED only
    /// when the observed Driver version Vx exceeds EVERY durably dispatched
    /// fence (Vx &gt; max at this statement's linearization point): versions
    /// only increase and the Driver row lock serializes writers, so any
    /// outstanding or future R(V) has V &lt;= max &lt; Vx and fails StaleVersion.
    /// A concurrent dispatch-fence commit wins first instead: the re-evaluated
    /// predicate (Vx &gt; new max) fails and this returns false — the caller
    /// must stay unresolved, never conclude from its cached fence.
    /// NULL fence (legacy, no trustworthy provenance) never concludes.
    /// </summary>
    /// <returns>True when this call durably concluded the attempt.</returns>
    public async Task<bool> TryConcludeFencedAvailableAsync(
        Guid tripId, int round, int ordinal, int observedVersion, string? errorCode,
        CancellationToken ct = default)
    {
        if (observedVersion < 1)
        {
            throw new ArgumentException("observedVersion must be >= 1.", nameof(observedVersion));
        }

        const string Sql = """
            UPDATE matching_attempts SET state = 'REJECTED', last_error_code = @err,
                updated_at = now()
            WHERE trip_id = @trip AND matching_round = @round AND ordinal = @ordinal
              AND state = 'UNKNOWN'
              AND max_dispatched_version IS NOT NULL
              AND @vx > max_dispatched_version;
            """;
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(Sql, conn);
        cmd.Parameters.AddWithValue("trip", tripId);
        cmd.Parameters.AddWithValue("round", round);
        cmd.Parameters.AddWithValue("ordinal", ordinal);
        cmd.Parameters.AddWithValue("vx", observedVersion);
        cmd.Parameters.AddWithValue("err", (object?)errorCode ?? DBNull.Value);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    /// <summary>
    /// Atomic version-fenced negative conclusion for an incompatible-state
    /// observation (ReservedByOther/Offline/OnTrip, S2). Concludes
    /// UNKNOWN -&gt; REJECTED only when the observed version Vx covers every
    /// durably dispatched fence (Vx &gt;= max at linearization): every state
    /// change bumps the Driver version, so each outstanding R(V) with
    /// V &lt;= max &lt;= Vx either is already stale (future versions only
    /// grow past Vx) or would meet an unchanged incompatible state (same
    /// version implies no effective mutation since the observation) and
    /// conflict. A concurrent newer fence defeats the predicate (fail closed).
    /// Bare state incompatibility alone is NOT durable proof (a later release
    /// can restore AVAILABLE), so the fence comparison is load-bearing here
    /// just as for AVAILABLE — only the strictness differs (AVAILABLE needs
    /// strict &gt; because R(Vx) itself could still commit pre-lock).
    /// NULL fence never concludes. NotFound (no version) is handled by the
    /// caller: no dispatch is possible without a version read, so a missing
    /// row cannot hide an outstanding committable effect (no DELETE in
    /// production) and needs no fence comparison.
    /// </summary>
    /// <returns>True when this call durably concluded the attempt.</returns>
    public async Task<bool> TryConcludeFencedIncompatibleAsync(
        Guid tripId, int round, int ordinal, int observedVersion, string? errorCode,
        CancellationToken ct = default)
    {
        if (observedVersion < 1)
        {
            throw new ArgumentException("observedVersion must be >= 1.", nameof(observedVersion));
        }

        const string Sql = """
            UPDATE matching_attempts SET state = 'REJECTED', last_error_code = @err,
                updated_at = now()
            WHERE trip_id = @trip AND matching_round = @round AND ordinal = @ordinal
              AND state = 'UNKNOWN'
              AND max_dispatched_version IS NOT NULL
              AND @vx >= max_dispatched_version;
            """;
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(Sql, conn);
        cmd.Parameters.AddWithValue("trip", tripId);
        cmd.Parameters.AddWithValue("round", round);
        cmd.Parameters.AddWithValue("ordinal", ordinal);
        cmd.Parameters.AddWithValue("vx", observedVersion);
        cmd.Parameters.AddWithValue("err", (object?)errorCode ?? DBNull.Value);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    private static MatchingAttemptRow ReadAttempt(NpgsqlDataReader reader) => new(
        reader.GetGuid(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetGuid(3),
        reader.GetGuid(4), reader.GetString(5),
        reader.IsDBNull(6) ? null : reader.GetString(6),
        reader.FieldCount > 7 && !reader.IsDBNull(7) ? reader.GetInt32(7) : null);
}
