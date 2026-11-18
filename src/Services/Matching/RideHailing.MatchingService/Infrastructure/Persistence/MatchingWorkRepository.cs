// Durable Matching work coordination.
//
// The broker intake ACKs only after the round row durably owns the work
// (pickup + auto_execute). This repository is the worker side of that
// contract: bounded due-round discovery, a cross-process advisory lease per
// round, atomic stage-snapshot/attempt creation, gapless stage advancement,
// and cancellation intents that survive queue reordering.
//
// No in-memory queue and no process-global ownership: every safety decision
// is a PostgreSQL transaction against the durable round/attempt ledger.
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using RideHailing.MatchingService.Application.Matching;
using RideHailing.MatchingService.Domain.Matching;

namespace RideHailing.MatchingService.Infrastructure.Persistence;

public sealed record StageCandidate(
    int Ordinal,
    Guid DriverId,
    Guid ReservationId,
    double DistanceKm,
    string Stage);

public sealed record StageCandidateSeed(
    Guid DriverId,
    Guid ReservationId,
    double DistanceKm,
    double Score,
    int Version,
    DateTimeOffset CapturedAt);

public enum StageEnsureOutcome
{
    Ready,
    RoundGone,
    NotWritable,
    StageMismatch,
}

public sealed record StageEnsureResult(
    StageEnsureOutcome Outcome,
    IReadOnlyList<StageCandidate> Candidates,
    string? Reason = null);

public enum StageAdvanceOutcome
{
    Advanced,
    RoundGone,
    NotWritable,
    NotCurrentStage,
    BlockedUnresolved,
    StageNotExhausted,
}

public sealed class MatchingWorkRepository
{
    private static readonly JsonSerializerOptions SnapshotJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _connectionString;
    private readonly MatchingMetrics? _metrics;

    public MatchingWorkRepository(string connectionString, MatchingMetrics? metrics = null)
    {
        _connectionString = connectionString;
        _metrics = metrics;
    }

    /// <summary>
    /// PROCESSING production rounds whose last worker check is older than the
    /// backoff. NULL (never checked) is first; ordering is deterministic.
    /// </summary>
    public async Task<IReadOnlyList<(Guid TripId, int Round)>> ListDueRoundsAsync(
        DateTimeOffset cutoff, int limit, CancellationToken ct = default)
    {
        const string Sql = """
            SELECT trip_id, matching_round
            FROM matching_rounds
            WHERE status = 'PROCESSING' AND auto_execute = TRUE
              AND (work_checked_at IS NULL OR work_checked_at <= @cutoff)
            ORDER BY work_checked_at NULLS FIRST, updated_at, trip_id, matching_round
            LIMIT @limit;
            """;
        var rows = new List<(Guid, int)>();
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(Sql, conn);
        cmd.Parameters.AddWithValue("cutoff", cutoff);
        cmd.Parameters.AddWithValue("limit", Math.Max(1, limit));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add((reader.GetGuid(0), reader.GetInt32(1)));
        }

