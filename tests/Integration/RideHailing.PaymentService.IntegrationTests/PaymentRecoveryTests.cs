using RideHailing.PaymentService.Domain;
using RideHailing.PaymentService.Infrastructure;

namespace RideHailing.PaymentService.IntegrationTests;

public class PaymentRecoveryTests(PaymentDatabase database) : IClassFixture<PaymentDatabase>
{
    private static PaymentOperation Request() => PaymentOperation.Create(Guid.NewGuid().ToString(), Guid.NewGuid(), PaymentOperationType.Authorize, 100, "VND", DateTimeOffset.UtcNow);

    [Fact]
    public async Task LostResponseAfterEffectIsUnknownThenReconcilesSameKeyAfterRestart()
    {
        await using var provider = new ProviderLedger(database.ConnectionString);
        var request = Request();
        var execution = new PaymentExecution(database.Source, new PaymentSimulator(provider));
        var unknown = await execution.ExecuteAsync(request, PaymentSimulatorMode.TimeoutAfterEffect, default);
        Assert.Equal(PaymentOperationState.Unknown, unknown.State);
        Assert.False(unknown.ProviderEffectOccurred); // Caller does not know provider truth.
        var effectId = await provider.FindEffectAsync(request.OperationKey, default);
        Assert.NotNull(effectId);
        var restarted = new PaymentExecution(database.Source, new PaymentSimulator(provider));
        Assert.Equal(unknown, await restarted.ExecuteAsync(request, PaymentSimulatorMode.Success, default));
        var result = await restarted.ReconcileAsync(request.OperationKey, provider, default);
        Assert.Equal(PaymentOperationState.Succeeded, result.State);
        Assert.Equal(unknown.Id, result.Id);
        Assert.Equal(effectId, result.ProviderEffectId);
        Assert.Equal(1, result.AttemptCount);
        Assert.Equal(result, await restarted.ReconcileAsync(request.OperationKey, provider, default));
    }

    [Fact]
    public async Task MissingEffectReconcilesBeforeRetryAndRetryBudgetIsFinite()
    {
        await using var provider = new ProviderLedger(database.ConnectionString);
        var request = Request();
        var execution = new PaymentExecution(database.Source, new PaymentSimulator(provider));
        var unknown = await execution.ExecuteAsync(request, PaymentSimulatorMode.TimeoutBeforeEffect, default);
        Assert.Null(await provider.FindEffectAsync(request.OperationKey, default));
        Assert.Equal(unknown, await execution.ExecuteAsync(request, PaymentSimulatorMode.Success, default, retry: true));
        Assert.Equal(PaymentOperationState.Pending, (await execution.ReconcileAsync(request.OperationKey, provider, default)).State);
        for (var attempt = 2; attempt <= 3; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1));
            Assert.Equal(attempt, (await execution.ExecuteAsync(request, PaymentSimulatorMode.TransientFailure, default, retry: true)).AttemptCount);
        }
        var review = await execution.ExecuteAsync(request, PaymentSimulatorMode.Success, default, retry: true);
        Assert.Equal(PaymentOperationState.RequiresReview, review.State);
        Assert.Equal(3, review.AttemptCount);
        Assert.Equal(review, await execution.ExecuteAsync(request, PaymentSimulatorMode.Success, default, retry: true));
        Assert.Equal(review, await database.Repository.GetAsync(review.Id, default));
        Assert.Null(await provider.FindEffectAsync(request.OperationKey, default));
    }

    [Theory]
    [InlineData(PaymentOperationType.Authorize)]
    [InlineData(PaymentOperationType.Capture)]
    [InlineData(PaymentOperationType.Void)]
    public async Task ProviderCommitSurvivesPaymentTransactionRollbackAndCannotBecomeDecline(PaymentOperationType type)
    {
        var request = PaymentOperation.Create(Guid.NewGuid().ToString(), Guid.NewGuid(), type, 100, "VND", DateTimeOffset.UtcNow);
        await database.Repository.SaveAsync(request, default); // Durable intent precedes the attempt.
        await using (var connection = await database.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await using var processing = new Npgsql.NpgsqlCommand("""
                UPDATE payment_operations SET state='PROCESSING',attempt_count=1,updated_at=clock_timestamp()
                WHERE id=@id
                """, connection, transaction);
            processing.Parameters.AddWithValue("id", request.Id);
            Assert.Equal(1, await processing.ExecuteNonQueryAsync());
            await using var provider = new ProviderLedger(database.ConnectionString);
            await provider.CommitEffectAsync(request, default);
            // Models the database boundary of process death before Payment commits its result.
            await transaction.RollbackAsync();
        }
        Assert.Equal(PaymentOperationState.Pending, (await database.Repository.GetAsync(request.Id, default))!.State);
        await using var restartedProvider = new ProviderLedger(database.ConnectionString);
        var restarted = new PaymentExecution(database.Source, new PaymentSimulator(restartedProvider));
        var result = await restarted.ExecuteAsync(request, PaymentSimulatorMode.Decline, default);
        Assert.Equal(PaymentOperationState.Succeeded, result.State);
        Assert.Equal(request.Id, result.Id);
        Assert.Equal(await restartedProvider.FindEffectAsync(request.OperationKey, default), result.ProviderEffectId);
        Assert.Equal(result, await restarted.ExecuteAsync(request, PaymentSimulatorMode.Success, default));
        await using var count = database.Source.CreateCommand("SELECT count(*) FROM provider_effects WHERE operation_key=@key");
        count.Parameters.AddWithValue("key", request.OperationKey);
        Assert.Equal(1L, (long)(await count.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task ProviderRecoveryPreservesOriginalReferenceAndRejectsConflictingMeaning()
    {
        await using var provider = new ProviderLedger(database.ConnectionString);
        var request = Request();
        var originalReference = await provider.CommitEffectAsync(request, default);
        var restoredIntent = PaymentOperation.Create(request.OperationKey, request.TripId, request.Type,
            request.AmountMinor, request.Currency, DateTimeOffset.UtcNow);
        var execution = new PaymentExecution(database.Source, new PaymentSimulator(provider));
        var result = await execution.ExecuteAsync(restoredIntent, PaymentSimulatorMode.Decline, default);
        Assert.Equal(originalReference, result.ProviderEffectId);
        Assert.Equal(PaymentOperationState.Succeeded, result.State);

        var other = Request();
        await provider.CommitEffectAsync(PaymentOperation.Create(other.OperationKey, other.TripId, other.Type,
            other.AmountMinor + 1, other.Currency, DateTimeOffset.UtcNow), default);
        await Assert.ThrowsAsync<PaymentConflictException>(() => execution.ExecuteAsync(other, PaymentSimulatorMode.Success, default));
        Assert.Equal(PaymentOperationState.Pending, (await database.Repository.GetAsync(other.Id, default))!.State);
        var processing = other.BeginAttempt(DateTimeOffset.UtcNow);
        await database.Repository.SaveAsync(processing, default);
        await database.Repository.SaveAsync(processing.ObserveUnknown(DateTimeOffset.UtcNow), default);
        await Assert.ThrowsAsync<PaymentConflictException>(() => execution.ReconcileAsync(other.OperationKey, provider, default));
        Assert.Equal(PaymentOperationState.Unknown, (await database.Repository.GetAsync(other.Id, default))!.State);
    }

}
