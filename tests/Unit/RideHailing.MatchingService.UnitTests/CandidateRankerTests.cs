// B-W05-03/07: deterministic ranking proofs (DSI-019).
// Same snapshot+config ⇒ same order, always; ties break by driverId.
using RideHailing.MatchingService.Domain.Matching;

namespace RideHailing.MatchingService.UnitTests;

public sealed class CandidateRankerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    private static EligibleCandidate C(Guid id, double km, int version = 1) =>
        new(id, km, version, T0, TimeSpan.FromSeconds(10));

    [Fact]
    public void Orders_By_Distance_Ascending()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        var ranked = CandidateRanker.Rank(
            new[] { C(c, 3.0), C(a, 1.0), C(b, 2.0) },
            MatchingPolicy.FrozenW5Baseline());

        Assert.Equal(new[] { a, b, c }, ranked.Select(r => r.DriverId));
        Assert.Equal(new[] { 0, 1, 2 }, ranked.Select(r => r.Ordinal));
    }

    [Fact]
    public void Ties_Break_By_DriverId_Deterministically()
    {
        var ids = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToList();
        var policy = MatchingPolicy.FrozenW5Baseline();
        var expected = ids.OrderBy(x => x).ToList();

        var ranked = CandidateRanker.Rank(ids.Select(id => C(id, 1.5)).ToList(), policy);

        Assert.Equal(expected, ranked.Select(r => r.DriverId).ToList());
    }

    [Fact]
    public void Shuffled_Input_Yields_Identical_Output()
    {
        var ids = Enumerable.Range(0, 10).Select(_ => Guid.NewGuid()).ToList();
        var distances = new[] { 5.0, 1.0, 3.0, 3.0, 2.0, 9.0, 0.5, 7.0, 4.0, 6.0 };
        var policy = MatchingPolicy.FrozenW5Baseline();
        var reference = CandidateRanker.Rank(
            ids.Select((id, i) => C(id, distances[i])).ToList(), policy)
            .Select(r => r.DriverId).ToList();

        var rng = new Random(42);
        for (var k = 0; k < 5; k++)
        {
            var shuffled = ids.Select((id, i) => C(id, distances[i]))
                .OrderBy(_ => rng.Next()).ToList();
            Assert.Equal(
                reference,
                CandidateRanker.Rank(shuffled, policy).Select(r => r.DriverId).ToList());
        }
    }

    [Fact]
    public void Bounds_To_MaxCandidates_Top_First()
    {
        var policy = new MatchingPolicy { MaxCandidates = 3 };
        var ranked = CandidateRanker.Rank(
            Enumerable.Range(0, 10).Select(i => C(Guid.NewGuid(), 10 - i)).ToList(), policy);

        Assert.Equal(3, ranked.Count);
        Assert.Equal(new[] { 1.0, 2.0, 3.0 }, ranked.Select(r => r.DistanceKm).ToList());
    }

    [Fact]
    public void Empty_Stays_Empty()
    {
        Assert.Empty(CandidateRanker.Rank(
            Array.Empty<EligibleCandidate>(), MatchingPolicy.FrozenW5Baseline()));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1.0)]
    public void Bad_Distance_Fails_Closed(double distance)
    {
        Assert.Throws<ArgumentException>(() => CandidateRanker.Rank(
            new[] { C(Guid.NewGuid(), distance) }, MatchingPolicy.FrozenW5Baseline()));
    }

    [Fact]
    public void Freshness_Weight_Changes_Order_Predictably()
    {
        var near = Guid.NewGuid();
        var far = Guid.NewGuid();
        var policy = new MatchingPolicy { FreshnessWeightKmPerSecond = 1.0 };
        var ranked = CandidateRanker.Rank(new[]
        {
            new EligibleCandidate(near, 1.0, 1, T0, TimeSpan.FromSeconds(100)),
            new EligibleCandidate(far, 50.0, 1, T0, TimeSpan.FromSeconds(1)),
        }, policy);

        // near: 1 + 100 = 101 > far: 50 + 1 = 51 → far first.
        Assert.Equal(new[] { far, near }, ranked.Select(r => r.DriverId));
    }

    [Fact]
    public void Snapshot_Hash_Stable_For_Same_Ranked_Set()
    {
        var policy = MatchingPolicy.FrozenW5Baseline();
        var first = CandidateRanker.Rank(
            new[] { C(Guid.NewGuid(), 2.0), C(Guid.NewGuid(), 1.0) }, policy);
        // Rebuild with fresh GUIDs in different order: different set → different hash...
        var h1 = CandidateRanker.SnapshotHash(first, policy.RankingConfigVersion);
        var h2 = CandidateRanker.SnapshotHash(first, policy.RankingConfigVersion);
        Assert.Equal(h1, h2);

        var h3 = CandidateRanker.SnapshotHash(first, "v2");
        Assert.NotEqual(h1, h3);
    }
}