        return rows;
    }

    /// <summary>
    /// Reads the frozen entries for one stage (empty when the stage has no
    /// candidates or has not been evaluated yet — StageEvaluated distinguishes
    /// the two on the round row).
    /// </summary>
    public async Task<IReadOnlyList<StageCandidate>> LoadStageCandidatesAsync(
        Guid tripId, int round, string stage, CancellationToken ct = default)
    {
        const string Sql = """
            SELECT candidate_snapshot_json::text
            FROM matching_rounds
            WHERE trip_id = @trip AND matching_round = @round;
            """;
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(Sql, conn);
        cmd.Parameters.AddWithValue("trip", tripId);
        cmd.Parameters.AddWithValue("round", round);
        var json = await cmd.ExecuteScalarAsync(ct) as string;
        return ParseSnapshot(json)
            .Where(e => string.Equals(e.ResolvedStage, stage, StringComparison.Ordinal))
            .OrderBy(e => e.Ordinal)
            .Select(e => new StageCandidate(e.Ordinal, e.DriverId, e.ReservationId, e.DistanceKm, e.ResolvedStage))
            .ToList();
    }

    /// <summary>
    /// Freezes one stage's deterministic candidate sequence and creates its
    /// READY attempt identities in ONE transaction. A crash can therefore
    /// never leave a frozen sequence without its durable attempt identities,
    /// and concurrent workers serialize on the round row before deciding.
    /// Existing stage entries are replayed verbatim (no new identities).
    /// </summary>
    public async Task<StageEnsureResult> EnsureStageSnapshotAsync(
        Guid tripId,
        int round,
        string stage,
        IReadOnlyList<StageCandidateSeed> seeds,
        string rankingConfigVersion,
        DateTimeOffset rankedAt,
        CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var roundState = await LoadRoundForUpdateAsync(conn, tx, tripId, round, ct);
        if (roundState is null)
        {
            await tx.RollbackAsync(ct);
            return new StageEnsureResult(StageEnsureOutcome.RoundGone, []);
        }

        if (!string.Equals(roundState.Status, MatchingRoundStatuses.Processing, StringComparison.Ordinal)
            || roundState.CancelRequested)
        {
            await tx.RollbackAsync(ct);
            return new StageEnsureResult(
                StageEnsureOutcome.NotWritable, [], roundState.CancelRequested ? "CANCEL_REQUESTED" : roundState.Status);
        }

        if (!string.Equals(roundState.CurrentStage, stage, StringComparison.Ordinal))
        {
            await tx.RollbackAsync(ct);
            return new StageEnsureResult(StageEnsureOutcome.StageMismatch, [], roundState.CurrentStage);
        }

        var existing = ParseSnapshot(roundState.SnapshotJson);
        var stageEntries = existing
            .Where(e => string.Equals(e.ResolvedStage, stage, StringComparison.Ordinal))
            .OrderBy(e => e.Ordinal)
            .ToList();

        if (stageEntries.Count > 0)
        {
            // Frozen stage replay: only TRULY missing rows are healed. Healed
            // rows reuse a frozen identity, so they are not new logical
            // attempts and must not inflate matching.attempt.created_total.
            await EnsureAttemptsForEntriesAsync(conn, tx, tripId, round, stage, stageEntries, ct);
            await MarkStageEvaluatedAsync(conn, tx, tripId, round, stage, ct);
            await tx.CommitAsync(ct);
            return new StageEnsureResult(
                StageEnsureOutcome.Ready,
                stageEntries.Select(e => new StageCandidate(
                    e.Ordinal, e.DriverId, e.ReservationId, e.DistanceKm, e.ResolvedStage)).ToList());
        }

        var nextOrdinal = Math.Max(
            roundState.NextAttemptOrdinal,
            existing.Count == 0 ? 0 : existing.Max(e => e.Ordinal) + 1);
        var merged = new List<PersistedSnapshotEntry>(existing);
        var created = new List<PersistedSnapshotEntry>(seeds.Count);
        foreach (var seed in seeds)
        {
            var entry = new PersistedSnapshotEntry(
                nextOrdinal++, seed.DriverId, seed.ReservationId, seed.DistanceKm,
                seed.Score, seed.Version, seed.CapturedAt, stage);
            merged.Add(entry);
            created.Add(entry);
        }

        if (created.Count > 0)
        {
            await InsertReadyAttemptsAsync(conn, tx, tripId, round, created, ct);
        }

        var hash = CandidateRanker.SnapshotHash(
            merged.Select(e => new SnapshotEntry(
                e.Ordinal, e.DriverId, e.ReservationId, e.DistanceKm, e.Score,
                e.Version, e.CapturedAt, e.ResolvedStage)).ToList(),
            rankingConfigVersion,
            rankedAt);

        const string Update = """
            UPDATE matching_rounds
            SET candidate_snapshot_json = @json,
                candidate_snapshot_hash = @hash,
                candidate_count = @count,
                next_attempt_ordinal = @next,
                stage_evaluated = @stage,
                updated_at = now()
            WHERE trip_id = @trip AND matching_round = @round
              AND status = 'PROCESSING' AND current_stage = @stage;
            """;
        await using (var update = new NpgsqlCommand(Update, conn, tx))
        {
            update.Parameters.AddWithValue("json", NpgsqlTypes.NpgsqlDbType.Jsonb,
                JsonSerializer.Serialize(merged, SnapshotJson));
            update.Parameters.AddWithValue("hash", hash);
            update.Parameters.AddWithValue("count", merged.Count);
            update.Parameters.AddWithValue("next", nextOrdinal);
            update.Parameters.AddWithValue("trip", tripId);
            update.Parameters.AddWithValue("round", round);
            update.Parameters.AddWithValue("stage", stage);
            if (await update.ExecuteNonQueryAsync(ct) != 1)
            {
                throw new InvalidOperationException(
                    $"Matching round {tripId:D}/{round} changed while freezing stage {stage}.");
            }
        }

        await tx.CommitAsync(ct);

        // L4: the initial stage snapshot mints NEW durable attempt identities;
        // count them only after the commit, never on a failed/rolled-back
        // insert and never on the frozen-stage replay/heal path above.
        _metrics?.AttemptCreated(created.Count);

        return new StageEnsureResult(
            StageEnsureOutcome.Ready,
            created.Select(e => new StageCandidate(
                e.Ordinal, e.DriverId, e.ReservationId, e.DistanceKm, e.ResolvedStage)).ToList());
    }

    /// <summary>
    /// Advances exactly one stage when every attempt and frozen
    /// snapshot entry considered by the current stage is conclusively
    /// REJECTED, no reservation succeeded, and no unresolved attempt exists.
    /// The transition is a conditional UPDATE under the round row lock, so
    /// two workers can never advance the same round inconsistently.
    /// </summary>
    public async Task<StageAdvanceOutcome> TryAdvanceStageAsync(
        Guid tripId, int round, string fromStage, string toStage,
        CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var roundState = await LoadRoundForUpdateAsync(conn, tx, tripId, round, ct);
        if (roundState is null)
        {
            await tx.RollbackAsync(ct);
            return StageAdvanceOutcome.RoundGone;
        }

        if (!string.Equals(roundState.Status, MatchingRoundStatuses.Processing, StringComparison.Ordinal)
            || roundState.CancelRequested)
        {
            await tx.RollbackAsync(ct);
            return StageAdvanceOutcome.NotWritable;
        }

        if (!string.Equals(roundState.CurrentStage, fromStage, StringComparison.Ordinal))
        {
            await tx.RollbackAsync(ct);
            return StageAdvanceOutcome.NotCurrentStage;
        }

        var attempts = await LoadAttemptsForUpdateAsync(conn, tx, tripId, round, ct);
        if (attempts.Any(a => a.State is MatchingAttemptStates.InFlight
                or MatchingAttemptStates.Unknown or MatchingAttemptStates.Reserved))
        {
            await tx.RollbackAsync(ct);
            return StageAdvanceOutcome.BlockedUnresolved;
        }

        if (attempts.Any(a => string.Equals(a.Stage, fromStage, StringComparison.Ordinal)
                && !string.Equals(a.State, MatchingAttemptStates.Rejected, StringComparison.Ordinal)))
        {
            await tx.RollbackAsync(ct);
            return StageAdvanceOutcome.StageNotExhausted;
        }

        // A future stage must not already carry an executable row; advancing
        // into it while work exists would violate its own logical scope.
        if (attempts.Any(a => !string.Equals(a.Stage, fromStage, StringComparison.Ordinal)
                && string.Equals(a.State, MatchingAttemptStates.Ready, StringComparison.Ordinal)))
        {
            await tx.RollbackAsync(ct);
            return StageAdvanceOutcome.StageNotExhausted;
        }

        var entries = ParseSnapshot(roundState.SnapshotJson)
            .Where(e => string.Equals(e.ResolvedStage, fromStage, StringComparison.Ordinal))
            .ToList();
        if (entries.Count == 0
            && !string.Equals(roundState.StageEvaluated, fromStage, StringComparison.Ordinal))
        {
            await tx.RollbackAsync(ct);
            return StageAdvanceOutcome.StageNotExhausted;
        }

        var rejected = attempts
            .Where(a => string.Equals(a.Stage, fromStage, StringComparison.Ordinal)
                && string.Equals(a.State, MatchingAttemptStates.Rejected, StringComparison.Ordinal))
            .Select(a => (a.Ordinal, a.DriverId))
            .ToHashSet();
        if (entries.Any(e => !rejected.Contains((e.Ordinal, e.DriverId))))
        {
            await tx.RollbackAsync(ct);
            return StageAdvanceOutcome.StageNotExhausted;
        }

        const string Update = """
            UPDATE matching_rounds SET current_stage = @to, updated_at = now()
            WHERE trip_id = @trip AND matching_round = @round
              AND status = 'PROCESSING' AND cancel_requested = FALSE
              AND current_stage = @from;
            """;
        await using (var update = new NpgsqlCommand(Update, conn, tx))
        {
            update.Parameters.AddWithValue("to", toStage);
            update.Parameters.AddWithValue("from", fromStage);
            update.Parameters.AddWithValue("trip", tripId);
            update.Parameters.AddWithValue("round", round);
            if (await update.ExecuteNonQueryAsync(ct) != 1)
            {
                throw new InvalidOperationException(
                    $"Matching round {tripId:D}/{round} changed while advancing {fromStage}->{toStage}.");
            }
        }

        await tx.CommitAsync(ct);
        return StageAdvanceOutcome.Advanced;
    }

    /// <summary>
    /// Records CancelMatchingRoundV1 durably. The tombstone exists so a cancel
    /// that overtakes its RideRequestedV1 (independent queues) cannot be lost:
    /// the intake applies it atomically before ACK. An existing round is
    /// flagged in the same transaction.
    /// </summary>
    public async Task RecordCancelIntentAsync(
        Guid tripId, int round, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await AdvisoryXactLockAsync(conn, tx, CancelKey(tripId, round), ct);

        const string Insert = """
            INSERT INTO matching_cancel_intents (trip_id, matching_round)
            VALUES (@trip, @round)
            ON CONFLICT (trip_id, matching_round) DO NOTHING;
            """;
        await using (var insert = new NpgsqlCommand(Insert, conn, tx))
        {
            insert.Parameters.AddWithValue("trip", tripId);
            insert.Parameters.AddWithValue("round", round);
            await insert.ExecuteNonQueryAsync(ct);
        }

        const string Flag = """
            UPDATE matching_rounds SET cancel_requested = TRUE, updated_at = now()
            WHERE trip_id = @trip AND matching_round = @round AND status = 'PROCESSING';
            """;
        await using (var flag = new NpgsqlCommand(Flag, conn, tx))
        {
            flag.Parameters.AddWithValue("trip", tripId);
            flag.Parameters.AddWithValue("round", round);
            await flag.ExecuteNonQueryAsync(ct);
        }

        // A round that already committed a winning reservation will never
        // dispatch again: no additional READY attempt can cross the remote
        // effect boundary. Close those rows now so the cancellation proof
        // (and the invariant audit) sees a settled round instead of an
        // "unresolved" leftover. A PROCESSING round is left to the worker,
        // which closes READY rows under its round lease.
        const string CloseUnselected = """
            UPDATE matching_attempts a
            SET state = 'CANCELLED',
                last_error_code = COALESCE(last_error_code, 'NOT_SELECTED'),
                updated_at = now()
            WHERE a.trip_id = @trip AND a.matching_round = @round AND a.state = 'READY'
              AND EXISTS (
                  SELECT 1 FROM matching_rounds r
                  WHERE r.trip_id = @trip AND r.matching_round = @round
                    AND r.status = 'RESERVED');
            """;
        await using (var close = new NpgsqlCommand(CloseUnselected, conn, tx))
        {
            close.Parameters.AddWithValue("trip", tripId);
            close.Parameters.AddWithValue("round", round);
            await close.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    public async Task<bool> IsCancellationStoppedAsync(Guid tripId, int round, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand("""
            SELECT EXISTS (SELECT 1 FROM matching_cancel_intents WHERE trip_id=@trip AND matching_round=@round)
              AND NOT EXISTS (SELECT 1 FROM matching_attempts WHERE trip_id=@trip AND matching_round=@round
                              AND state IN ('READY','IN_FLIGHT','UNKNOWN'))
              AND NOT EXISTS (SELECT 1 FROM matching_rounds WHERE trip_id=@trip AND matching_round=@round
                              AND status NOT IN ('RESERVED','NO_DRIVER','CANCELLED') AND cancel_requested=FALSE);
            """, connection);
        command.Parameters.AddWithValue("trip", tripId);
        command.Parameters.AddWithValue("round", round);
        return (bool)(await command.ExecuteScalarAsync(ct))!;
    }

    /// <summary>
    /// Applies a cancel intent that arrived before its round. Must run after
    /// the round row exists; the tombstone is consumed exactly once.
    /// </summary>
    public async Task ApplyPendingCancelAsync(
        Guid tripId, int round, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await AdvisoryXactLockAsync(conn, tx, CancelKey(tripId, round), ct);

        const string LockRound = """
            SELECT 1 FROM matching_rounds
            WHERE trip_id = @trip AND matching_round = @round
            FOR UPDATE;
            """;
        await using (var lockRound = new NpgsqlCommand(LockRound, conn, tx))
        {
            lockRound.Parameters.AddWithValue("trip", tripId);
            lockRound.Parameters.AddWithValue("round", round);
            if (await lockRound.ExecuteScalarAsync(ct) is null)
            {
                // Never consume an intent without its round: a later intake
                // must still observe it.
                await tx.RollbackAsync(ct);
                return;
            }
        }

        const string Consume = """
            UPDATE matching_cancel_intents SET consumed_at = now()
            WHERE trip_id = @trip AND matching_round = @round AND consumed_at IS NULL
            RETURNING 1;
            """;
        bool pending;
        await using (var consume = new NpgsqlCommand(Consume, conn, tx))
        {
            consume.Parameters.AddWithValue("trip", tripId);
            consume.Parameters.AddWithValue("round", round);
            pending = await consume.ExecuteScalarAsync(ct) is not null;
        }

        if (pending)
        {
            const string Flag = """
                UPDATE matching_rounds SET cancel_requested = TRUE, updated_at = now()
                WHERE trip_id = @trip AND matching_round = @round AND status = 'PROCESSING';
                """;
            await using var flag = new NpgsqlCommand(Flag, conn, tx);
            flag.Parameters.AddWithValue("trip", tripId);
            flag.Parameters.AddWithValue("round", round);
            await flag.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    public async Task<bool> HasCancelIntentAsync(
        Guid tripId, int round, CancellationToken ct = default)
    {
        const string Sql = """
            SELECT EXISTS (
                SELECT 1 FROM matching_cancel_intents
                WHERE trip_id = @trip AND matching_round = @round);
            """;
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(Sql, conn);
        cmd.Parameters.AddWithValue("trip", tripId);
        cmd.Parameters.AddWithValue("round", round);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static string CancelKey(Guid tripId, int round) =>
        MatchingAdvisoryKeys.Cancel(tripId, round);

    private static async Task AdvisoryXactLockAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string key, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended(@key, 0));", conn, tx);
        cmd.Parameters.AddWithValue("key", key);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<RoundWorkState?> LoadRoundForUpdateAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx,
        Guid tripId, int round, CancellationToken ct)
    {
        const string Sql = """
            SELECT status, current_stage, cancel_requested,
                   candidate_snapshot_json::text, next_attempt_ordinal,
                   stage_evaluated
            FROM matching_rounds
            WHERE trip_id = @trip AND matching_round = @round
            FOR UPDATE;
            """;
        await using var cmd = new NpgsqlCommand(Sql, conn, tx);
        cmd.Parameters.AddWithValue("trip", tripId);
        cmd.Parameters.AddWithValue("round", round);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return new RoundWorkState(
            reader.GetString(0), reader.GetString(1), reader.GetBoolean(2),
            reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetInt32(4),
            reader.IsDBNull(5) ? null : reader.GetString(5));
    }

    private static async Task<IReadOnlyList<AttemptWorkState>> LoadAttemptsForUpdateAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx,
        Guid tripId, int round, CancellationToken ct)
    {
        const string Sql = """
            SELECT ordinal, driver_id, reservation_id, state, stage
            FROM matching_attempts
            WHERE trip_id = @trip AND matching_round = @round
            ORDER BY ordinal
            FOR UPDATE;
            """;
        await using var cmd = new NpgsqlCommand(Sql, conn, tx);
        cmd.Parameters.AddWithValue("trip", tripId);
        cmd.Parameters.AddWithValue("round", round);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<AttemptWorkState>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new AttemptWorkState(
                reader.GetInt32(0), reader.GetGuid(1), reader.GetGuid(2),
                reader.GetString(3), reader.GetString(4)));
        }

        return rows;
    }

    private static async Task MarkStageEvaluatedAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx,
        Guid tripId, int round, string stage, CancellationToken ct)
    {
        const string Sql = """
            UPDATE matching_rounds SET stage_evaluated = @stage
            WHERE trip_id = @trip AND matching_round = @round AND current_stage = @stage;
            """;
        await using var cmd = new NpgsqlCommand(Sql, conn, tx);
        cmd.Parameters.AddWithValue("trip", tripId);
        cmd.Parameters.AddWithValue("round", round);
        cmd.Parameters.AddWithValue("stage", stage);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task EnsureAttemptsForEntriesAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx,
        Guid tripId, int round, string stage,
        IReadOnlyList<PersistedSnapshotEntry> entries,
        CancellationToken ct)
    {
        var attempts = await LoadAttemptsForUpdateAsync(conn, tx, tripId, round, ct);
        foreach (var entry in entries)
        {
            var existing = attempts.FirstOrDefault(a =>
                a.Ordinal == entry.Ordinal && a.DriverId == entry.DriverId);
            if (existing is not null)
            {
                if (existing.ReservationId != entry.ReservationId)
                {
                    throw new InvalidOperationException(
                        $"Snapshot entry {entry.Ordinal}/{entry.DriverId:D} carries reservation " +
                        $"{entry.ReservationId:D} but the ledger has {existing.ReservationId:D}.");
                }

                continue;
            }

            await InsertReadyAttemptsAsync(conn, tx, tripId, round, [entry], ct);
        }
    }

    private static async Task InsertReadyAttemptsAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx,
        Guid tripId, int round,
        IReadOnlyList<PersistedSnapshotEntry> entries,
        CancellationToken ct)
    {
        const string Insert = """
            INSERT INTO matching_attempts
                (trip_id, matching_round, ordinal, driver_id, reservation_id, state, stage)
            VALUES (@trip, @round, @ordinal, @driver, @reservation, 'READY', @stage);
            """;
        foreach (var entry in entries)
        {
            await using var cmd = new NpgsqlCommand(Insert, conn, tx);
            cmd.Parameters.AddWithValue("trip", tripId);
            cmd.Parameters.AddWithValue("round", round);
            cmd.Parameters.AddWithValue("ordinal", entry.Ordinal);
            cmd.Parameters.AddWithValue("driver", entry.DriverId);
            cmd.Parameters.AddWithValue("reservation", entry.ReservationId);
            cmd.Parameters.AddWithValue("stage", entry.ResolvedStage);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private static List<PersistedSnapshotEntry> ParseSnapshot(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        var entries = JsonSerializer.Deserialize<List<PersistedSnapshotEntry>>(json, SnapshotJson)
            ?? throw new InvalidOperationException("Matching candidate snapshot is unreadable.");
        return entries;
    }

    private sealed record RoundWorkState(
        string Status,
        string CurrentStage,
        bool CancelRequested,
        string? SnapshotJson,
        int NextAttemptOrdinal,
        string? StageEvaluated);

    private sealed record AttemptWorkState(
        int Ordinal,
        Guid DriverId,
        Guid ReservationId,
        string State,
        string Stage);

    private sealed record PersistedSnapshotEntry(
        int Ordinal,
        Guid DriverId,
        Guid ReservationId,
        double DistanceKm,
        double Score,
        int Version,
        DateTimeOffset CapturedAt,
        [property: JsonPropertyName("stage")] string? Stage)
    {
        [JsonIgnore]
        public string ResolvedStage => string.IsNullOrWhiteSpace(Stage) ? "S0" : Stage;
    }
}
