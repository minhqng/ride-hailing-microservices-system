using RideHailing.Messaging;
using RideHailing.Messaging.Faults;

namespace RideHailing.PaymentService.UnitTests;

public class FaultPointTests
{
    [Fact]
    public async Task DisabledFaultPointIsNoOp()
    {
        var fp = new FaultPoint(Array.Empty<string>());
        await fp.HitAsync(FaultPointNames.PaymentAfterProviderEffectBeforeResponse);
        Assert.False(fp.IsEnabled(FaultPointNames.PaymentAfterProviderEffectBeforeResponse));
    }

    [Fact]
    public async Task EnabledFaultPointBlocksUntilReleased()
    {
        var fp = new FaultPoint(new[] { FaultPointNames.PaymentAfterProviderEffectBeforeResponse });
        var hitTask = fp.HitAsync(FaultPointNames.PaymentAfterProviderEffectBeforeResponse);
        await fp.WaitForHitAsync(FaultPointNames.PaymentAfterProviderEffectBeforeResponse);
        Assert.False(hitTask.IsCompleted);
        fp.Release(FaultPointNames.PaymentAfterProviderEffectBeforeResponse);
        await hitTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(hitTask.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task WaitingForMissingHitIsBounded()
    {
        var fp = new FaultPoint(
            new[] { FaultPointNames.PaymentAfterProviderEffectBeforeResponse },
            defaultTimeout: TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fp.WaitForHitAsync(FaultPointNames.PaymentAfterProviderEffectBeforeResponse));
    }
}
