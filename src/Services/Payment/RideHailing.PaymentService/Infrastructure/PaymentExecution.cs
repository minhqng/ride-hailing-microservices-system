using System.Globalization;
using Npgsql;
using RideHailing.PaymentService.Domain;

namespace RideHailing.PaymentService.Infrastructure;

public sealed class PaymentConflictException : Exception;
public sealed record PaymentExecutionResult(PaymentOperation Operation, bool Applied);

/// <summary>One canonical operation per exact key. PostgreSQL arbitrates across service instances.</summary>
public sealed class PaymentExecution(NpgsqlDataSource source, PaymentSimulator simulator)
{
    public static string Fingerprint(PaymentOperation operation) =>
        $"{operation.Type.ToString().ToUpperInvariant()}:{operation.TripId:D}:{operation.AmountMinor.ToString(CultureInfo.InvariantCulture)}:{operation.Currency}";

    public async Task<PaymentOperation> ExecuteAsync(PaymentOperation request, PaymentSimulatorMode mode, CancellationToken cancellationToken, bool retry = false)
        => (await ExecuteCoreAsync(request, mode, cancellationToken, retry, null, null)).Operation;

    /// <summary>
    /// Executes an integration command with caller-owned transaction hooks. The first hook inserts
    /// the processed-message marker; the second inserts the result Outbox intent. Both share the
    /// Payment transaction so a committed marker can never exist without its local effect/result.
    /// </summary>
    public Task<PaymentExecutionResult> ExecuteMessageAsync(
        PaymentOperation request,
        PaymentSimulatorMode mode,
        Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task<bool>> tryBegin,
        Func<NpgsqlConnection, NpgsqlTransaction, PaymentOperation, CancellationToken, Task> persistResult,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tryBegin);
        ArgumentNullException.ThrowIfNull(persistResult);
        return ExecuteCoreAsync(request, mode, cancellationToken, retry: false, tryBegin, persistResult);
    }

    private async Task<PaymentExecutionResult> ExecuteCoreAsync(
        PaymentOperation request,
        PaymentSimulatorMode mode,
        CancellationToken cancellationToken,
        bool retry,
        Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task<bool>>? tryBegin,
        Func<NpgsqlConnection, NpgsqlTransaction, PaymentOperation, CancellationToken, Task>? persistResult)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        // Durable intent exists before provider invocation. Replays preserve the initial identity.
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO payment_operations(id,operation_key,trip_id,operation_type,amount_minor,currency,state,created_at,updated_at)
            VALUES(@id,@key,@trip,@type,@amount,@currency,'PENDING',@now,@now)
            ON CONFLICT DO NOTHING
            """, connection))
        {
            insert.Parameters.AddWithValue("id", request.Id);
            insert.Parameters.AddWithValue("key", request.OperationKey);
            insert.Parameters.AddWithValue("trip", request.TripId);
            insert.Parameters.AddWithValue("type", request.Type.ToString().ToUpperInvariant());
            insert.Parameters.AddWithValue("amount", request.AmountMinor);
            insert.Parameters.AddWithValue("currency", request.Currency);
            insert.Parameters.AddWithValue("now", request.CreatedAt);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var stored = await LoadAsync(request.OperationKey, connection, transaction, cancellationToken);
        if (Fingerprint(stored) != Fingerprint(request)) throw new PaymentConflictException();
        if (tryBegin is not null && !await tryBegin(connection, transaction, cancellationToken))
        {
            await transaction.CommitAsync(cancellationToken);
            return new PaymentExecutionResult(stored, Applied: false);
        }
        PaymentOperation result;
        if (stored.AttemptCount > 0 && (!retry || stored.State != PaymentOperationState.Pending))
        {
            result = stored;
        }
        else if (stored.AttemptCount >= 3)
        {
            result = stored.RequireReview(DateTimeOffset.UtcNow);
            await PaymentOperationRepository.SaveAsync(result, connection, transaction, cancellationToken);
            result = await LoadAsync(request.OperationKey, connection, transaction, cancellationToken);
        }
        else if (retry && DateTimeOffset.UtcNow < stored.UpdatedAt + RideHailing.Messaging.Reliability.FailurePolicy.Delay(stored.AttemptCount))
        {
            result = stored;
        }
        else
        {
            // A bounded owner execution continues even if its caller disconnects after acquiring the row.
            using var completion = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var processing = stored.BeginAttempt(DateTimeOffset.UtcNow);
            await PaymentOperationRepository.SaveAsync(processing, connection, transaction, completion.Token);
            try { result = await simulator.ExecuteAsync(processing, mode, completion.Token); }
            catch (ProviderResponseLostException) { result = processing.ObserveUnknown(DateTimeOffset.UtcNow); }
            await PaymentOperationRepository.SaveAsync(result, connection, transaction, completion.Token);
            result = await LoadAsync(request.OperationKey, connection, transaction, completion.Token);
        }

        if (persistResult is not null)
            await persistResult(connection, transaction, result, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new PaymentExecutionResult(result, Applied: true);
    }

    public async Task<PaymentOperation> ReconcileAsync(string key, ProviderLedger provider, CancellationToken token)
    {
        await using var connection = await source.OpenConnectionAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        var stored = await LoadAsync(key, connection, transaction, token);
        if (stored.State is PaymentOperationState.Unknown or PaymentOperationState.Processing)
        {
            stored = stored.Reconcile(await provider.FindEffectAsync(stored, token), DateTimeOffset.UtcNow);
            if (stored.State == PaymentOperationState.Pending && stored.AttemptCount >= 3)
                stored = stored.RequireReview(DateTimeOffset.UtcNow);
            await PaymentOperationRepository.SaveAsync(stored, connection, transaction, token);
        }
        stored = await LoadAsync(key, connection, transaction, token);
        await transaction.CommitAsync(token);
        return stored;
    }

    internal static async Task<PaymentOperation> LoadAsync(string key, NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("""
            SELECT id,operation_key,trip_id,operation_type,amount_minor,currency,state,
                   provider_effect_occurred,provider_effect_id,result_code,attempt_count,created_at,updated_at,completed_at
            FROM payment_operations WHERE operation_key COLLATE "C" = @key COLLATE "C" FOR UPDATE
            """, connection, transaction);
        command.Parameters.AddWithValue("key", key);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) throw new InvalidOperationException("Operation intent missing");
        return PaymentOperationRepository.Read(reader);
    }
}
