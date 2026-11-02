// B-W07-05/08: Matching attempt + round durable workflow state
// (Doc 02 §12.2–12.4, DSI-015/017/018/031). Tables come from the frozen W2
// baseline (001/002); this repository is the write-ahead USAGE: persist
// IN_FLIGHT before the remote call, transition on classification, reload for
// resume. Matching owns this DB exclusively; Driver truth is only ever read
// through the Driver HTTP contract, never SQL.
using Npgsql;
using RideHailing.MatchingService.Application.Matching;

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
    int? MaxDispatchedVersion,
    string Stage = "S0");

public sealed record MatchingRoundRow(
    Guid TripId,
    int MatchingRound,
    string Status,
    Guid? SelectedDriverId,
    Guid? SelectedReservationId,
    Guid? ResultMessageId,
    string InputMessageId = "",
    string CurrentStage = "S0",
    bool CancelRequested = false,
    bool AutoExecute = false,
    double? PickupLongitude = null,
    double? PickupLatitude = null,
    string? SnapshotJson = null,
    string? WorkErrorCode = null,
    DateTimeOffset? WorkCheckedAt = null,
    string? StageEvaluated = null,
    string? IntakeTraceParent = null);

public sealed class MatchingAttemptRepository
{
    private readonly string _connectionString;
    private readonly MatchingMetrics? _metrics;

    public MatchingAttemptRepository(string connectionString)
        : this(connectionString, null)
    {
    }

    public MatchingAttemptRepository(string connectionString, MatchingMetrics? metrics)
    {
        _connectionString = connectionString;
        _metrics = metrics;
    }

    /// <summary>
    /// Ensures the round row exists (PROCESSING). On PK conflict
    /// (duplicate trigger, DSR-009 spirit) converges to the existing row
    /// WITHOUT overwriting its stored input_message_id: the durable
    /// primary identity is (tripId, matchingRound) and the first writer's
    /// envelope MessageId wins. A UNIQUE(input_message_id) conflict (same
    /// envelope identity reused for a different round) is a permanent
    /// identity conflict: fail loud so the broker path drops it instead of
    /// ACKing a second effect or hot-looping transiently.
    /// </summary>
    public async Task<MatchingRoundRow> EnsureRoundAsync(
        Guid tripId, int round, string inputMessageId, CancellationToken ct = default) =>
        await EnsureRoundCoreAsync(tripId, round, inputMessageId, null, null, ct);

