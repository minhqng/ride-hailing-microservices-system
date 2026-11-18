// The fault seam must be inert outside the explicit Testing
// environment, observable when enabled, and bounded when never released.
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using RideHailing.DriverService.Infrastructure.Faults;

namespace RideHailing.DriverService.UnitTests;

public sealed class DriverFaultPointTests
{
    private const string FaultName = DriverFaultPointNames.AfterReserveCommitBeforeResponse;

    [Fact]
    public void ProductionEnvironment_IgnoresEnabledVariable()
    {
        using var scope = new EnvironmentScope(("RIDE_FAULTPOINTS", FaultName));
        var point = DriverFaultPoint.FromEnvironment(new TestHostEnvironment("Production"));

        Assert.False(point.Enabled);
        Assert.False(point.IsEnabledFor(FaultName));
    }

    [Fact]
    public void TestingEnvironmentWithoutOptIn_IsDisabled()
    {
        using var scope = new EnvironmentScope(
            ("RIDE_FAULTPOINTS", null),
            ("RIDE_FAULT_CONTROL_TOKEN", "token"));
        var point = DriverFaultPoint.FromEnvironment(new TestHostEnvironment("Testing"));

        Assert.False(point.Enabled);
    }

    [Fact]
    public void TestingEnvironmentWithoutControlToken_IsDisabled()
    {
        using var scope = new EnvironmentScope(
            ("RIDE_FAULTPOINTS", FaultName),
            ("RIDE_FAULT_CONTROL_TOKEN", null));
        var point = DriverFaultPoint.FromEnvironment(new TestHostEnvironment("Testing"));

        Assert.False(point.Enabled);
        Assert.False(point.IsEnabledFor(FaultName));
    }

    [Fact]
    public async Task TestingEnvironment_ReachesHoldsAndReleases()
    {
        using var scope = new EnvironmentScope(
            ("RIDE_FAULTPOINTS", FaultName),
            ("RIDE_FAULT_CONTROL_TOKEN", "token"),
            ("RIDE_FAULT_MAX_HOLD_SECONDS", "10"));
        var point = DriverFaultPoint.FromEnvironment(new TestHostEnvironment("Testing"));
        Assert.True(point.IsEnabledFor(FaultName));

        var hit = point.HitAsync(FaultName);
        await point.WaitForReachedAsync(TimeSpan.FromSeconds(2));

        var held = point.Snapshot();
        Assert.Equal(1, held.ReachedCount);
        Assert.Equal(1, held.PendingCount);
        Assert.NotNull(held.LastReachedAtUtc);
        Assert.False(hit.IsCompleted);

        Assert.True(point.Release(FaultName));
        await hit.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, point.Snapshot().PendingCount);
    }

    [Fact]
    public async Task BoundedHold_ReleasesWithoutControl()
    {
        var point = DriverFaultPoint.ForTesting(
            [FaultName], TimeSpan.FromMilliseconds(150));

        var hit = point.HitAsync(FaultName);
        await point.WaitForReachedAsync(TimeSpan.FromSeconds(2));
        var held = point.Snapshot();
        Assert.Equal(1, held.ReachedCount);
        Assert.Equal(1, held.PendingCount);
        Assert.False(hit.IsCompleted);

        var started = TimeProvider.System.GetTimestamp();
        await hit;
        var elapsed = TimeProvider.System.GetElapsedTime(started);

        Assert.True(elapsed >= TimeSpan.FromMilliseconds(100),
            $"hold released before its bound: {elapsed}");
        Assert.True(elapsed < TimeSpan.FromSeconds(5), $"hold was not bounded: {elapsed}");
        Assert.Equal(0, point.Snapshot().PendingCount);
    }

    [Fact]
    public async Task UnknownFaultName_IsIgnored()
    {
        var point = DriverFaultPoint.ForTesting(
            ["Driver.NotAKnownFault"], TimeSpan.FromSeconds(5));

        Assert.False(point.Enabled);
        await point.HitAsync(FaultName).WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(point.Release(FaultName));
    }

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "driver-fault-tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class EnvironmentScope : IDisposable
    {
        private readonly List<(string Name, string? Value)> _previous = [];

        public EnvironmentScope(params (string Name, string? Value)[] values)
        {
            foreach (var (name, value) in values)
            {
                _previous.Add((name, Environment.GetEnvironmentVariable(name)));
                Environment.SetEnvironmentVariable(name, value);
            }
        }

        public void Dispose()
        {
            foreach (var (name, value) in _previous)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }
}
