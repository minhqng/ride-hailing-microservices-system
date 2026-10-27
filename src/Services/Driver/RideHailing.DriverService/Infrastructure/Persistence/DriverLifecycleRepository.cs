using System.Text.Json;
using Npgsql;
using RideHailing.DriverService.Application.Messaging;
using RideHailing.DriverService.Domain.Drivers;
using RideHailing.DriverService.Infrastructure.Telemetry;
using RideHailing.Messaging.Contracts;
using RideHailing.Messaging.Reliability;

namespace RideHailing.DriverService.Infrastructure.Persistence;

public sealed partial class DriverRepository
{
    public Task<DriverLifecycleResult> TryMarkTripStartedAsync(Guid driverId, Guid tripId,
        Guid reservationId, CancellationToken ct = default) =>
        ExecuteLifecycleAsync(driverId, tripId, reservationId, false, ct);

    public Task<DriverLifecycleResult> TryMarkTripCompletedAsync(Guid driverId, Guid tripId,
        Guid reservationId, CancellationToken ct = default) =>
        ExecuteLifecycleAsync(driverId, tripId, reservationId, true, ct);

    private async Task<DriverLifecycleResult> ExecuteLifecycleAsync(Guid driverId, Guid tripId,
        Guid reservationId, bool completed, CancellationToken ct)
    {
        ValidateLifecycleIdentity(driverId, tripId, reservationId);
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var result = await LifecycleInTransactionAsync(conn, tx, driverId, tripId, reservationId,
            completed, ct);
        await tx.CommitAsync(ct);
        return result;
    }

    public async Task<DriverLifecycleResult> HandleCommandAsync(
        MessageEnvelope<JsonElement> envelope, CancellationToken ct = default)
    {
        var command = DriverCommand.Parse(envelope);
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        // Consistent lock order: message identity, then Driver. Hash collisions
        // only serialize unrelated commands; exact receipt keys still decide identity.
        await using (var gate = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended(@id, 721094));", conn, tx))
        {
            gate.Parameters.AddWithValue("id", envelope.MessageId);
            await gate.ExecuteNonQueryAsync(ct);
        }
        await using (var receipt = new NpgsqlCommand("""
            SELECT fingerprint, disposition, version, event_message_id, reason
            FROM driver_command_receipts WHERE message_id = @id;
            """, conn, tx))
        {
            receipt.Parameters.AddWithValue("id", envelope.MessageId);
            await using var reader = await receipt.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                if (reader.GetString(0) != command.Fingerprint)
                    throw new DeliveryFailureException(FailureKind.Poison, "DRIVER_COMMAND_CONTENT_CONFLICT");
                return new(reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetInt32(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4));
            }
        }

        command.ValidateForExecution(envelope.MessageType);
        DriverLifecycleResult result;
        if (envelope.MessageType == DriverCommand.Release)
        {
            var release = await ReleaseInTransactionAsync(conn, tx, command.DriverId,
                command.TripId, command.ReservationId, ct, envelope, command.MatchingRound);
            result = release.Outcome switch
            {
                DriverMutationOutcome.Applied => new("Applied", release.Driver!.Version,
                    DriverEventMessageIds.ForDriverEffect(command.DriverId, command.ReservationId,
                        release.Driver.Version, "AVAILABLE")),
                DriverMutationOutcome.Unchanged => new("Replayed", release.Driver!.Version),
                DriverMutationOutcome.DriverNotFound => new("NotFound", Reason: "DRIVER_NOT_FOUND"),
                _ => new("Rejected", Reason: release.ConflictCode)
            };
        }
        else result = await LifecycleInTransactionAsync(conn, tx, command.DriverId,
            command.TripId, command.ReservationId, envelope.MessageType == DriverCommand.Complete,
            ct, envelope);

        await using (var receipt = new NpgsqlCommand("""
            INSERT INTO driver_command_receipts
                (message_id, fingerprint, disposition, version, event_message_id, reason)
            VALUES (@id, @fingerprint, @disposition, @version, @event, @reason);
            """, conn, tx))
        {
            receipt.Parameters.AddWithValue("id", envelope.MessageId);
            receipt.Parameters.AddWithValue("fingerprint", command.Fingerprint);
            receipt.Parameters.AddWithValue("disposition", result.Disposition);
            receipt.Parameters.AddWithValue("version", (object?)result.Version ?? DBNull.Value);
            receipt.Parameters.AddWithValue("event", (object?)result.EventMessageId ?? DBNull.Value);
            receipt.Parameters.AddWithValue("reason", (object?)result.Reason ?? DBNull.Value);
            await receipt.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);