    /// <summary>
    /// B-REC-01 durable intake: the canonical RideRequestedV1 commits the
    /// pickup center, the auto-execute obligation and any cancellation that
    /// overtook this message in the SAME transaction, before the consumer
    /// ACKs. A crash after ACK therefore leaves work that the autonomous
    /// worker can discover and execute; no in-memory queue is involved.
    /// Duplicate deliveries converge; the first pickup identity wins (never
    /// overwritten by a replay). A row created by the demo/test seam is never
    /// adopted: its caller-selected pickup/candidates are not production
    /// authority, so the intake rejects it without mutating or executing it
    /// (L3 origin provenance).
    /// </summary>
    public async Task<MatchingRoundRow> EnsureProductionRoundAsync(
        Guid tripId, int round, string inputMessageId,
        double pickupLongitude, double pickupLatitude, CancellationToken ct = default, string? traceParent = null)
    {
        if (string.IsNullOrWhiteSpace(inputMessageId))
        {
            throw new ArgumentException("inputMessageId must not be empty.", nameof(inputMessageId));
        }

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Same namespace as CancelMatchingRoundV1: intake and cancel cannot
        // interleave between "is a cancel pending?" and round visibility.
        await using (var lockCmd = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended(@key, 0));", conn, tx))
        {
            lockCmd.Parameters.AddWithValue("key", MatchingAdvisoryKeys.Cancel(tripId, round));
            await lockCmd.ExecuteNonQueryAsync(ct);
        }

        bool pendingCancel;
        await using (var cancelCmd = new NpgsqlCommand("""
            SELECT EXISTS (
                SELECT 1 FROM matching_cancel_intents
                WHERE trip_id = @trip AND matching_round = @round);
            """, conn, tx))
        {
            cancelCmd.Parameters.AddWithValue("trip", tripId);
            cancelCmd.Parameters.AddWithValue("round", round);
            pendingCancel = (bool)(await cancelCmd.ExecuteScalarAsync(ct))!;
        }

        const string Insert = """
            INSERT INTO matching_rounds
                (trip_id, matching_round, input_message_id, status,
                 pickup_longitude, pickup_latitude, auto_execute, cancel_requested, intake_traceparent)
            VALUES (@trip, @round, @msg, 'PROCESSING', @lon, @lat, TRUE, @cancel, @traceparent)
            ON CONFLICT (trip_id, matching_round) DO NOTHING;
            """;
        int inserted;
        try
        {
            await using var cmd = new NpgsqlCommand(Insert, conn, tx);
            cmd.Parameters.AddWithValue("trip", tripId);
            cmd.Parameters.AddWithValue("round", round);
            cmd.Parameters.AddWithValue("msg", inputMessageId);
            cmd.Parameters.AddWithValue("lon", pickupLongitude);
            cmd.Parameters.AddWithValue("lat", pickupLatitude);
            cmd.Parameters.AddWithValue("cancel", pendingCancel);
            cmd.Parameters.AddWithValue("traceparent", NpgsqlTypes.NpgsqlDbType.Text,
                System.Diagnostics.ActivityContext.TryParse(traceParent, null, out _) ? traceParent! : DBNull.Value);
            inserted = await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation
            && string.Equals(ex.ConstraintName, "ux_matching_rounds_input_message_id", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Input message '{inputMessageId}' is already assigned to a different matching round.", ex);
        }

        if (inserted == 0)
        {
            // L3 origin gate: only a round created by canonical production
            // intake may be upgraded and executed. A demo/test round
            // (auto_execute = FALSE) may carry caller-selected pickup and
            // candidate identities; adopting it would make the production
            // worker execute non-canonical state. The row is locked while the
            // origin is read so a concurrent demo create cannot slip between
            // the PK conflict and this decision. Rejection is a permanent
            // input conflict (poison classification: no hot loop), and it
            // mutates nothing — no pickup overwrite, no cancel apply, no work.
            const string Origin = """
                SELECT auto_execute FROM matching_rounds
                WHERE trip_id = @trip AND matching_round = @round
                FOR UPDATE;
                """;
            bool productionOrigin;
            await using (var origin = new NpgsqlCommand(Origin, conn, tx))
            {
                origin.Parameters.AddWithValue("trip", tripId);
                origin.Parameters.AddWithValue("round", round);
                var value = await origin.ExecuteScalarAsync(ct);
                if (value is not bool flag)
                {
                    throw new InvalidOperationException(
                        $"MATCHING_ROUND_ORIGIN_UNKNOWN: round {tripId:D}/{round} disappeared during production intake.");
                }

                productionOrigin = flag;
            }

            if (!productionOrigin)
            {
                throw new InvalidOperationException(
                    $"MATCHING_ROUND_ORIGIN_CONFLICT: round {tripId:D}/{round} was created by the demo/test seam and cannot be adopted by production intake.");
            }

            const string Upgrade = """
                UPDATE matching_rounds
                SET pickup_longitude = COALESCE(pickup_longitude, @lon),
                    pickup_latitude = COALESCE(pickup_latitude, @lat),
                    cancel_requested = cancel_requested OR @cancel,
                    updated_at = now()
                WHERE trip_id = @trip AND matching_round = @round
                  AND status = 'PROCESSING' AND auto_execute = TRUE;
                """;
            await using var upgrade = new NpgsqlCommand(Upgrade, conn, tx);
            upgrade.Parameters.AddWithValue("trip", tripId);
            upgrade.Parameters.AddWithValue("round", round);
            upgrade.Parameters.AddWithValue("lon", pickupLongitude);
            upgrade.Parameters.AddWithValue("lat", pickupLatitude);
            upgrade.Parameters.AddWithValue("cancel", pendingCancel);
            await upgrade.ExecuteNonQueryAsync(ct);
        }

        if (pendingCancel)
        {
            await using var consume = new NpgsqlCommand("""
                UPDATE matching_cancel_intents SET consumed_at = now()
                WHERE trip_id = @trip AND matching_round = @round AND consumed_at IS NULL;
                """, conn, tx);
            consume.Parameters.AddWithValue("trip", tripId);
            consume.Parameters.AddWithValue("round", round);
            await consume.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return (await LoadRoundAsync(tripId, round, ct))!;
    }

    private async Task<MatchingRoundRow> EnsureRoundCoreAsync(
        Guid tripId, int round, string inputMessageId,
        double? pickupLongitude, double? pickupLatitude,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(inputMessageId))
        {
            throw new ArgumentException("inputMessageId must not be empty.", nameof(inputMessageId));
        }

        const string Insert = """
            INSERT INTO matching_rounds
                (trip_id, matching_round, input_message_id, status,
                 pickup_longitude, pickup_latitude)
            VALUES (@trip, @round, @msg, 'PROCESSING', @lon, @lat);
            """;
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        try
        {
            await using var cmd = new NpgsqlCommand(Insert, conn);
            cmd.Parameters.AddWithValue("trip", tripId);
            cmd.Parameters.AddWithValue("round", round);
            cmd.Parameters.AddWithValue("msg", inputMessageId);
            cmd.Parameters.AddWithValue("lon", (object?)pickupLongitude ?? DBNull.Value);
            cmd.Parameters.AddWithValue("lat", (object?)pickupLatitude ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation
            && string.Equals(ex.ConstraintName, "matching_rounds_pkey", StringComparison.Ordinal))
        {
            // Duplicate trigger converged: the durable row already exists.
            // Stored input_message_id is intentionally left untouched.
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation
            && string.Equals(ex.ConstraintName, "ux_matching_rounds_input_message_id", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Input message '{inputMessageId}' is already assigned to a different matching round.", ex);
        }

        return (await LoadRoundAsync(tripId, round, ct))!;
    }

    public async Task<MatchingRoundRow?> LoadRoundAsync(
        Guid tripId, int round, CancellationToken ct = default)
    {
        const string Sql = """
            SELECT trip_id, matching_round, status, selected_driver_id, selected_reservation_id,
                   result_message_id, input_message_id, current_stage, cancel_requested,
                   auto_execute, pickup_longitude, pickup_latitude,
                   candidate_snapshot_json::text, work_error_code, work_checked_at,
                   stage_evaluated, intake_traceparent
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
            reader.IsDBNull(4) ? null : reader.GetGuid(4),
            reader.IsDBNull(5) ? null : reader.GetGuid(5),
            reader.GetString(6), reader.GetString(7), reader.GetBoolean(8),
            reader.GetBoolean(9),
            reader.IsDBNull(10) ? null : reader.GetDouble(10),
            reader.IsDBNull(11) ? null : reader.GetDouble(11),
            reader.IsDBNull(12) ? null : reader.GetString(12),
            reader.IsDBNull(13) ? null : reader.GetString(13),
            reader.IsDBNull(14) ? null : ReadTimestamp(reader, 14),
            reader.IsDBNull(15) ? null : reader.GetString(15),
            reader.IsDBNull(16) ? null : reader.GetString(16));
    }

    private static DateTimeOffset ReadTimestamp(NpgsqlDataReader reader, int ordinal)
    {
        var value = reader.GetFieldValue<DateTime>(ordinal);
        return new DateTimeOffset(
            value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc));
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
    ///
    /// The round row is locked FOR SHARE before the insert and held for the
    /// whole short transaction. W9 terminalization locks the same round row
    /// FOR UPDATE before reading attempts: a terminal NO_DRIVER can therefore
    /// never commit from a stale attempt set while this new attempt is being
    /// created, and an attempt can never be created on a terminal round.
    /// Returns (insertedNew, row); Row is null when the round is no longer
    /// writable (terminal or cancellation requested).
    /// </summary>
    public async Task<(bool Inserted, MatchingAttemptRow? Row)> EnsureInflightAttemptAsync(
        Guid tripId, int round, int ordinal, Guid driverId, Guid reservationId,
        CancellationToken ct = default, string stage = "S0")
    {
        const string Insert = """
            INSERT INTO matching_attempts
                (trip_id, matching_round, ordinal, driver_id, reservation_id, state, stage)
            VALUES (@trip, @round, @ordinal, @driver, @reservation, 'IN_FLIGHT', @stage);
            """;
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        if (!await IsRoundWritableForShareAsync(conn, tx, tripId, round, ct))
        {
            await tx.RollbackAsync(ct);
            return (false, null);
        }

        try
        {
            await using var cmd = new NpgsqlCommand(Insert, conn, tx);
            cmd.Parameters.AddWithValue("trip", tripId);
            cmd.Parameters.AddWithValue("round", round);
            cmd.Parameters.AddWithValue("ordinal", ordinal);
            cmd.Parameters.AddWithValue("driver", driverId);
            cmd.Parameters.AddWithValue("reservation", reservationId);
            cmd.Parameters.AddWithValue("stage", stage);
            await cmd.ExecuteNonQueryAsync(ct);
            await tx.CommitAsync(ct);

            // B-W12-02: one durable ATTEMPT IDENTITY per inserted row. A
            // duplicate worker converges on the existing row and is not
            // counted again; retries/reconnects reuse the same reservation.
            _metrics?.AttemptCreated();

            return (true, new MatchingAttemptRow(
                tripId, round, ordinal, driverId, reservationId,
                MatchingAttemptStates.InFlight, null, null, stage));
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation
            && string.Equals(ex.ConstraintName, "matching_attempts_pkey", StringComparison.Ordinal))
        {
            // Same ordinal already attempted (duplicate worker / redelivery):
            // converge to the existing row and its reservation identity.
            // Release the share lock before the read so a waiting terminalizer
            // is not held behind this losing duplicate.
            await tx.RollbackAsync(ct);
            var existing = await LoadAttemptAsync(tripId, round, ordinal, ct);
            return (false, existing);
        }
    }

    public async Task<MatchingAttemptRow?> LoadAttemptAsync(
        Guid tripId, int round, int ordinal, CancellationToken ct = default)
    {
        const string Sql = """
            SELECT trip_id, matching_round, ordinal, driver_id, reservation_id, state, last_error_code, max_dispatched_version, stage
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
            SELECT trip_id, matching_round, ordinal, driver_id, reservation_id, state, last_error_code, max_dispatched_version, stage
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

    public async Task<IReadOnlyList<MatchingAttemptRow>> LoadAttemptsByStageAsync(
        Guid tripId, int round, string stage, CancellationToken ct = default)
    {
        const string Sql = """
            SELECT trip_id, matching_round, ordinal, driver_id, reservation_id, state, last_error_code,
                   max_dispatched_version, stage
            FROM matching_attempts
            WHERE trip_id = @trip AND matching_round = @round AND stage = @stage
            ORDER BY ordinal;
            """;
        var rows = new List<MatchingAttemptRow>();
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(Sql, conn);
        cmd.Parameters.AddWithValue("trip", tripId);
        cmd.Parameters.AddWithValue("round", round);
        cmd.Parameters.AddWithValue("stage", stage);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(ReadAttempt(reader));
        }

        return rows;
    }

    /// <summary>DSI-017/022 guard: any IN_FLIGHT/UNKNOWN attempt in the round.</summary>
    public async Task<bool> HasUnresolvedAsync(
        Guid tripId, int round, CancellationToken ct = default)
    {
        const string Sql = """
            SELECT EXISTS (
                SELECT 1 FROM matching_attempts
                WHERE trip_id = @trip AND matching_round = @round
                  AND state IN ('IN_FLIGHT', 'UNKNOWN'));
            """;
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(Sql, conn);
        cmd.Parameters.AddWithValue("trip", tripId);
        cmd.Parameters.AddWithValue("round", round);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    /// <summary>
    /// Durable worker bookkeeping: records this check instant and the last
    /// execution error, so the dispatcher's backoff survives restart and a
    /// dependency outage is visible without inventing a terminal outcome.
    /// </summary>
    public async Task SetWorkCheckAsync(
        Guid tripId, int round, DateTimeOffset checkedAt, string? errorCode,
        CancellationToken ct = default)
    {
        const string Sql = """
            UPDATE matching_rounds
            SET work_checked_at = @at, work_error_code = @err
            WHERE trip_id = @trip AND matching_round = @round;
            """;
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(Sql, conn);
        cmd.Parameters.AddWithValue("trip", tripId);
        cmd.Parameters.AddWithValue("round", round);
        cmd.Parameters.AddWithValue("at", checkedAt);
        cmd.Parameters.AddWithValue("err", (object?)errorCode ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SetAttemptLastCheckedAsync(
        Guid tripId, int round, int ordinal, DateTimeOffset checkedAt,
        CancellationToken ct = default)
    {
        const string Sql = """
            UPDATE matching_attempts
            SET last_checked_at = @at, updated_at = now()
            WHERE trip_id = @trip AND matching_round = @round AND ordinal = @ordinal
              AND state IN ('IN_FLIGHT', 'UNKNOWN');
            """;
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(Sql, conn);
        cmd.Parameters.AddWithValue("trip", tripId);
        cmd.Parameters.AddWithValue("round", round);
        cmd.Parameters.AddWithValue("ordinal", ordinal);
        cmd.Parameters.AddWithValue("at", checkedAt);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Closes READY attempts at a cancellation boundary (no effect was sent),
    /// then marks the round CANCELLED only when no unresolved or reserved
    /// effect remains. An unresolved attempt keeps the round PROCESSING so it
    /// is still reconciled; a reservation that committed wins (Trip performs
    /// exact cleanup per Doc 02 §12.9).
    /// </summary>
    public async Task<bool> TryMarkRoundCancelledAsync(
        Guid tripId, int round, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        const string Lock = """
            SELECT 1 FROM matching_rounds
            WHERE trip_id = @trip AND matching_round = @round
              AND status = 'PROCESSING' AND cancel_requested = TRUE
            FOR UPDATE;
            """;
        await using (var lockCmd = new NpgsqlCommand(Lock, conn, tx))
        {
            lockCmd.Parameters.AddWithValue("trip", tripId);
            lockCmd.Parameters.AddWithValue("round", round);
            if (await lockCmd.ExecuteScalarAsync(ct) is null)
            {
                await tx.RollbackAsync(ct);
                return false;
            }
        }

        const string CancelReady = """
            UPDATE matching_attempts
            SET state = 'CANCELLED',
                last_error_code = COALESCE(last_error_code, 'CANCEL_REQUESTED'),
                updated_at = now()
            WHERE trip_id = @trip AND matching_round = @round AND state = 'READY';
            """;
        await using (var cancelCmd = new NpgsqlCommand(CancelReady, conn, tx))
        {
            cancelCmd.Parameters.AddWithValue("trip", tripId);
            cancelCmd.Parameters.AddWithValue("round", round);
            await cancelCmd.ExecuteNonQueryAsync(ct);
        }

        const string CancelRound = """
            UPDATE matching_rounds SET status = 'CANCELLED', updated_at = now()
            WHERE trip_id = @trip AND matching_round = @round
              AND status = 'PROCESSING' AND cancel_requested = TRUE
              AND NOT EXISTS (
                  SELECT 1 FROM matching_attempts a
                  WHERE a.trip_id = @trip AND a.matching_round = @round
                    AND a.state IN ('IN_FLIGHT', 'UNKNOWN', 'RESERVED'));
            """;
        int rows;
        await using (var roundCmd = new NpgsqlCommand(CancelRound, conn, tx))
        {
            roundCmd.Parameters.AddWithValue("trip", tripId);
            roundCmd.Parameters.AddWithValue("round", round);
            rows = await roundCmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return rows == 1;
    }
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
              AND a.state IN ('READY', 'IN_FLIGHT', 'UNKNOWN');
            """;
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Same round-row lock discipline as terminalization: hold FOR SHARE
        // across the fence write so NO_DRIVER cannot commit between the
        // PROCESSING check and the authorized dispatch, and a terminalizer
        // that wins first makes this authorization fail closed.
        if (!await IsRoundWritableForShareAsync(conn, tx, tripId, round, ct))
        {
            await tx.RollbackAsync(ct);
            return false;
        }

        await using var cmd = new NpgsqlCommand(Sql, conn, tx);
        cmd.Parameters.AddWithValue("trip", tripId);
        cmd.Parameters.AddWithValue("round", round);
        cmd.Parameters.AddWithValue("ordinal", ordinal);
        cmd.Parameters.AddWithValue("v", dispatchedVersion);
        var authorized = await cmd.ExecuteNonQueryAsync(ct) == 1;
        await tx.CommitAsync(ct);
        return authorized;
    }

    /// <summary>
    /// Acquires the W9 serialization lock used by every operation that can
    /// add or authorize a new attempt. Share mode lets concurrent attempt
    /// creators/dispatchers proceed together, but excludes the terminalizer's
    /// FOR UPDATE. A terminal round or a cancellation request yields false.
    /// </summary>
    private static async Task<bool> IsRoundWritableForShareAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx,
        Guid tripId, int round, CancellationToken ct)
    {
        const string Sql = """
            SELECT 1 FROM matching_rounds
            WHERE trip_id = @trip AND matching_round = @round
              AND status = 'PROCESSING' AND cancel_requested = FALSE
            FOR SHARE;
            """;
        await using var cmd = new NpgsqlCommand(Sql, conn, tx);
        cmd.Parameters.AddWithValue("trip", tripId);
        cmd.Parameters.AddWithValue("round", round);
        return await cmd.ExecuteScalarAsync(ct) is not null;
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
        reader.FieldCount > 7 && !reader.IsDBNull(7) ? reader.GetInt32(7) : null,
        reader.FieldCount > 8 && !reader.IsDBNull(8) ? reader.GetString(8) : "S0");
}
