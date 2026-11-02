using RideHailing.Messaging;
using RideHailing.Messaging.Faults;

namespace RideHailing.PaymentService.UnitTests;

public class FaultPointTests
{
    [Fact]
    public async Task ExternalHarnessObservesReachedMarkerBeforeReleasing()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"fault-signals-{Guid.NewGuid():N}");
        try
        {
            var name = FaultPointNames.PaymentAfterProviderEffectBeforeResponse;
            var fp = new FaultPoint([name], TimeSpan.FromSeconds(5), directory);
            var execution = fp.HitAsync(name);
            await fp.WaitForHitAsync(name);
            Assert.True(File.Exists(Path.Combine(directory, $"faultpoint-{name}.reached")));
            Assert.False(execution.IsCompleted);
            await File.WriteAllTextAsync(Path.Combine(directory, $"faultpoint-{name}.release"), "release");
            await execution;
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("Production", false)]
    [InlineData("Staging", false)]
    [InlineData(null, false)]
    [InlineData("Testing", true)]
    [InlineData("Development", true)]
    public void EnvironmentActivationRequiresTestProfile(string? environment, bool expected)
    {
        var previousDotnet = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        var previousAspnet = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        var previousNames = Environment.GetEnvironmentVariable("RIDE_FAULTPOINTS");
        try
        {
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", environment);
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", environment);
            Environment.SetEnvironmentVariable("RIDE_FAULTPOINTS", FaultPointNames.OutboxAfterPublishBeforeMark);
            Assert.Equal(expected, FaultPoint.FromEnvironment().IsEnabled(FaultPointNames.OutboxAfterPublishBeforeMark));
            Assert.False(FaultPoint.ForHostEnvironment("Production").IsEnabled(FaultPointNames.OutboxAfterPublishBeforeMark));
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", previousDotnet);
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", previousAspnet);
            Environment.SetEnvironmentVariable("RIDE_FAULTPOINTS", previousNames);
        }
    }

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
