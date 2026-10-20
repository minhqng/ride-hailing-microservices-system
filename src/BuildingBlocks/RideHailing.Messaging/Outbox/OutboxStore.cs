using System.Data;
using System.Data.Common;
using Npgsql;
using NpgsqlTypes;
using RideHailing.Messaging.Schema;

namespace RideHailing.Messaging.Outbox;

public sealed record OutboxStats(long PendingCount, DateTimeOffset? OldestPendingAt, long FailedVisibleCount)
{
    public double OldestPendingAgeSeconds(DateTimeOffset now) =>
        OldestPendingAt is null ? 0 : Math.Max(0, (now - OldestPendingAt.Value).TotalSeconds);
}

public interface IOutboxStore
{
    Task<IReadOnlyList<OutboxMessage>> ClaimBatchAsync(string relayId, int batchSize, TimeSpan lease,
        DateTimeOffset now, CancellationToken token = default);
    Task<bool> MarkPublishedAsync(string messageId, DateTimeOffset publishedAt, CancellationToken token = default);
    Task<bool> MarkFailedAsync(OutboxMessage message, string relayId, string errorCode, DateTimeOffset failedAt,
        TimeSpan retryDelay, int maxAttempts, CancellationToken token = default);
    Task<OutboxStats> GetStatsAsync(CancellationToken token = default);
}

/// <summary>
/// PostgreSQL Outbox store for one owning service database. Claims use a short
/// transaction and SKIP LOCKED; the relay publishes only after that transaction
/// has committed, then marks in a separate short transaction.
/// </summary>
public sealed class NpgsqlOutboxStore(NpgsqlDataSource dataSource) : IOutboxStore
{
    private const string Columns = """
        message_id, message_type, schema_version, correlation_id, causation_id,
        aggregate_type, aggregate_id, aggregate_version, saga_id, operation_key,
        payload_json, headers_json, exchange, routing_key, occurred_at,
        status, attempt_count, next_attempt_at, claimed_by, claimed_at,
        published_at, last_error_code, last_error_at
        """;

    public Task EnsureSchemaAsync(CancellationToken token = default)
        => EmbeddedSql.ApplyAsync(dataSource, "001_outbox_messages.sql", token);

    /// <summary>
    /// Inserts the intent inside the caller's business transaction. A repeat of
    /// the same stable messageId is a no-op rather than a second intent.
    /// </summary>
    public async Task<bool> EnqueueAsync(DbConnection connection, DbTransaction transaction,
        OutboxMessage message, CancellationToken token = default)
    {
        message.Validate();
        if (message.Status != OutboxStatus.Pending)
            throw new ArgumentException("Only pending Outbox intents can be enqueued.");
        if (connection is not NpgsqlConnection npgsqlConnection)
            throw new ArgumentException("An open Npgsql connection is required.", nameof(connection));
        if (transaction is not NpgsqlTransaction npgsqlTransaction)
            throw new ArgumentException("An Npgsql transaction is required.", nameof(transaction));
        if (npgsqlConnection.State != ConnectionState.Open) throw new InvalidOperationException("Connection must be open.");
        if (!ReferenceEquals(npgsqlTransaction.Connection, npgsqlConnection))
            throw new InvalidOperationException("Transaction must belong to the supplied connection.");

        const string sql = """
            INSERT INTO outbox_messages (
                message_id, message_type, schema_version, correlation_id, causation_id,
                aggregate_type, aggregate_id, aggregate_version, saga_id, operation_key,
                payload_json, headers_json, exchange, routing_key, occurred_at,
                status, attempt_count, next_attempt_at)
            VALUES (
                @message_id, @message_type, @schema_version, @correlation_id, @causation_id,
                @aggregate_type, @aggregate_id, @aggregate_version, @saga_id, @operation_key,
                @payload_json, @headers_json, @exchange, @routing_key, @occurred_at,
                @status, @attempt_count, @next_attempt_at)
            ON CONFLICT (message_id) DO NOTHING;
            """;
        await using var command = new NpgsqlCommand(sql, npgsqlConnection, npgsqlTransaction);
        command.Parameters.AddWithValue("message_id", message.MessageId);
        command.Parameters.AddWithValue("message_type", message.MessageType);
        command.Parameters.AddWithValue("schema_version", message.SchemaVersion);
        command.Parameters.AddWithValue("correlation_id", message.CorrelationId);
        AddNullableText(command, "causation_id", message.CausationId);
        AddNullableText(command, "aggregate_type", message.AggregateType);
        AddNullableText(command, "aggregate_id", message.AggregateId);
        command.Parameters.Add(new NpgsqlParameter("aggregate_version", NpgsqlDbType.Bigint)
        {
            Value = (object?)message.AggregateVersion ?? DBNull.Value,
        });
        AddNullableText(command, "saga_id", message.SagaId);
        AddNullableText(command, "operation_key", message.OperationKey);
        command.Parameters.Add(new NpgsqlParameter("payload_json", NpgsqlDbType.Jsonb) { Value = message.PayloadJson });
        command.Parameters.Add(new NpgsqlParameter("headers_json", NpgsqlDbType.Jsonb) { Value = message.HeadersJson });
        command.Parameters.AddWithValue("exchange", message.Exchange);
        command.Parameters.AddWithValue("routing_key", message.RoutingKey);
        command.Parameters.AddWithValue("occurred_at", message.OccurredAt);
        command.Parameters.AddWithValue("status", OutboxMessage.ToDatabase(message.Status));
        command.Parameters.AddWithValue("attempt_count", message.AttemptCount);
        command.Parameters.AddWithValue("next_attempt_at", message.NextAttemptAt);
        return await command.ExecuteNonQueryAsync(token) == 1;
    }

