using RideHailing.PaymentService.Domain;

namespace RideHailing.PaymentService.Infrastructure;

public sealed class ProviderResponseLostException : TimeoutException;

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
        // Recovery after a crash between provider commit and Payment commit must not reinterpret
        // an existing effect as a decline or perform a new logical operation.
        if (provider is not null && await provider.FindEffectAsync(operation.OperationKey, cancellationToken) is not null)
            return operation.Resolve(PaymentSimulatorMode.Success, DateTimeOffset.UtcNow);
        if (mode == PaymentSimulatorMode.SlowSuccess)
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);

        if (provider is not null && mode is PaymentSimulatorMode.Success or PaymentSimulatorMode.SlowSuccess or PaymentSimulatorMode.TimeoutAfterEffect)
            await provider.CommitEffectAsync(operation, cancellationToken);
        // Real independent ledger commit has completed, but the response boundary supplies no outcome.
        if (provider is not null && mode is PaymentSimulatorMode.TimeoutAfterEffect or PaymentSimulatorMode.TimeoutBeforeEffect)
            throw new ProviderResponseLostException();
        return operation.Resolve(mode, DateTimeOffset.UtcNow);
    }
}
