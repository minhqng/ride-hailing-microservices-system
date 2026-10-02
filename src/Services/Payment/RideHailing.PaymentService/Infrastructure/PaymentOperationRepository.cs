using Npgsql;
using RideHailing.PaymentService.Domain;

namespace RideHailing.PaymentService.Infrastructure;

public sealed class PaymentOperationRepository(NpgsqlDataSource dataSource)
{
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var stream = typeof(PaymentOperationRepository).Assembly
            .GetManifestResourceStream("RideHailing.PaymentService.Migrations.001_create_payment_operations.sql")
            ?? throw new InvalidOperationException("Payment operations migration resource is missing.");
        using var reader = new StreamReader(stream);
        var sql = await reader.ReadToEndAsync(cancellationToken);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SaveAsync(PaymentOperation operation, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO payment_operations (
                id, operation_key, trip_id, operation_type, amount_minor, currency,
                state, provider_effect_occurred, provider_effect_id, result_code,
                attempt_count, created_at, updated_at, completed_at)
            VALUES (
                @id, @operation_key, @trip_id, @operation_type, @amount_minor, @currency,
                @state, @provider_effect_occurred, @provider_effect_id, @result_code,
                @attempt_count, @created_at, @updated_at, @completed_at)
            ON CONFLICT (id) DO UPDATE SET
                state = EXCLUDED.state,
                provider_effect_occurred = EXCLUDED.provider_effect_occurred,
                provider_effect_id = EXCLUDED.provider_effect_id,
                result_code = EXCLUDED.result_code,
                attempt_count = EXCLUDED.attempt_count,
                updated_at = EXCLUDED.updated_at,
                completed_at = EXCLUDED.completed_at;
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", operation.Id);
        command.Parameters.AddWithValue("operation_key", operation.OperationKey);
        command.Parameters.AddWithValue("trip_id", operation.TripId);
        command.Parameters.AddWithValue("operation_type", operation.Type.ToString().ToUpperInvariant());
        command.Parameters.AddWithValue("amount_minor", operation.AmountMinor);
        command.Parameters.AddWithValue("currency", operation.Currency);
        command.Parameters.AddWithValue("state", operation.State switch
        {
            PaymentOperationState.RequiresReview => "REQUIRES_REVIEW",
            _ => operation.State.ToString().ToUpperInvariant()
        });
        command.Parameters.AddWithValue("provider_effect_occurred", operation.ProviderEffectOccurred);
        command.Parameters.AddWithValue("provider_effect_id", (object?)operation.ProviderEffectId ?? DBNull.Value);
        command.Parameters.AddWithValue("result_code", (object?)operation.ResultCode ?? DBNull.Value);
        command.Parameters.AddWithValue("attempt_count", operation.AttemptCount);
        command.Parameters.AddWithValue("created_at", operation.CreatedAt);
        command.Parameters.AddWithValue("updated_at", operation.UpdatedAt);
        command.Parameters.AddWithValue("completed_at", NpgsqlTypes.NpgsqlDbType.TimestampTz,
            (object?)operation.CompletedAt ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<PaymentOperation?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id, operation_key, trip_id, operation_type, amount_minor, currency,
                   state, provider_effect_occurred, provider_effect_id, result_code,
                   attempt_count, created_at, updated_at, completed_at
            FROM payment_operations WHERE id = @id;
            """;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;

        return PaymentOperation.Restore(
            reader.GetGuid(0), reader.GetString(1), reader.GetGuid(2),
            Enum.Parse<PaymentOperationType>(reader.GetString(3), ignoreCase: true),
            reader.GetInt64(4), reader.GetString(5).TrimEnd(),
            Enum.Parse<PaymentOperationState>(reader.GetString(6).Replace("_", ""), ignoreCase: true),
            reader.GetBoolean(7), reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9), reader.GetInt32(10),
            reader.GetFieldValue<DateTimeOffset>(11), reader.GetFieldValue<DateTimeOffset>(12),
            reader.IsDBNull(13) ? null : reader.GetFieldValue<DateTimeOffset>(13));
    }
}
