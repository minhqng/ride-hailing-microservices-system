using RideHailing.PaymentService.Domain;

namespace RideHailing.PaymentService.Infrastructure;

public sealed class PaymentSimulator(ProviderLedger? provider = null)
{
    public async Task<PaymentOperation> ExecuteAsync(
        PaymentOperation operation,
        PaymentSimulatorMode mode,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        cancellationToken.ThrowIfCancellationRequested();
        if (operation.State != PaymentOperationState.Processing)
            throw new InvalidOperationException("Persist the processing attempt before invoking the simulator.");
        if (mode == PaymentSimulatorMode.SlowSuccess)
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);

        if (provider is not null && mode is PaymentSimulatorMode.Success or PaymentSimulatorMode.SlowSuccess or PaymentSimulatorMode.TimeoutAfterEffect)
            await provider.CommitEffectAsync(operation, cancellationToken);
        return operation.Resolve(mode, DateTimeOffset.UtcNow);
    }
}
