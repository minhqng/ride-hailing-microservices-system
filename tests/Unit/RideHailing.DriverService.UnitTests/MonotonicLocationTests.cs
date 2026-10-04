// B-W03-06: DSI-006 monotonic accepted location per session.
using RideHailing.DriverService.Application.Location;

namespace RideHailing.DriverService.UnitTests;

public class MonotonicLocationTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Newer_Sequence_Wins_Older_Is_Stale()
    {
        var acc = new LocationAcceptor();
        var driver = Guid.NewGuid();
        var s20 = new LocationSample(driver, "s", 20, T0, 10.0, 106.0);
        var s19 = new LocationSample(driver, "s", 19, T0.AddSeconds(1), 10.1, 106.1);

        Assert.Equal(LocationAcceptOutcome.Accepted, acc.Accept(s20, T0).Outcome);
        var stale = acc.Accept(s19, T0);
        Assert.Equal(LocationAcceptOutcome.Stale, stale.Outcome);
        Assert.Equal(20, stale.Current.SequenceNo); // seq20 remains current
    }

    [Fact]
    public void Same_Sequence_Newer_Timestamp_Wins()
    {
        var acc = new LocationAcceptor();
        var driver = Guid.NewGuid();
        var a = new LocationSample(driver, "s", 5, T0, 10.0, 106.0);
        var b = new LocationSample(driver, "s", 5, T0.AddSeconds(10), 10.1, 106.1);

        Assert.Equal(LocationAcceptOutcome.Accepted, acc.Accept(a, T0).Outcome);
        Assert.Equal(LocationAcceptOutcome.Accepted, acc.Accept(b, T0).Outcome);
        Assert.Equal(10.1, acc.GetLatest(driver, "s")!.Latitude);
    }

    [Fact]
    public void Sessions_Are_Independent()
    {
        var acc = new LocationAcceptor();
        var driver = Guid.NewGuid();
        acc.Accept(new LocationSample(driver, "s1", 100, T0, 10.0, 106.0), T0);
        var r = acc.Accept(new LocationSample(driver, "s2", 1, T0, 11.0, 107.0), T0);
        Assert.Equal(LocationAcceptOutcome.Accepted, r.Outcome);
    }

    [Fact]
    public void Exact_Replay_Of_Current_Sample_Is_Idempotent_Accept()
    {
        var acc = new LocationAcceptor();
        var driver = Guid.NewGuid();
        var first = new LocationSample(driver, "s", 7, T0, 10.0, 106.0);

        Assert.Equal(LocationAcceptOutcome.Accepted, acc.Accept(first, T0).Outcome);

        // Same logical effect replayed (at-least-once device retry) must be
        // canonical success, not a 409 conflict (DSR-002).
        var replay = acc.Accept(first with { }, T0);
        Assert.Equal(LocationAcceptOutcome.Accepted, replay.Outcome);
        Assert.Equal(first, replay.Current);
    }

    [Fact]
    public void Same_Seq_And_Timestamp_With_Different_Payload_Is_Stale()
    {
        var acc = new LocationAcceptor();
        var driver = Guid.NewGuid();
        acc.Accept(new LocationSample(driver, "s", 7, T0, 10.0, 106.0), T0);

        // Same seq/timestamp but conflicting coordinates is not a replay of
        // the same logical effect: it must not overwrite the current sample.
        var conflict = acc.Accept(new LocationSample(driver, "s", 7, T0, 11.0, 107.0), T0);
        Assert.Equal(LocationAcceptOutcome.Stale, conflict.Outcome);
        Assert.Equal(10.0, conflict.Current.Latitude);
    }
}
