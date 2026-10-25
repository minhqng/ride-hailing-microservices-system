// B-W09-03/04/08: atomic terminal Matching result (Doc 02 §12.8, DSI-035,
// DSR-010). NO_DRIVER is legal only when every durable attempt is
// conclusively REJECTED, no UNKNOWN/IN_FLIGHT/READY/RESERVED remains, the
// round is still PROCESSING, cancellation was not requested, and the frozen
// candidate snapshot (when present) is fully covered by attempt rows.
//
// The terminal row update and the Outbox(MatchingFailedV1) intent commit in
// ONE database transaction. The outbox write never touches the broker; C's
// shared relay publishes only after commit. Any failure (including a missing
// Outbox schema) rolls the state change back, so no NO_DRIVER can exist
// without its durable publication intent.
//
// Concurrency: the round row is locked FOR UPDATE before any attempt is read.
// Attempt creation (MatchingRepository.InsertAttemptAsync,
// MatchingAttemptRepository.EnsureInflightAttemptAsync) and dispatch
// authorization (TryRecordDispatchAsync) take FOR SHARE on the same round
// row first. Therefore either a new attempt/dispatch commits before
// terminalization (and the terminalizer sees it and refuses) or the
// terminalizer commits first (and the attempt/dispatch is denied). A
// reconcile transition only touches attempt rows, so the attempt FOR UPDATE
// read serializes with it as well.
using System.Text.Json;
using Npgsql;
using RideHailing.MatchingService.Application.Messaging;
using RideHailing.MatchingService.Infrastructure.Persistence;
using RideHailing.Messaging.Contracts;
using RideHailing.Messaging.Outbox;

namespace RideHailing.MatchingService.Application.Reserve;

public enum MatchingTerminalOutcome
{
    NoDriver,
    Reserved,
    Refused,
}

public sealed record MatchingTerminalResult(
    MatchingTerminalOutcome Outcome,
    string? ResultMessageId = null,
    Guid? DriverId = null,
    Guid? ReservationId = null,
    string? Reason = null);

public static class MatchingTerminalReasons
{
    public const string CancelRequested = "CANCEL_REQUESTED";
    public const string UnresolvedAttempt = "UNRESOLVED_ATTEMPT";
    public const string SnapshotIncomplete = "SNAPSHOT_INCOMPLETE";
    public const string RoundNotFound = "ROUND_NOT_FOUND";
}

