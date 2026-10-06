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
        Assert.Null(await provider.FindEffectAsync(request.OperationKey, default));
    }
}