    public async Task<IReadOnlyList<OutboxMessage>> ClaimBatchAsync(string relayId, int batchSize, TimeSpan lease,
        DateTimeOffset now, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(relayId)) throw new ArgumentException("relayId is required");
        if (batchSize is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(batchSize));
        if (lease < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(lease));
        if (now == default) throw new ArgumentException("now is required");

        const string sql = """
            WITH candidate AS (
                SELECT message_id
                FROM outbox_messages
                WHERE status = 'PENDING'
                  AND next_attempt_at <= @now
                  AND (claimed_at IS NULL OR claimed_at <= @now - @lease)
                ORDER BY occurred_at, message_id
                FOR UPDATE SKIP LOCKED
                LIMIT @batch
            )
            UPDATE outbox_messages AS o
            SET claimed_by = @relay, claimed_at = @now
            FROM candidate AS c
            WHERE o.message_id = c.message_id
            RETURNING o.message_id, o.message_type, o.schema_version, o.correlation_id, o.causation_id,
                      o.aggregate_type, o.aggregate_id, o.aggregate_version, o.saga_id, o.operation_key,
                      o.payload_json, o.headers_json, o.exchange, o.routing_key, o.occurred_at,
                      o.status, o.attempt_count, o.next_attempt_at, o.claimed_by, o.claimed_at,
                      o.published_at, o.last_error_code, o.last_error_at;
            """;
        await using var connection = await dataSource.OpenConnectionAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("relay", relayId);
        command.Parameters.AddWithValue("batch", batchSize);
        command.Parameters.AddWithValue("lease", lease);
        command.Parameters.AddWithValue("now", now);
        var claimed = new List<OutboxMessage>(batchSize);
        await using (var reader = await command.ExecuteReaderAsync(token))
        {
            while (await reader.ReadAsync(token)) claimed.Add(Read(reader));
        }
        // Locks are held only for the claim; publish and mark use separate
        // short connections/transactions.
        await transaction.CommitAsync(token);
        return claimed;
    }

    public async Task<bool> MarkPublishedAsync(string messageId, DateTimeOffset publishedAt,
        CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(messageId)) throw new ArgumentException("messageId is required");
        const string sql = """
            UPDATE outbox_messages
            SET status = 'PUBLISHED',
                published_at = @published_at,
                claimed_by = NULL,
                claimed_at = NULL,
                last_error_code = NULL,
                last_error_at = NULL
            WHERE message_id = @message_id AND status <> 'PUBLISHED';
            """;
        await using var connection = await dataSource.OpenConnectionAsync(token);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("message_id", messageId);
        command.Parameters.AddWithValue("published_at", publishedAt);
        return await command.ExecuteNonQueryAsync(token) == 1;
    }

    public async Task<bool> MarkFailedAsync(OutboxMessage message, string relayId, string errorCode,
        DateTimeOffset failedAt, TimeSpan retryDelay, int maxAttempts, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(relayId)) throw new ArgumentException("relayId is required");
        if (string.IsNullOrWhiteSpace(errorCode)) throw new ArgumentException("errorCode is required");
        if (retryDelay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(retryDelay));
        if (maxAttempts is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        const string sql = """
            UPDATE outbox_messages
            SET attempt_count = attempt_count + 1,
                status = CASE WHEN attempt_count + 1 >= @max_attempts THEN 'FAILED_VISIBLE' ELSE 'PENDING' END,
                next_attempt_at = @next_attempt_at,
                claimed_by = NULL,
                claimed_at = NULL,
                last_error_code = @error_code,
                last_error_at = @failed_at
            WHERE message_id = @message_id
              AND status = 'PENDING'
              AND claimed_by = @relay;
            """;
        await using var connection = await dataSource.OpenConnectionAsync(token);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("message_id", message.MessageId);
        command.Parameters.AddWithValue("relay", relayId);
        command.Parameters.AddWithValue("error_code", errorCode[..Math.Min(errorCode.Length, 64)]);
        command.Parameters.AddWithValue("failed_at", failedAt);
        command.Parameters.AddWithValue("next_attempt_at", failedAt + retryDelay);
        command.Parameters.AddWithValue("max_attempts", maxAttempts);
        return await command.ExecuteNonQueryAsync(token) == 1;
    }

    public async Task<OutboxStats> GetStatsAsync(CancellationToken token = default)
    {
        const string sql = """
            SELECT
                count(*) FILTER (WHERE status = 'PENDING'),
                min(occurred_at) FILTER (WHERE status = 'PENDING'),
                count(*) FILTER (WHERE status = 'FAILED_VISIBLE')
            FROM outbox_messages;
            """;
        await using var connection = await dataSource.OpenConnectionAsync(token);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return new OutboxStats(0, null, 0);
        var pending = reader.GetInt64(0);
        var oldest = reader.IsDBNull(1) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(1);
        var failed = reader.GetInt64(2);
        return new OutboxStats(pending, oldest, failed);
    }

    public async Task<OutboxMessage?> LoadAsync(string messageId, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(messageId)) throw new ArgumentException("messageId is required");
        var sql = $"SELECT {Columns} FROM outbox_messages WHERE message_id = @message_id;";
        await using var connection = await dataSource.OpenConnectionAsync(token);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("message_id", messageId);
        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? Read(reader) : null;
    }

    private static OutboxMessage Read(NpgsqlDataReader reader) => new()
    {
        MessageId = reader.GetString(0),
        MessageType = reader.GetString(1),
        SchemaVersion = reader.GetInt32(2),
        CorrelationId = reader.GetString(3),
        CausationId = reader.IsDBNull(4) ? null : reader.GetString(4),
        AggregateType = reader.IsDBNull(5) ? null : reader.GetString(5),
        AggregateId = reader.IsDBNull(6) ? null : reader.GetString(6),
        AggregateVersion = reader.IsDBNull(7) ? null : reader.GetInt64(7),
        SagaId = reader.IsDBNull(8) ? null : reader.GetString(8),
        OperationKey = reader.IsDBNull(9) ? null : reader.GetString(9),
        PayloadJson = reader.GetString(10),
        HeadersJson = reader.GetString(11),
        Exchange = reader.GetString(12),
        RoutingKey = reader.GetString(13),
        OccurredAt = reader.GetFieldValue<DateTimeOffset>(14),
        Status = OutboxMessage.FromDatabase(reader.GetString(15)),
        AttemptCount = reader.GetInt32(16),
        NextAttemptAt = reader.GetFieldValue<DateTimeOffset>(17),
        ClaimedBy = reader.IsDBNull(18) ? null : reader.GetString(18),
        ClaimedAt = reader.IsDBNull(19) ? null : reader.GetFieldValue<DateTimeOffset>(19),
        PublishedAt = reader.IsDBNull(20) ? null : reader.GetFieldValue<DateTimeOffset>(20),
        LastErrorCode = reader.IsDBNull(21) ? null : reader.GetString(21),
        LastErrorAt = reader.IsDBNull(22) ? null : reader.GetFieldValue<DateTimeOffset>(22),
    };

    private static void AddNullableText(NpgsqlCommand command, string name, string? value)
        => command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Text)
        {
            Value = (object?)value ?? DBNull.Value,
        });
}
