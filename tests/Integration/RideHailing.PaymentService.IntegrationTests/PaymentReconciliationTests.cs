using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RideHailing.Messaging.Outbox;
using RideHailing.PaymentService.Application.Payment;
using RideHailing.PaymentService.Domain;
using RideHailing.PaymentService.Infrastructure;
using RideHailing.PaymentService.Integration.Payment;

namespace RideHailing.PaymentService.IntegrationTests;

public class PaymentReconciliationTests(PaymentDatabase database) : IClassFixture<PaymentDatabase>
{
    private static PaymentReconciliationOptions TestOptions() => new()
    {
        Enabled = true,
        BatchSize = 25,
        PollIntervalSeconds = 1,
        ReviewAfterSeconds = 60,
    };

    private async Task<NpgsqlOutboxStore> OutboxAsync()
    {
        var outbox = new NpgsqlOutboxStore(database.Source);
        await outbox.EnsureSchemaAsync();
        return outbox;
    }

    private (PaymentExecution Execution, PaymentReconciliationWorker Worker,
        ProviderLedger Provider, NpgsqlOutboxStore Outbox) Pipeline(NpgsqlOutboxStore outbox)
    {
        var provider = new ProviderLedger(database.ConnectionString);
        var execution = new PaymentExecution(database.Source, new PaymentSimulator(provider));
        var worker = new PaymentReconciliationWorker(
            execution, provider, outbox, TimeProvider.System,
            Options.Create(TestOptions()), NullLogger<PaymentReconciliationWorker>.Instance);
        return (execution, worker, provider, outbox);
    }

    private static PaymentOperation Operation(
        Guid tripId, PaymentOperationType type, string? key = null) =>
        PaymentOperation.Create(
            key ?? $"{tripId:D}:{type.ToString().ToLowerInvariant()}:v1",
            tripId, type, type == PaymentOperationType.Void ? 1 : 25_000, "VND", DateTimeOffset.UtcNow);

    private async Task ExecuteAsync(
        PaymentExecution execution, PaymentOperation operation, PaymentSimulatorMode mode) =>
        await execution.ExecuteAsync(operation, mode, CancellationToken.None);

    private async Task<PaymentOperation> ReadAsync(Guid id) =>
        await database.Repository.GetAsync(id, CancellationToken.None)
        ?? throw new InvalidOperationException("Payment operation not found.");

