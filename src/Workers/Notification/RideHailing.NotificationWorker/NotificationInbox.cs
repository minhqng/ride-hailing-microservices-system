// DS03 recipient-addressed inbox (Doc 01 §40.2, Doc 02 §43, Doc 05C §48).
//
// The inbox is owned by notification_db and is derived, non-authoritative
// data. A row is available to a recipient only after it is committed with a
// deterministic notification id; the message receipt and the inbox row commit
// in one transaction, so a consumer crash after commit is replay-safe.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using RideHailing.Messaging.Contracts;

namespace RideHailing.NotificationWorker;

public sealed record NotificationInboxEntry(
    Guid NotificationId,
    Guid RecipientUserId,
    string RecipientRole,
    string NotificationType,
    Guid TripId,
    string SourceMessageId,
    string SourceEventType,
    string CorrelationId,
    string Content,
    DateTimeOffset CreatedAt,
    DateTimeOffset AvailableAt,
    DateTimeOffset? ReadAt,
    string DeliveryStatus);

public enum NotificationWriteResult
{
    Applied,
    Duplicate,
}

/// <summary>A duplicate message id carried different semantic content.</summary>
public sealed class NotificationContentConflictException(string message) : Exception(message);

public interface INotificationInboxStore
{
    Task<NotificationWriteResult> RecordNotificationAsync(
        NotificationInboxEntry entry, string fingerprint, CancellationToken token);

    Task<IReadOnlyList<NotificationInboxEntry>> ListForRecipientAsync(
        Guid recipientUserId, int limit, CancellationToken token);

    Task<NotificationInboxEntry?> GetForRecipientAsync(
        Guid recipientUserId, Guid notificationId, CancellationToken token);

    Task<NotificationInboxEntry?> MarkReadAsync(
        Guid recipientUserId, Guid notificationId, DateTimeOffset readAt, CancellationToken token);
}

public static class NotificationContent
{
    /// <summary>Bounded human-readable content; no token, credential or payment secret.</summary>
    public static string For(string notificationType) => notificationType switch
    {
        NotificationMessage.Types.RiderRideRequested =>
            "Your ride request has been created.",
        NotificationMessage.Types.RiderDriverAssigned =>
            "A driver has been assigned to your ride.",
        NotificationMessage.Types.RiderDriverRejected =>
            "The driver rejected your ride. You can request a new ride.",
        NotificationMessage.Types.RiderNoDriver =>
            "No driver was available for your ride.",
        NotificationMessage.Types.RiderTripCompleted =>
            "Your ride is complete.",
        NotificationMessage.Types.RiderRideCancelled =>
            "Your ride was cancelled.",
        NotificationMessage.Types.RiderPaymentFailed =>
            "Your payment could not be authorized, so the ride was cancelled.",
        NotificationMessage.Types.DriverTripAssigned =>
            "You have been assigned a new trip.",
        NotificationMessage.Types.DriverReservationReleased =>
            "Your trip assignment was released.",
        _ => throw new ArgumentException("Unsupported notification type.", nameof(notificationType)),
    };

    /// <summary>
    /// Deterministic id from the stable business identity. Redelivery after a
    /// commit, or a consumer restart, reproduces the same id.
    /// </summary>
    public static Guid DeriveNotificationId(string sourceMessageId, Guid recipientUserId, string notificationType)
    {
        var input = $"{sourceMessageId}|{recipientUserId:N}|{notificationType}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return new Guid(hash.AsSpan(0, 16));
    }

    /// <summary>Semantic fingerprint of the source event for conflict detection.</summary>
    public static string Fingerprint(MessageEnvelope<JsonElement> envelope)
    {
        var semantic = JsonSerializer.SerializeToElement(new
        {
            envelope.MessageType,
            envelope.MessageId,
            envelope.Payload,
        });
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(semantic)));
    }
}

