// B-W05-05/06: round/attempt aggregate + policy baseline proofs.
using RideHailing.MatchingService.Domain.Matching;

namespace RideHailing.MatchingService.UnitTests;

public sealed class MatchingPolicyTests
{
    [Fact]
    public void Frozen_Baseline_Matches_Documented_Values()
    {
        var p = MatchingPolicy.FrozenW5Baseline();
        Assert.Equal(5.0, p.SearchRadiusKm);
        Assert.Equal(20, p.MaxCandidates);
        Assert.Equal(3, p.DiscoveryOversample);
        Assert.Equal(100, p.DiscoveryHardCap);
        Assert.Equal(TimeSpan.FromMinutes(5), p.LocationFreshnessWindow);
        Assert.Equal(0.0, p.FreshnessWeightKmPerSecond);
        Assert.Null(p.RequiredCapability);
        Assert.Equal("v1", p.RankingConfigVersion);
    }
}

public sealed class MatchingRoundTests
{
    [Fact]
    public void Create_Starts_Processing_S0()
    {
        var round = new MatchingRound(Guid.NewGuid(), 1, Guid.NewGuid(), "v1");
        Assert.Equal(MatchingRoundStatus.Processing, round.Status);
        Assert.Equal("S0", round.CurrentStage);
        Assert.Equal(0, round.NextAttemptOrdinal);
        Assert.Null(round.CandidateSnapshotHash);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Create_Rejects_NonPositive_Round(int round)
    {
        Assert.Throws<ArgumentException>(() => new MatchingRound(Guid.NewGuid(), round, Guid.NewGuid(), "v1"));
    }

    [Fact]
    public void Create_Rejects_Empty_Identities()
    {
        Assert.Throws<ArgumentException>(() => new MatchingRound(Guid.Empty, 1, Guid.NewGuid(), "v1"));
        Assert.Throws<ArgumentException>(() => new MatchingRound(Guid.NewGuid(), 1, Guid.Empty, "v1"));
    }

    [Fact]
    public void AttachSnapshot_Stores_All_Fields()
    {
        var round = new MatchingRound(Guid.NewGuid(), 2, Guid.NewGuid(), "v1");
        round.AttachSnapshot("abc", "[1]", 1, 3);
        Assert.Equal("abc", round.CandidateSnapshotHash);
        Assert.Equal("[1]", round.CandidateSnapshotJson);
        Assert.Equal(1, round.CandidateCount);
        Assert.Equal(3, round.NextAttemptOrdinal);
    }

    [Theory]
    [InlineData("PROCESSING", MatchingRoundStatus.Processing)]
    [InlineData("RESERVED", MatchingRoundStatus.Reserved)]
    [InlineData("NO_DRIVER", MatchingRoundStatus.NoDriver)]
    [InlineData("CANCELLED", MatchingRoundStatus.Cancelled)]
    [InlineData("FAILED", MatchingRoundStatus.Failed)]
    public void Status_Mapping_RoundTrips(string db, MatchingRoundStatus status)
    {
        Assert.Equal(status, MatchingRound.FromDb(db));
        Assert.Equal(db, MatchingRound.ToDb(status));
    }

    [Theory]
    [InlineData("OPEN")]
    [InlineData("processing")]
    [InlineData("")]
    public void Status_Mapping_Rejects_Unknown(string db)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MatchingRound.FromDb(db));
    }
}

public sealed class MatchingAttemptTests
{
    [Fact]
    public void Create_Starts_Ready()
    {
        var a = new MatchingAttempt(Guid.NewGuid(), 1, 0, Guid.NewGuid(), Guid.NewGuid());
        Assert.Equal(MatchingAttemptState.Ready, a.State);
    }

    [Fact]
    public void MarkRejected_Only_From_Ready()
    {
        var a = new MatchingAttempt(Guid.NewGuid(), 1, 0, Guid.NewGuid(), Guid.NewGuid());
        a.MarkRejected();
        Assert.Equal(MatchingAttemptState.Rejected, a.State);
        Assert.Throws<InvalidOperationException>(() => a.MarkRejected());
    }

    [Fact]
    public void Create_Rejects_Empty_Identities_And_Bad_Ordinal()
    {
        Assert.Throws<ArgumentException>(() => new MatchingAttempt(Guid.Empty, 1, 0, Guid.NewGuid(), Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() => new MatchingAttempt(Guid.NewGuid(), 1, 0, Guid.Empty, Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() => new MatchingAttempt(Guid.NewGuid(), 1, 0, Guid.NewGuid(), Guid.Empty));
        Assert.Throws<ArgumentException>(() => new MatchingAttempt(Guid.NewGuid(), 1, -1, Guid.NewGuid(), Guid.NewGuid()));
    }

    [Theory]
    [InlineData("READY", MatchingAttemptState.Ready)]
    [InlineData("IN_FLIGHT", MatchingAttemptState.InFlight)]
    [InlineData("UNKNOWN", MatchingAttemptState.Unknown)]
    [InlineData("REJECTED", MatchingAttemptState.Rejected)]
    [InlineData("RESERVED", MatchingAttemptState.Reserved)]
    public void State_Mapping_RoundTrips(string db, MatchingAttemptState state)
    {
        Assert.Equal(state, MatchingAttempt.FromDb(db));
        Assert.Equal(db, MatchingAttempt.ToDb(state));
    }

    [Theory]
    [InlineData("OPEN")]
    [InlineData("ready")]
    public void State_Mapping_Rejects_Unknown(string db)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MatchingAttempt.FromDb(db));
    }
}
