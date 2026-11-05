using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Npgsql;
using RideHailing.Messaging.Contracts;
namespace RideHailing.NotificationWorker;

public interface INotificationProjectionStore
{
    Task<bool> RecordDeliveredAsync(
        string consumerScope, string messageId, string messageType, string correlationId,
        string? aggregateId, DateTimeOffset deliveredAt, CancellationToken token);
}

/// <summary>
/// Non-authoritative delivery projection. The only durable mutation is in
/// notification_db; this component has no dependency on Trip/Driver/Payment DBs.
/// </summary>
public sealed partial class NotificationProjectionStore(NpgsqlDataSource source) : INotificationProjectionStore, INotificationInboxStore
{
    private NpgsqlDataSource Source => source;

    public async Task EnsureSchemaAsync(CancellationToken token = default)
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS notification_deliveries (
                consumer_scope text NOT NULL,
                message_id text NOT NULL,
                message_type text NOT NULL,
                correlation_id text NOT NULL,
                aggregate_id text NULL,
                state text NOT NULL CHECK (state IN ('PENDING','DELIVERED','FAILED_VISIBLE')),
                attempt_count integer NOT NULL DEFAULT 1 CHECK (attempt_count > 0),
                last_error_code text NULL,
                first_seen_at timestamptz NOT NULL,
                delivered_at timestamptz NULL,
                PRIMARY KEY (consumer_scope, message_id)
            );
            CREATE INDEX IF NOT EXISTS ix_notification_deliveries_state_seen
                ON notification_deliveries(state, first_seen_at);
            """;
        await using var connection = await source.OpenConnectionAsync(token).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false);
        // Replica startup serializes on a stable advisory key, matching the
        // other services' DDL coordination; transactional DDL rolls back on
        // failure and releases the lock with the transaction.
        await using (var gate = new NpgsqlCommand("SELECT pg_advisory_xact_lock(761103)", connection, transaction))
            await gate.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        await using (var command = new NpgsqlCommand(sql, connection, transaction))
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        await using (var command = new NpgsqlCommand(InboxSchema, connection, transaction))
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        await transaction.CommitAsync(token).ConfigureAwait(false);
    }

    public async Task<bool> RecordDeliveredAsync(
        string consumerScope, string messageId, string messageType, string correlationId,
        string? aggregateId, DateTimeOffset deliveredAt, CancellationToken token)
    {
        const string sql = """
            INSERT INTO notification_deliveries(
                consumer_scope,message_id,message_type,correlation_id,aggregate_id,
                state,first_seen_at,delivered_at)
            VALUES(@scope,@message,@type,@correlation,@aggregate,'DELIVERED',@at,@at)
            ON CONFLICT (consumer_scope,message_id) DO NOTHING
            """;
        await using var command = source.CreateCommand(sql);
        command.Parameters.AddWithValue("scope", consumerScope);
        command.Parameters.AddWithValue("message", messageId);
        command.Parameters.AddWithValue("type", messageType);
        command.Parameters.AddWithValue("correlation", correlationId);
        command.Parameters.AddWithValue("aggregate", (object?)aggregateId ?? DBNull.Value);
        command.Parameters.AddWithValue("at", deliveredAt);
        return await command.ExecuteNonQueryAsync(token) == 1;
    }
}

public static class NotificationEventTypes
{
    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.Ordinal)
    {
        RideHailing.Messaging.Contracts.NotificationMessage.MessageType,
        "RideRequestedV1", "MatchingFailedV1",
        "DriverAvailableV1", "DriverOfflineV1", "DriverReservedV1",
        "DriverReservationReleasedV1", "DriverTripStartedV1", "DriverTripCompletedV1",
        "DriverLocationAcceptedV1", "PaymentAuthorizedV1", "PaymentAuthorizationFailedV1",
        "PaymentAuthorizationVoidedV1", "PaymentCapturedV1", "PaymentCapturePendingV1",
        "PaymentRequiresReviewV1",
    };
}

public static class NotificationMetrics
{
    public const string MeterName = "RideHailing.Notification";
    public static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> Deliveries = Meter.CreateCounter<long>("notification.delivery", "events");

    public static void Record(bool duplicate)
    {
        var tags = new TagList { { "outcome", duplicate ? "duplicate" : "delivered" } };
        Deliveries.Add(1, tags);
    }
}

public sealed class NotificationEventProcessor(
    INotificationProjectionStore store,
    TimeProvider time,
    ILogger<NotificationEventProcessor>? logger = null,
    INotificationInboxStore? inbox = null)
{
    public const string ConsumerScope = "notification.business-events.v1";

    public async Task<bool> ProcessAsync(MessageEnvelope<JsonElement> envelope, CancellationToken token)
    {
        if (!NotificationEventTypes.Supported.Contains(envelope.MessageType))
            throw new ArgumentException("Unsupported notification event type.");

        if (envelope.MessageType == RideHailing.Messaging.Contracts.NotificationMessage.MessageType)
            return await ProcessInboxAsync(envelope, token);

        bool applied;
        try
        {
            applied = await store.RecordDeliveredAsync(
                ConsumerScope, envelope.MessageId, envelope.MessageType, envelope.CorrelationId,
                envelope.AggregateId, time.GetUtcNow(), token);
        }
        catch (NpgsqlException ex) when (ex.IsTransient)
        {
            throw new RideHailing.Messaging.Reliability.DeliveryFailureException(
                RideHailing.Messaging.Reliability.FailureKind.Transient,
                "NOTIFICATION_DATABASE_UNAVAILABLE");
        }
        catch (TimeoutException)
        {
            throw new RideHailing.Messaging.Reliability.DeliveryFailureException(
                RideHailing.Messaging.Reliability.FailureKind.Transient,
                "NOTIFICATION_DATABASE_TIMEOUT");
        }

        NotificationMetrics.Record(duplicate: !applied);
        logger?.LogInformation("Notification projection handled {MessageType}; applied={Applied}.",
            envelope.MessageType, applied);
        return applied;
    }

    private async Task<bool> ProcessInboxAsync(MessageEnvelope<JsonElement> envelope, CancellationToken token)
    {
        if (inbox is null)
            throw new InvalidOperationException("The notification inbox store is not configured.");

        RideHailing.Messaging.Contracts.NotificationRequestedV1 intent;
        try
        {
            intent = envelope.Payload.Deserialize<RideHailing.Messaging.Contracts.NotificationRequestedV1>()
                ?? throw new ArgumentException("Notification payload is missing.");
            intent.Validate();
        }
        catch (Exception error) when (error is JsonException or ArgumentException)
        {
            throw new RideHailing.Messaging.Reliability.DeliveryFailureException(
                RideHailing.Messaging.Reliability.FailureKind.Poison, "INVALID_NOTIFICATION_EVENT");
        }

        var createdAt = time.GetUtcNow();
        var entry = new NotificationInboxEntry(
            NotificationContent.DeriveNotificationId(envelope.MessageId, intent.RecipientUserId, intent.NotificationType),
            intent.RecipientUserId,
            intent.RecipientRole,
            intent.NotificationType,
            intent.TripId,
            envelope.MessageId,
            envelope.MessageType,
            envelope.CorrelationId,
            NotificationContent.For(intent.NotificationType),
            createdAt,
            createdAt,
            null,
            "INBOX_AVAILABLE");

        NotificationWriteResult result;
        try
        {
            result = await inbox.RecordNotificationAsync(
                entry, NotificationContent.Fingerprint(envelope), token);
        }
        catch (NotificationContentConflictException)
        {
            throw new RideHailing.Messaging.Reliability.DeliveryFailureException(
                RideHailing.Messaging.Reliability.FailureKind.Poison, "NOTIFICATION_CONTENT_CONFLICT");
        }
        catch (NpgsqlException ex) when (ex.IsTransient)
        {
            throw new RideHailing.Messaging.Reliability.DeliveryFailureException(
                RideHailing.Messaging.Reliability.FailureKind.Transient,
                "NOTIFICATION_DATABASE_UNAVAILABLE");
        }
        catch (TimeoutException)
        {
            throw new RideHailing.Messaging.Reliability.DeliveryFailureException(
                RideHailing.Messaging.Reliability.FailureKind.Transient,
                "NOTIFICATION_DATABASE_TIMEOUT");
        }

        var applied = result == NotificationWriteResult.Applied;
        NotificationMetrics.Record(duplicate: !applied);
        logger?.LogInformation(
            "Recipient notification {MessageId} type {Type} recipient {Recipient}; applied={Applied}.",
            envelope.MessageId, intent.NotificationType, intent.RecipientUserId, applied);
        return applied;
    }
}