    [Fact]
    public async Task UnknownAuthorizeWithProviderEffect_ResolvesAndPublishesAuthorized()
    {
        var outbox = await OutboxAsync();
        var (execution, worker, provider, _) = Pipeline(outbox);
        var tripId = Guid.NewGuid();
        var operation = Operation(tripId, PaymentOperationType.Authorize);
        await ExecuteAsync(execution, operation, PaymentSimulatorMode.TimeoutAfterEffect);

        Assert.Equal(PaymentOperationState.Unknown, (await ReadAsync(operation.Id)).State);
        await worker.ReconcileBatchAsync(CancellationToken.None);

        var resolved = await ReadAsync(operation.Id);
        Assert.Equal(PaymentOperationState.Succeeded, resolved.State);
        Assert.True(resolved.ProviderEffectOccurred);
        var intent = await outbox.LoadAsync($"payment:{operation.Id:N}:PaymentAuthorizedV1");
        Assert.NotNull(intent);
        Assert.Equal("payment.authorized.v1", intent!.RoutingKey);
        Assert.Equal(operation.OperationKey, intent.OperationKey);

        // The provider ledger still holds one effect; reconciliation never
        // started a second financial operation.
        await using var count = database.Source.CreateCommand(
            "SELECT count(*) FROM provider_effects WHERE operation_key=@key");
        count.Parameters.AddWithValue("key", operation.OperationKey);
        Assert.Equal(1L, (long)(await count.ExecuteScalarAsync())!);
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task UnknownCaptureWithProviderEffect_ResolvesAndPublishesCaptured()
    {
        var outbox = await OutboxAsync();
        var (execution, worker, provider, _) = Pipeline(outbox);
        var tripId = Guid.NewGuid();
        var operation = Operation(tripId, PaymentOperationType.Capture);
        await ExecuteAsync(execution, operation, PaymentSimulatorMode.TimeoutAfterEffect);

        await worker.ReconcileBatchAsync(CancellationToken.None);

        Assert.Equal(PaymentOperationState.Succeeded, (await ReadAsync(operation.Id)).State);
        var intent = await outbox.LoadAsync($"payment:{operation.Id:N}:PaymentCapturedV1");
        Assert.NotNull(intent);
        Assert.Equal("payment.captured.v1", intent!.RoutingKey);
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task ProviderCommittedBeforeLocalRollback_RecoversTheSameOperation()
    {
        var outbox = await OutboxAsync();
        var (execution, worker, provider, _) = Pipeline(outbox);
        var tripId = Guid.NewGuid();
        var operation = Operation(tripId, PaymentOperationType.Authorize);
        await ExecuteAsync(execution, operation, PaymentSimulatorMode.TimeoutAfterEffect);

        // Simulate the provider commit surviving the Payment transaction
        // rollback: the row returns to PENDING with no attempt recorded.
        await using (var reset = database.Source.CreateCommand("""
            UPDATE payment_operations
            SET state='PENDING', attempt_count=0, provider_effect_occurred=false,
                provider_effect_id=NULL, result_code=NULL, completed_at=NULL, updated_at=now()
            WHERE id=@id
            """))
        {
            reset.Parameters.AddWithValue("id", operation.Id);
            Assert.Equal(1, await reset.ExecuteNonQueryAsync());
        }

        await worker.ReconcileBatchAsync(CancellationToken.None);

        var recovered = await ReadAsync(operation.Id);
        Assert.Equal(PaymentOperationState.Succeeded, recovered.State);
        Assert.True(recovered.ProviderEffectOccurred);
        Assert.Equal(1, recovered.AttemptCount);
        var intent = await outbox.LoadAsync($"payment:{operation.Id:N}:PaymentAuthorizedV1");
        Assert.NotNull(intent);
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task UnknownWithoutProviderEffect_RemainsPendingWithoutAFalseOutcome()
    {
        var outbox = await OutboxAsync();
        var (execution, worker, provider, _) = Pipeline(outbox);
        var tripId = Guid.NewGuid();
        var operation = Operation(tripId, PaymentOperationType.Authorize);
        await ExecuteAsync(execution, operation, PaymentSimulatorMode.TimeoutBeforeEffect);

        await worker.ReconcileBatchAsync(CancellationToken.None);

        Assert.Equal(PaymentOperationState.Pending, (await ReadAsync(operation.Id)).State);
        Assert.Null(await outbox.LoadAsync($"payment:{operation.Id:N}:PaymentAuthorizedV1"));
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task StaleUnresolvedOperation_BecomesVisibleReview()
    {
        var outbox = await OutboxAsync();
        var (execution, worker, provider, _) = Pipeline(outbox);
        var tripId = Guid.NewGuid();
        var operation = Operation(tripId, PaymentOperationType.Authorize);
        await ExecuteAsync(execution, operation, PaymentSimulatorMode.TimeoutBeforeEffect);
        await using (var age = database.Source.CreateCommand("""
            UPDATE payment_operations
            SET attempt_count=3, created_at=now() - interval '20 minutes',
                updated_at=now() - interval '10 minutes'
            WHERE id=@id
            """))
        {
            age.Parameters.AddWithValue("id", operation.Id);
            await age.ExecuteNonQueryAsync();
        }

        await worker.ReconcileBatchAsync(CancellationToken.None);

        var review = await ReadAsync(operation.Id);
        Assert.Equal(PaymentOperationState.RequiresReview, review.State);
        var intent = await outbox.LoadAsync($"payment:{operation.Id:N}:PaymentRequiresReviewV1");
        Assert.NotNull(intent);
        Assert.Equal("payment.requires-review.v1", intent!.RoutingKey);
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task TwoReconciliationWorkersRace_OneOutcomeOneProviderEffect()
    {
        var outbox = await OutboxAsync();
        var (execution, _, provider, _) = Pipeline(outbox);
        var tripId = Guid.NewGuid();
        var operation = Operation(tripId, PaymentOperationType.Authorize);
        await ExecuteAsync(execution, operation, PaymentSimulatorMode.TimeoutAfterEffect);

        var workerA = new PaymentReconciliationWorker(execution, provider, outbox,
            TimeProvider.System, Options.Create(TestOptions()), NullLogger<PaymentReconciliationWorker>.Instance);
        var workerB = new PaymentReconciliationWorker(execution, provider, outbox,
            TimeProvider.System, Options.Create(TestOptions()), NullLogger<PaymentReconciliationWorker>.Instance);
        await Task.WhenAll(
            workerA.ReconcileBatchAsync(CancellationToken.None),
            workerB.ReconcileBatchAsync(CancellationToken.None));

        Assert.Equal(PaymentOperationState.Succeeded, (await ReadAsync(operation.Id)).State);
        await using var intents = database.Source.CreateCommand(
            "SELECT count(*) FROM outbox_messages WHERE operation_key=@key");
        intents.Parameters.AddWithValue("key", operation.OperationKey);
        Assert.Equal(1L, (long)(await intents.ExecuteScalarAsync())!);
        await using var effects = database.Source.CreateCommand(
            "SELECT count(*) FROM provider_effects WHERE operation_key=@key");
        effects.Parameters.AddWithValue("key", operation.OperationKey);
        Assert.Equal(1L, (long)(await effects.ExecuteScalarAsync())!);
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task ConflictingProviderPayload_FailsClosedWithoutOverwriting()
    {
        var outbox = await OutboxAsync();
        var (execution, worker, provider, _) = Pipeline(outbox);
        var tripId = Guid.NewGuid();
        var operation = Operation(tripId, PaymentOperationType.Authorize);
        await ExecuteAsync(execution, operation, PaymentSimulatorMode.TimeoutBeforeEffect);
        await using (var conflict = database.Source.CreateCommand("""
            INSERT INTO provider_effects(operation_key,request_fingerprint,effect_id)
            VALUES(@key,@fingerprint,'sim-conflict')
            """))
        {
            conflict.Parameters.AddWithValue("key", operation.OperationKey);
            conflict.Parameters.AddWithValue("fingerprint",
                $"AUTHORIZE:{tripId:D}:999:VND");
            await conflict.ExecuteNonQueryAsync();
        }
        await using (var unknown = database.Source.CreateCommand("""
            UPDATE payment_operations SET state='UNKNOWN', attempt_count=1, updated_at=now()
            WHERE id=@id
            """))
        {
            unknown.Parameters.AddWithValue("id", operation.Id);
            await unknown.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<PaymentConflictException>(
            () => worker.ReconcileBatchAsync(CancellationToken.None));

        Assert.Equal(PaymentOperationState.Unknown, (await ReadAsync(operation.Id)).State);
        Assert.Null(await outbox.LoadAsync($"payment:{operation.Id:N}:PaymentAuthorizedV1"));

        // Remove the deliberately corrupt fixture so later worker scans in
        // this shared test database cannot observe it.
        await using (var cleanup = database.Source.CreateCommand("""
            DELETE FROM provider_effects WHERE operation_key=@key;
            DELETE FROM payment_operations WHERE operation_key=@key;
            """))
        {
            cleanup.Parameters.AddWithValue("key", operation.OperationKey);
            await cleanup.ExecuteNonQueryAsync();
        }
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task VoidFailureOutcomeCarriesTheVoidLineage()
    {
        var provider = new ProviderLedger(database.ConnectionString);
        var execution = new PaymentExecution(database.Source, new PaymentSimulator(provider));
        var tripId = Guid.NewGuid();
        var declined = Operation(tripId, PaymentOperationType.Void);
        await ExecuteAsync(execution, declined, PaymentSimulatorMode.Decline);

        var failed = Assert.IsType<PaymentAuthorizationVoidFailedV1>(
            PaymentCommandHandlers.ToVoidResult(await ReadAsync(declined.Id)));
        Assert.Equal(declined.OperationKey, failed.OperationKey);
        Assert.False(failed.Retryable);
        var descriptor = PaymentOutcomeContract.Describe(failed);
        Assert.Equal("PaymentAuthorizationVoidFailedV1", descriptor.MessageType);
        Assert.Equal("payment.void-failed.v1", descriptor.RoutingKey);

        var unknown = Operation(tripId, PaymentOperationType.Void,
            $"{tripId:D}:void:v2");
        await ExecuteAsync(execution, unknown, PaymentSimulatorMode.TimeoutBeforeEffect);
        var retryable = Assert.IsType<PaymentAuthorizationVoidFailedV1>(
            PaymentCommandHandlers.ToVoidResult(await ReadAsync(unknown.Id)));
        Assert.True(retryable.Retryable);
        Assert.Equal(unknown.OperationKey, retryable.OperationKey);
        await provider.DisposeAsync();
    }
}