public sealed partial class NotificationProjectionStore
{
    private const string InboxSchema = """
        ALTER TABLE notification_deliveries
            ADD COLUMN IF NOT EXISTS content_fingerprint text NULL;
        CREATE TABLE IF NOT EXISTS notification_source_messages (
            message_id text NOT NULL PRIMARY KEY,
            fingerprint text NOT NULL,
            first_seen_at timestamptz NOT NULL
        );
        CREATE TABLE IF NOT EXISTS notifications (
            notification_id uuid NOT NULL PRIMARY KEY,
            recipient_user_id uuid NOT NULL,
            recipient_role text NOT NULL CHECK (recipient_role IN ('Rider','Driver')),
            notification_type text NOT NULL,
            trip_id uuid NULL,
            source_message_id text NOT NULL,
            source_event_type text NOT NULL,
            correlation_id text NOT NULL,
            content text NOT NULL,
            created_at timestamptz NOT NULL,
            available_at timestamptz NOT NULL,
            read_at timestamptz NULL,
            delivery_status text NOT NULL
                CHECK (delivery_status IN ('INBOX_AVAILABLE','READ','FAILED_VISIBLE')),
            CONSTRAINT ux_notifications_source_recipient_type
                UNIQUE (source_message_id, recipient_user_id, notification_type)
        );
        CREATE INDEX IF NOT EXISTS ix_notifications_recipient_created
            ON notifications(recipient_user_id, created_at DESC);
        """;

    public async Task<NotificationWriteResult> RecordNotificationAsync(
        NotificationInboxEntry entry, string fingerprint, CancellationToken token)
    {
        await using var conn = await Source.OpenConnectionAsync(token).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(token).ConfigureAwait(false);

        // One source message identity has exactly one semantic content. This
        // ledger is global across recipients, so a reused messageId with a
        // changed recipient/payload is poison instead of an injection into a
        // second recipient's inbox.
        var sourceInserted = await InsertSourceMessageAsync(
            conn, tx, entry.SourceMessageId, fingerprint, entry.CreatedAt, token).ConfigureAwait(false);
        if (!sourceInserted)
        {
            var storedSource = await ReadSourceFingerprintAsync(
                conn, tx, entry.SourceMessageId, token).ConfigureAwait(false);
            if (storedSource is null ||
                !string.Equals(storedSource, fingerprint, StringComparison.Ordinal))
            {
                await tx.RollbackAsync(token).ConfigureAwait(false);
                throw new NotificationContentConflictException(
                    "The same source message id carried different notification content.");
            }
        }

        var inserted = await InsertReceiptAsync(conn, tx, entry, fingerprint, token)
            .ConfigureAwait(false);
        if (!inserted)
        {
            var (found, stored) = await ReadReceiptFingerprintAsync(conn, tx, entry.RecipientUserId,
                entry.SourceMessageId, token).ConfigureAwait(false);
            if (!found || stored is null ||
                !string.Equals(stored, fingerprint, StringComparison.Ordinal))
            {
                await tx.RollbackAsync(token).ConfigureAwait(false);
                throw new NotificationContentConflictException(
                    "The same source message id carried different notification content.");
            }

            await tx.CommitAsync(token).ConfigureAwait(false);
            return NotificationWriteResult.Duplicate;
        }

        const string sql = """
            INSERT INTO notifications(
                notification_id, recipient_user_id, recipient_role, notification_type,
                trip_id, source_message_id, source_event_type, correlation_id, content,
                created_at, available_at, read_at, delivery_status)
            VALUES(@id,@recipient,@role,@type,@trip,@message,@event,@correlation,@content,
                @created,@available,NULL,'INBOX_AVAILABLE')
            ON CONFLICT (source_message_id, recipient_user_id, notification_type) DO NOTHING
            """;
        await using (var command = new NpgsqlCommand(sql, conn, tx))
        {
            command.Parameters.AddWithValue("id", entry.NotificationId);
            command.Parameters.AddWithValue("recipient", entry.RecipientUserId);
            command.Parameters.AddWithValue("role", entry.RecipientRole);
            command.Parameters.AddWithValue("type", entry.NotificationType);
            command.Parameters.AddWithValue("trip", entry.TripId);
            command.Parameters.AddWithValue("message", entry.SourceMessageId);
            command.Parameters.AddWithValue("event", entry.SourceEventType);
            command.Parameters.AddWithValue("correlation", entry.CorrelationId);
            command.Parameters.AddWithValue("content", entry.Content);
            command.Parameters.AddWithValue("created", entry.CreatedAt);
            command.Parameters.AddWithValue("available", entry.AvailableAt);
            if (await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
            {
                await tx.RollbackAsync(token).ConfigureAwait(false);
                throw new NotificationContentConflictException(
                    "The inbox business identity already exists with a different notification id.");
            }
        }

        await tx.CommitAsync(token).ConfigureAwait(false);
        return NotificationWriteResult.Applied;
    }

    public async Task<IReadOnlyList<NotificationInboxEntry>> ListForRecipientAsync(
        Guid recipientUserId, int limit, CancellationToken token)
    {
        const string sql = """
            SELECT notification_id, recipient_user_id, recipient_role, notification_type,
                   trip_id, source_message_id, source_event_type, correlation_id, content,
                   created_at, available_at, read_at, delivery_status
            FROM notifications
            WHERE recipient_user_id = @recipient
            ORDER BY created_at DESC, notification_id
            LIMIT @limit
            """;
        await using var conn = await Source.OpenConnectionAsync(token).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, conn);
        command.Parameters.AddWithValue("recipient", recipientUserId);
        command.Parameters.AddWithValue("limit", limit);
        var rows = new List<NotificationInboxEntry>();
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
            rows.Add(ReadEntry(reader));
        return rows;
    }

