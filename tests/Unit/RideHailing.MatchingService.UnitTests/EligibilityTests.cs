// B-W05-02: eligibility decision proofs — one row per reason (DSI-020:
// ineligible can never outrank into candidacy; the ranker type enforces it).
using RideHailing.MatchingService.Domain.Matching;

namespace RideHailing.MatchingService.UnitTests;

public sealed class EligibilityTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    private static EnrichedCandidate Base() => new(
        Guid.NewGuid(), 1.0, "AVAILABLE", 3, 106.6, 10.8, Now.AddMinutes(-1), null);

    private static (EligibilityReason Reason, EligibleCandidate? Candidate) Decide(
        EnrichedCandidate c, MatchingPolicy? policy = null, ISet<Guid>? rejected = null) =>
        CandidateEligibility.Decide(c, rejected ?? new HashSet<Guid>(),
            policy ?? MatchingPolicy.FrozenW5Baseline(), Now);

    [Fact]
    public void Available_Fresh_Passes_With_Age()
    {
        var (reason, candidate) = Decide(Base());
        Assert.Equal(EligibilityReason.Eligible, reason);
        Assert.NotNull(candidate);
        Assert.Equal(TimeSpan.FromMinutes(1), candidate!.Age);
    }

    [Theory]
    [InlineData("RESERVED")]
    [InlineData("OFFLINE")]
    [InlineData("ON_TRIP")]
    [InlineData("")]
    [InlineData("available")]
    public void Non_Available_Status_Rejected(string status)
    {
        var (reason, candidate) = Decide(Base() with { DerivedStatus = status });
        Assert.Equal(EligibilityReason.NotAvailable, reason);
        Assert.Null(candidate);
    }

    [Theory]
    [InlineData(double.NaN, 106.6)]
    [InlineData(10.8, double.PositiveInfinity)]
    [InlineData(95.0, 106.6)]
    [InlineData(10.8, 181.0)]
    public void Bad_Coordinates_Rejected(double lat, double lon)
    {
        var (reason, candidate) = Decide(Base() with { Latitude = lat, Longitude = lon });
        Assert.Equal(EligibilityReason.MissingLocation, reason);
        Assert.Null(candidate);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(-2.0)]
    public void Bad_Distance_Rejected(double distance)
    {
        var (reason, candidate) = Decide(Base() with { DistanceKm = distance });
        Assert.Equal(EligibilityReason.InvalidDistance, reason);
        Assert.Null(candidate);
    }

    [Fact]
    public void Stale_Location_Rejected()
    {
        var (reason, candidate) = Decide(Base() with { CapturedAt = Now.AddMinutes(-30) });
        Assert.Equal(EligibilityReason.StaleLocation, reason);
        Assert.Null(candidate);
    }

    [Fact]
    public void Future_Location_Rejected()
    {
        var (reason, candidate) = Decide(Base() with { CapturedAt = Now.AddMinutes(1) });
        Assert.Equal(EligibilityReason.StaleLocation, reason);
        Assert.Null(candidate);
    }

    [Fact]
    public void Unparseable_Timestamp_Rejected()
    {
        // Enriched with MinValue (projection unparseable path): age overflows window.
        var (reason, _) = Decide(Base() with { CapturedAt = DateTimeOffset.MinValue });
        Assert.Equal(EligibilityReason.StaleLocation, reason);
    }

    [Fact]
    public void Required_Capability_With_Unknown_Candidate_Fails_Safe()
    {
        var policy = new MatchingPolicy { RequiredCapability = "SUV" };
        var (reason, candidate) = Decide(Base(), policy);
        Assert.Equal(EligibilityReason.Incapable, reason);
        Assert.Null(candidate);
    }

    [Fact]
    public void Matching_Capability_Passes()
    {
        var policy = new MatchingPolicy { RequiredCapability = "SUV" };
        var (reason, candidate) = Decide(Base() with { Capability = "SUV" }, policy);
        Assert.Equal(EligibilityReason.Eligible, reason);
        Assert.NotNull(candidate);
    }

    [Fact]
    public void Conclusively_Rejected_Driver_Excluded()
    {
        var c = Base();
        var (reason, candidate) = Decide(c, rejected: new HashSet<Guid> { c.DriverId });
        Assert.Equal(EligibilityReason.ConclusivelyRejected, reason);
        Assert.Null(candidate);
    }

    [Fact]
    public void Unparseable_Version_Is_Unknown_Not_Eligible()
    {
        var c = Base() with { DriverAggregateVersion = -1 };
        var (reason, candidate) = Decide(c);
        Assert.Equal(EligibilityReason.UnknownProjection, reason);
        Assert.Null(candidate);
    }
}