        // B-W12-01: the broker compensation path uses the same fencing rules
        // as HTTP release. Count only a committed fenced stale rejection (no
        // mutation); idempotent receipts and non-fencing refusals are excluded.
        if (envelope.MessageType == DriverCommand.Release
            && DriverMetrics.IsStaleReleaseBlocked(result.Reason))
        {
            _metrics?.StaleReleaseBlocked(DriverMetrics.ReleaseReasonClass(result.Reason));
        }

        return result;
    }

    private async Task<DriverLifecycleResult> LifecycleInTransactionAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Guid driverId, Guid tripId, Guid reservationId,
        bool completed, CancellationToken ct, MessageEnvelope<JsonElement>? command = null)
    {
        var current = await LoadForUpdateAsync(conn, tx, driverId, ct);
        if (current is null) return new("NotFound");
        var phase = completed ? "COMPLETED" : "STARTED";
        await using (var history = new NpgsqlCommand("""
            SELECT version, message_id FROM driver_lifecycle_effects
            WHERE driver_id = @driver AND trip_id = @trip AND reservation_id = @reservation AND phase = @phase;
            """, conn, tx))
        {
            AddIdentity(history, driverId, tripId, reservationId);
            history.Parameters.AddWithValue("phase", phase);
            await using var reader = await history.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
                return new("Replayed", reader.GetInt32(0), reader.GetString(1));
        }

        if (completed)
        {
            await using var origin = new NpgsqlCommand("""
                SELECT EXISTS (SELECT 1 FROM driver_lifecycle_effects
                WHERE driver_id = @driver AND trip_id = @trip AND reservation_id = @reservation AND phase = 'STARTED');
                """, conn, tx);
            AddIdentity(origin, driverId, tripId, reservationId);
            if (!(bool)(await origin.ExecuteScalarAsync(ct))!)
                return new("Rejected", Reason: "LIFECYCLE_PROVENANCE_REQUIRED");
        }
        try
        {
            if (completed) current.MarkTripCompleted(tripId);
            else current.MarkTripStarted(tripId, reservationId);
        }
        catch (DriverDomainException ex) { return new("Rejected", Reason: ex.Code); }

        await StoreAsync(conn, tx, current, ct);
        await _outbox.EnqueueLifecycleAsync(conn, tx, current, tripId, reservationId, completed, ct, command);
        var eventId = DriverEventMessageIds.ForDriverEffect(driverId, reservationId, current.Version,
            completed ? "AVAILABLE" : "ON_TRIP");
        await using (var history = new NpgsqlCommand("""
            INSERT INTO driver_lifecycle_effects
                (driver_id, trip_id, reservation_id, phase, version, message_id, occurred_at)
            VALUES (@driver, @trip, @reservation, @phase, @version, @message, @at);
            """, conn, tx))
        {
            AddIdentity(history, driverId, tripId, reservationId);
            history.Parameters.AddWithValue("phase", phase);
            history.Parameters.AddWithValue("version", current.Version);
            history.Parameters.AddWithValue("message", eventId);
            history.Parameters.AddWithValue("at", current.UpdatedAt);
            await history.ExecuteNonQueryAsync(ct);
        }
        return new("Applied", current.Version, eventId);
    }

    private static void ValidateLifecycleIdentity(Guid driverId, Guid tripId, Guid reservationId)
    {
        if (driverId == Guid.Empty || tripId == Guid.Empty || reservationId == Guid.Empty)
            throw new ArgumentException("driverId/tripId/reservationId must not be empty.");
    }

    private static void AddIdentity(NpgsqlCommand command, Guid driverId, Guid tripId, Guid reservationId)
    {
        command.Parameters.AddWithValue("driver", driverId);
        command.Parameters.AddWithValue("trip", tripId);
        command.Parameters.AddWithValue("reservation", reservationId);
    }
}