    public async Task<NotificationInboxEntry?> GetForRecipientAsync(
        Guid recipientUserId, Guid notificationId, CancellationToken token)
    {
        const string sql = """
            SELECT notification_id, recipient_user_id, recipient_role, notification_type,
                   trip_id, source_message_id, source_event_type, correlation_id, content,
                   created_at, available_at, read_at, delivery_status
            FROM notifications
            WHERE recipient_user_id = @recipient AND notification_id = @id
            """;
        await using var conn = await Source.OpenConnectionAsync(token).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, conn);
        command.Parameters.AddWithValue("recipient", recipientUserId);
        command.Parameters.AddWithValue("id", notificationId);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        return await reader.ReadAsync(token).ConfigureAwait(false) ? ReadEntry(reader) : null;
    }

    public async Task<NotificationInboxEntry?> MarkReadAsync(
        Guid recipientUserId, Guid notificationId, DateTimeOffset readAt, CancellationToken token)
    {
        const string sql = """
            UPDATE notifications
            SET read_at = COALESCE(read_at, @readAt), delivery_status = 'READ'
            WHERE recipient_user_id = @recipient AND notification_id = @id
            """;
        await using var conn = await Source.OpenConnectionAsync(token).ConfigureAwait(false);
        await using (var command = new NpgsqlCommand(sql, conn))
        {
            command.Parameters.AddWithValue("recipient", recipientUserId);
            command.Parameters.AddWithValue("id", notificationId);
            command.Parameters.AddWithValue("readAt", readAt);
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }

        return await GetForRecipientAsync(recipientUserId, notificationId, token).ConfigureAwait(false);
    }

    public async Task<bool> PingAsync(CancellationToken token)
    {
        try
        {
            await using var conn = await Source.OpenConnectionAsync(token).ConfigureAwait(false);
            await using var command = new NpgsqlCommand("SELECT 1", conn);
            return (int)(await command.ExecuteScalarAsync(token).ConfigureAwait(false))! == 1;
        }
        catch (NpgsqlException)
        {
            return false;
        }
    }

    private async Task<bool> InsertReceiptAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx,
        NotificationInboxEntry entry, string fingerprint, CancellationToken token)
    {
        const string sql = """
            INSERT INTO notification_deliveries(
                consumer_scope, message_id, message_type, correlation_id, aggregate_id,
                state, first_seen_at, delivered_at, content_fingerprint)
            VALUES(@scope,@message,@type,@correlation,@aggregate,'DELIVERED',@at,@at,@fingerprint)
            ON CONFLICT (consumer_scope, message_id) DO NOTHING
            """;
        await using var command = new NpgsqlCommand(sql, conn, tx);
        command.Parameters.AddWithValue("scope", ReceiptScope(entry.RecipientUserId));
        command.Parameters.AddWithValue("message", entry.SourceMessageId);
        command.Parameters.AddWithValue("type", entry.SourceEventType);
        command.Parameters.AddWithValue("correlation", entry.CorrelationId);
        command.Parameters.AddWithValue("aggregate", entry.TripId.ToString("D"));
        command.Parameters.AddWithValue("at", entry.CreatedAt);
        command.Parameters.AddWithValue("fingerprint", fingerprint);
        return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) == 1;
    }

    private static async Task<bool> InsertSourceMessageAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string messageId,
        string fingerprint, DateTimeOffset firstSeenAt, CancellationToken token)
    {
        const string sql = """
            INSERT INTO notification_source_messages(message_id, fingerprint, first_seen_at)
            VALUES(@message, @fingerprint, @at)
            ON CONFLICT (message_id) DO NOTHING
            """;
        await using var command = new NpgsqlCommand(sql, conn, tx);
        command.Parameters.AddWithValue("message", messageId);
        command.Parameters.AddWithValue("fingerprint", fingerprint);
        command.Parameters.AddWithValue("at", firstSeenAt);
        return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) == 1;
    }

    private static async Task<string?> ReadSourceFingerprintAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string messageId, CancellationToken token)
    {
        const string sql = """
            SELECT fingerprint FROM notification_source_messages WHERE message_id = @message
            """;
        await using var command = new NpgsqlCommand(sql, conn, tx);
        command.Parameters.AddWithValue("message", messageId);
        return await command.ExecuteScalarAsync(token).ConfigureAwait(false) as string;
    }

    private static async Task<(bool Found, string? Fingerprint)> ReadReceiptFingerprintAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Guid recipientUserId,
        string messageId, CancellationToken token)
    {
        const string sql = """
            SELECT content_fingerprint FROM notification_deliveries
            WHERE consumer_scope = @scope AND message_id = @message
            """;
        await using var command = new NpgsqlCommand(sql, conn, tx);
        command.Parameters.AddWithValue("scope", ReceiptScope(recipientUserId));
        command.Parameters.AddWithValue("message", messageId);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false)) return (false, null);
        return (true, reader.IsDBNull(0) ? null : reader.GetString(0));
    }

    // A source event may legitimately address more than one recipient. The
    // per-recipient receipt dedup therefore includes the recipient; the global
    // notification_source_messages ledger preserves one semantic payload per
    // message id and rejects a changed recipient/payload as poison.
    private static string ReceiptScope(Guid recipientUserId) =>
        $"{NotificationEventProcessor.ConsumerScope}:{recipientUserId:N}";

    private static NotificationInboxEntry ReadEntry(NpgsqlDataReader reader) => new(
        reader.GetGuid(0),
        reader.GetGuid(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetGuid(4),
        reader.GetString(5),
        reader.GetString(6),
        reader.GetString(7),
        reader.GetString(8),
        reader.GetFieldValue<DateTimeOffset>(9),
        reader.GetFieldValue<DateTimeOffset>(10),
        reader.IsDBNull(11) ? null : reader.GetFieldValue<DateTimeOffset>(11),
        reader.GetString(12));
}