public sealed class MatchingTerminalizer
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly NpgsqlOutboxStore _outbox;
    private readonly TimeProvider _clock;

    public MatchingTerminalizer(
        NpgsqlDataSource dataSource,
        NpgsqlOutboxStore outbox,
        TimeProvider? clock = null)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// Commits NO_DRIVER + MatchingFailedV1 together, or reports why the
    /// terminal is not legal. A round already in NO_DRIVER is an exact replay:
    /// the persisted identity is reused and a missing intent is repaired with
    /// that same identity (never a new logical result).
    /// </summary>
    public async Task<MatchingTerminalResult> TryTerminalizeNoDriverAsync(
        Guid tripId, int matchingRound, CancellationToken ct = default)
    {
        if (tripId == Guid.Empty)
        {
            throw new ArgumentException("tripId must not be empty.", nameof(tripId));
        }

        if (matchingRound < 1)
        {
            throw new ArgumentException("matchingRound must be >= 1.", nameof(matchingRound));
        }

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var round = await LoadRoundForUpdateAsync(conn, tx, tripId, matchingRound, ct);
        if (round is null)
        {
            await tx.RollbackAsync(ct);
            return new MatchingTerminalResult(
                MatchingTerminalOutcome.Refused, Reason: MatchingTerminalReasons.RoundNotFound);
        }

        var expectedIdentity = MatchingResultMessageIds.ForNoDriver(tripId, matchingRound);

        if (string.Equals(round.Status, MatchingRoundStatuses.NoDriver, StringComparison.Ordinal))
        {
            if (round.ResultMessageId is null)
            {
                throw new InvalidOperationException(
                    $"Matching round {tripId:D}/{matchingRound} is NO_DRIVER without a persisted result identity.");
            }

            var persisted = round.ResultMessageId.Value.ToString("D");
            if (!string.Equals(persisted, expectedIdentity, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Matching round {tripId:D}/{matchingRound} has result identity '{persisted}' " +
                    $"but '{expectedIdentity}' is required for its logical terminal result.");
            }

            await EnsureOutboxIntentAsync(
                conn, tx, CreateFailureMessage(tripId, matchingRound, round.InputMessageId), ct);
            await tx.CommitAsync(ct);
            return new MatchingTerminalResult(
                MatchingTerminalOutcome.NoDriver, ResultMessageId: persisted);
        }

        if (string.Equals(round.Status, MatchingRoundStatuses.Reserved, StringComparison.Ordinal))
        {
            await tx.RollbackAsync(ct);
            return new MatchingTerminalResult(
                MatchingTerminalOutcome.Reserved,
                DriverId: round.SelectedDriverId,
                ReservationId: round.SelectedReservationId,
                Reason: MatchingRoundStatuses.Reserved);
        }

        if (!string.Equals(round.Status, MatchingRoundStatuses.Processing, StringComparison.Ordinal))
        {
            await tx.RollbackAsync(ct);
            return new MatchingTerminalResult(
                MatchingTerminalOutcome.Refused, Reason: round.Status);
        }

        if (round.CancelRequested)
        {
            await tx.RollbackAsync(ct);
            return new MatchingTerminalResult(
                MatchingTerminalOutcome.Refused, Reason: MatchingTerminalReasons.CancelRequested);
        }

        if (round.ResultMessageId is not null)
        {
            throw new InvalidOperationException(
                $"Matching round {tripId:D}/{matchingRound} is PROCESSING but already carries a result identity.");
        }

        var attempts = await LoadAttemptsForUpdateAsync(conn, tx, tripId, matchingRound, ct);

        var reserved = attempts.FirstOrDefault(a =>
            string.Equals(a.State, MatchingAttemptStates.Reserved, StringComparison.Ordinal));
        if (reserved is not null)
        {
            await tx.RollbackAsync(ct);
            return new MatchingTerminalResult(
                MatchingTerminalOutcome.Reserved,
                DriverId: reserved.DriverId,
                ReservationId: reserved.ReservationId,
                Reason: MatchingRoundStatuses.Reserved);
        }

        if (attempts.Any(a => !string.Equals(a.State, MatchingAttemptStates.Rejected, StringComparison.Ordinal)))
        {
            await tx.RollbackAsync(ct);
            return new MatchingTerminalResult(
                MatchingTerminalOutcome.Refused, Reason: MatchingTerminalReasons.UnresolvedAttempt);
        }

        if (!SnapshotCovered(round.SnapshotJson, attempts))
        {
            await tx.RollbackAsync(ct);
            return new MatchingTerminalResult(
                MatchingTerminalOutcome.Refused, Reason: MatchingTerminalReasons.SnapshotIncomplete);
        }

        var message = CreateFailureMessage(tripId, matchingRound, round.InputMessageId);

        const string UpdateRound = """
            UPDATE matching_rounds
            SET status = 'NO_DRIVER',
                result_message_id = @result,
                selected_driver_id = NULL,
                selected_reservation_id = NULL,
                updated_at = now()
            WHERE trip_id = @trip AND matching_round = @round
              AND status = 'PROCESSING' AND cancel_requested = FALSE;
            """;
        await using (var update = new NpgsqlCommand(UpdateRound, conn, tx))
        {
            update.Parameters.AddWithValue("trip", tripId);
            update.Parameters.AddWithValue("round", matchingRound);
            update.Parameters.AddWithValue("result", Guid.Parse(expectedIdentity));
            if (await update.ExecuteNonQueryAsync(ct) != 1)
            {
                throw new InvalidOperationException(
                    $"Matching round {tripId:D}/{matchingRound} stopped being terminalizable under its row lock.");
            }
        }

        await EnsureOutboxIntentAsync(conn, tx, message, ct);
        await tx.CommitAsync(ct);

        return new MatchingTerminalResult(
            MatchingTerminalOutcome.NoDriver, ResultMessageId: expectedIdentity);
    }

    private OutboxMessage CreateFailureMessage(Guid tripId, int matchingRound, string inputMessageId)
    {
        var envelope = MessageEnvelope<MatchingFailedPayload>.Create(
            MatchingEventTypes.MatchingFailed,
            correlationId: inputMessageId,
            payload: new MatchingFailedPayload(tripId, matchingRound),
            causationId: inputMessageId,
            messageId: MatchingResultMessageIds.ForNoDriver(tripId, matchingRound),
            occurredAt: _clock.GetUtcNow());

        return OutboxMessage.Create(
            envelope, MatchingEventRoutes.EventsExchange, MatchingEventRoutes.MatchingFailed);
    }

    private async Task EnsureOutboxIntentAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, OutboxMessage message, CancellationToken ct)
    {
        var existing = await LoadOutboxForUpdateAsync(conn, tx, message.MessageId, ct);
        if (existing is not null)
        {
            ValidateOutboxContent(existing, message);
            return;
        }

        var inserted = await _outbox.EnqueueAsync(conn, tx, message, ct);
        if (inserted)
        {
            return;
        }

        // ON CONFLICT DO NOTHING lost to an existing intent: read and verify
        // it is the same logical result instead of silently accepting a
        // different payload under a reused identity.
        existing = await LoadOutboxForUpdateAsync(conn, tx, message.MessageId, ct)
            ?? throw new InvalidOperationException(
                $"Outbox intent '{message.MessageId}' could not be inserted or loaded.");
        ValidateOutboxContent(existing, message);
    }

    private static void ValidateOutboxContent(
        OutboxIntent existing, OutboxMessage expected)
    {
        if (!string.Equals(existing.MessageType, expected.MessageType, StringComparison.Ordinal)
            || existing.SchemaVersion != expected.SchemaVersion
            || !string.Equals(existing.Exchange, expected.Exchange, StringComparison.Ordinal)
            || !string.Equals(existing.RoutingKey, expected.RoutingKey, StringComparison.Ordinal)
            || !PayloadEquals(existing.PayloadJson, expected.PayloadJson))
        {
            throw new InvalidOperationException(
                $"Outbox intent '{expected.MessageId}' already exists with different content.");
        }
    }

    private static bool PayloadEquals(string left, string right)
    {
        using var a = JsonDocument.Parse(left);
        using var b = JsonDocument.Parse(right);
        return JsonElement.DeepEquals(a.RootElement, b.RootElement);
    }

    private async Task<RoundTerminalState?> LoadRoundForUpdateAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx,
        Guid tripId, int matchingRound, CancellationToken ct)
    {
        const string Sql = """
            SELECT status, cancel_requested, result_message_id, input_message_id,
                   selected_driver_id, selected_reservation_id,
                   candidate_snapshot_json::text
            FROM matching_rounds
            WHERE trip_id = @trip AND matching_round = @round
            FOR UPDATE;
            """;
        await using var cmd = new NpgsqlCommand(Sql, conn, tx);
        cmd.Parameters.AddWithValue("trip", tripId);
        cmd.Parameters.AddWithValue("round", matchingRound);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return new RoundTerminalState(
            reader.GetString(0),
            reader.GetBoolean(1),
            reader.IsDBNull(2) ? null : reader.GetGuid(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetGuid(4),
            reader.IsDBNull(5) ? null : reader.GetGuid(5),
            reader.IsDBNull(6) ? null : reader.GetString(6));
    }

    private static async Task<IReadOnlyList<AttemptTerminalState>> LoadAttemptsForUpdateAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx,
        Guid tripId, int matchingRound, CancellationToken ct)
    {
        const string Sql = """
            SELECT ordinal, driver_id, reservation_id, state
            FROM matching_attempts
            WHERE trip_id = @trip AND matching_round = @round
            ORDER BY ordinal
            FOR UPDATE;
            """;
        await using var cmd = new NpgsqlCommand(Sql, conn, tx);
        cmd.Parameters.AddWithValue("trip", tripId);
        cmd.Parameters.AddWithValue("round", matchingRound);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<AttemptTerminalState>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new AttemptTerminalState(
                reader.GetInt32(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetString(3)));
        }

        return rows;
    }

    private async Task<OutboxIntent?> LoadOutboxForUpdateAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string messageId, CancellationToken ct)
    {
        const string Sql = """
            SELECT message_type, schema_version, exchange, routing_key, payload_json::text
            FROM outbox_messages
            WHERE message_id = @id
            FOR UPDATE;
            """;
        await using var cmd = new NpgsqlCommand(Sql, conn, tx);
        cmd.Parameters.AddWithValue("id", messageId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return new OutboxIntent(
            reader.GetString(0), reader.GetInt32(1), reader.GetString(2),
            reader.GetString(3), reader.GetString(4));
    }

    /// <summary>
    /// The frozen snapshot defines the bounded candidate sequence. Every
    /// snapshot entry must have its durable attempt row (same ordinal and
    /// driver) before the round can be called exhausted. A missing attempt
    /// means a candidate was never driven to a conclusive outcome.
    /// </summary>
    private static bool SnapshotCovered(
        string? snapshotJson, IReadOnlyList<AttemptTerminalState> attempts)
    {
        if (string.IsNullOrWhiteSpace(snapshotJson))
        {
            return true;
        }

        using var document = JsonDocument.Parse(snapshotJson);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("Matching candidate snapshot is not a JSON array.");
        }

        var byIdentity = attempts.ToDictionary(
            attempt => (attempt.Ordinal, attempt.DriverId));
        foreach (var entry in document.RootElement.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object
                || !entry.TryGetProperty("ordinal", out var ordinalElement)
                || !ordinalElement.TryGetInt32(out var ordinal)
                || !entry.TryGetProperty("driverId", out var driverElement)
                || driverElement.ValueKind != JsonValueKind.String
                || !driverElement.TryGetGuid(out var driverId))
            {
                throw new InvalidOperationException(
                    "Matching candidate snapshot entry is missing ordinal/driverId.");
            }

            if (!byIdentity.ContainsKey((ordinal, driverId)))
            {
                return false;
            }
        }

        return true;
    }

    private sealed record RoundTerminalState(
        string Status,
        bool CancelRequested,
        Guid? ResultMessageId,
        string InputMessageId,
        Guid? SelectedDriverId,
        Guid? SelectedReservationId,
        string? SnapshotJson);

    private sealed record AttemptTerminalState(
        int Ordinal,
        Guid DriverId,
        Guid ReservationId,
        string State);

    private sealed record OutboxIntent(
        string MessageType,
        int SchemaVersion,
        string Exchange,
        string RoutingKey,
        string PayloadJson);
}
