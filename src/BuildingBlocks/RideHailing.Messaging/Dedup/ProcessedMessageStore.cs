using System.Data;
using System.Data.Common;
using Npgsql;
using NpgsqlTypes;
using RideHailing.Messaging.Observability;
using RideHailing.Messaging.Schema;

namespace RideHailing.Messaging.Dedup;

public enum DedupOutcome
{
    Applied,
    Duplicate,
}

public sealed record ProcessedMessage(
    string ConsumerScope,
    string MessageId,
    string MessageType,
    DateTimeOffset ProcessedAt,
    string? HandlerVersion,
    string? AggregateId);

/// <summary>
/// Durable consumer dedup. The marker insert must share the owning service's
/// local transaction with the business effect; this class never opens a
/// transaction on another service's database.
/// </summary>
public sealed class ProcessedMessageStore(NpgsqlDataSource dataSource)
{
    public Task EnsureSchemaAsync(CancellationToken token = default)
        => EmbeddedSql.ApplyAsync(dataSource, "001_processed_messages.sql", token);

    /// <summary>
    /// Inserts the dedup marker inside the caller's transaction. Returns true
    /// only for the first delivery. A false result must commit as a no-op and
    /// ACK; the original effect already committed with the marker.
    /// </summary>
    public async Task<bool> TryMarkAsync(DbConnection connection, DbTransaction transaction,
        string consumerScope, string messageId, string messageType, DateTimeOffset processedAt,
        string? handlerVersion = null, string? aggregateId = null, CancellationToken token = default)
    {
        ValidateIdentity(consumerScope, messageId, messageType);
        if (connection is not NpgsqlConnection npgsqlConnection)
            throw new ArgumentException("An open Npgsql connection is required.", nameof(connection));
        if (transaction is not NpgsqlTransaction npgsqlTransaction)
            throw new ArgumentException("An Npgsql transaction is required.", nameof(transaction));
        if (npgsqlConnection.State != ConnectionState.Open) throw new InvalidOperationException("Connection must be open.");
        if (!ReferenceEquals(npgsqlTransaction.Connection, npgsqlConnection))
            throw new InvalidOperationException("Transaction must belong to the supplied connection.");

        const string sql = """
            INSERT INTO processed_messages
                (consumer_scope, message_id, message_type, processed_at, handler_version, aggregate_id)
            VALUES
                (@scope, @message_id, @message_type, @processed_at, @handler_version, @aggregate_id)
            ON CONFLICT (consumer_scope, message_id) DO NOTHING;
            """;
        await using var command = new NpgsqlCommand(sql, npgsqlConnection, npgsqlTransaction);
        command.Parameters.AddWithValue("scope", consumerScope);
        command.Parameters.AddWithValue("message_id", messageId);
        command.Parameters.AddWithValue("message_type", messageType);
        command.Parameters.AddWithValue("processed_at", processedAt);
        command.Parameters.Add(new NpgsqlParameter("handler_version", NpgsqlDbType.Text)
        {
            Value = (object?)handlerVersion ?? DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter("aggregate_id", NpgsqlDbType.Text)
        {
            Value = (object?)aggregateId ?? DBNull.Value,
        });
        return await command.ExecuteNonQueryAsync(token) == 1;
    }

    /// <summary>
    /// Convenience for a consumer with no pre-existing transaction: opens one
    /// local transaction on the owning data source, marks, applies the effect,
    /// and commits. Marker and effect are always committed or rolled back
    /// together.
    /// </summary>
    public async Task<DedupOutcome> ExecuteOnceAsync(string consumerScope, string messageId, string messageType,
        Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task> effect,
        CancellationToken token = default, DateTimeOffset? processedAt = null,
        string? handlerVersion = null, string? aggregateId = null)
    {
        ArgumentNullException.ThrowIfNull(effect);
        ValidateIdentity(consumerScope, messageId, messageType);

        await using var connection = await dataSource.OpenConnectionAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        var first = await TryMarkAsync(connection, transaction, consumerScope, messageId, messageType,
            processedAt ?? DateTimeOffset.UtcNow, handlerVersion, aggregateId, token);

        if (!first)
        {
            await transaction.CommitAsync(token);
            MessagingMetrics.RecordDedup(duplicate: true);
            return DedupOutcome.Duplicate;
        }

        try
        {
            await effect(connection, transaction, token);
            await transaction.CommitAsync(token);
            MessagingMetrics.RecordDedup(duplicate: false);
            return DedupOutcome.Applied;
        }
        catch
        {
            try { await transaction.RollbackAsync(CancellationToken.None); }
            catch { /* the original failure remains authoritative */ }
            throw;
        }
    }

    public async Task<ProcessedMessage?> LoadAsync(string consumerScope, string messageId,
        CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(consumerScope) || string.IsNullOrWhiteSpace(messageId))
            throw new ArgumentException("consumerScope and messageId are required");
        const string sql = """
            SELECT consumer_scope, message_id, message_type, processed_at, handler_version, aggregate_id
            FROM processed_messages
            WHERE consumer_scope = @scope AND message_id = @message_id;
            """;
        await using var connection = await dataSource.OpenConnectionAsync(token);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("scope", consumerScope);
        command.Parameters.AddWithValue("message_id", messageId);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return null;
        return new ProcessedMessage(
            reader.GetString(0), reader.GetString(1), reader.GetString(2),
            reader.GetFieldValue<DateTimeOffset>(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5));
    }

    private static void ValidateIdentity(string consumerScope, string messageId, string messageType)
    {
        if (string.IsNullOrWhiteSpace(consumerScope)) throw new ArgumentException("consumerScope is required");
        if (string.IsNullOrWhiteSpace(messageId)) throw new ArgumentException("messageId is required");
        if (string.IsNullOrWhiteSpace(messageType)) throw new ArgumentException("messageType is required");
    }
}
