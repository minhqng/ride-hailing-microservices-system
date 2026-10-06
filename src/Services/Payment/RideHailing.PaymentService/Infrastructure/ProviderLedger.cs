using Npgsql;
using RideHailing.PaymentService.Domain;

namespace RideHailing.PaymentService.Infrastructure;

/// <summary>Independent simulator commit boundary; never enlisted in Payment's row-lock transaction.</summary>
public sealed class ProviderLedger : IAsyncDisposable
{
    private readonly NpgsqlDataSource source;
    public ProviderLedger(string connectionString) => source = NpgsqlDataSource.Create(
        new NpgsqlConnectionStringBuilder(connectionString) { MaxPoolSize = 8, ApplicationName = "payment-provider-simulator" }.ConnectionString);

    public async Task<string> CommitEffectAsync(PaymentOperation operation, CancellationToken token)
    {
        await using var connection = await source.OpenConnectionAsync(token);
        await using var insert = new NpgsqlCommand("""
            INSERT INTO provider_effects(operation_key,request_fingerprint,effect_id)
            VALUES(@key,@fingerprint,@effect) ON CONFLICT(operation_key) DO NOTHING
            """, connection);
        insert.Parameters.AddWithValue("key", operation.OperationKey);
        insert.Parameters.AddWithValue("fingerprint", PaymentExecution.Fingerprint(operation));
        insert.Parameters.AddWithValue("effect", $"sim-{operation.Id:N}");
        await insert.ExecuteNonQueryAsync(token);
        await using var query = new NpgsqlCommand("SELECT request_fingerprint,effect_id FROM provider_effects WHERE operation_key=@key", connection);
        query.Parameters.AddWithValue("key", operation.OperationKey);
        await using var reader = await query.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) throw new InvalidOperationException("Provider effect missing after commit");
        if (reader.GetString(0) != PaymentExecution.Fingerprint(operation)) throw new PaymentConflictException();
        return reader.GetString(1);
    }

    public async Task<string?> FindEffectAsync(PaymentOperation operation, CancellationToken token)
    {
        await using var command = source.CreateCommand("SELECT request_fingerprint,effect_id FROM provider_effects WHERE operation_key=@key");
        command.Parameters.AddWithValue("key", operation.OperationKey);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return null;
        if (reader.GetString(0) != PaymentExecution.Fingerprint(operation)) throw new PaymentConflictException();
        return reader.GetString(1);
    }

    public async Task<string?> FindEffectAsync(string operationKey, CancellationToken token)
    {
        await using var command = source.CreateCommand("SELECT effect_id FROM provider_effects WHERE operation_key=@key");
        command.Parameters.AddWithValue("key", operationKey);
        return (string?)await command.ExecuteScalarAsync(token);
    }

    public ValueTask DisposeAsync() => source.DisposeAsync();
}
