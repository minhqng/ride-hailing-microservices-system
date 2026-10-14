// B-W05-05/06: round/attempt aggregate + policy baseline proofs.
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RideHailing.MatchingService.Domain.Matching;

namespace RideHailing.MatchingService.UnitTests;

public sealed class MatchingPolicyTests
{
    [Fact]
    public void Frozen_Baseline_Matches_Shipped_Config_Defaults()
    {
        // The frozen baseline equals the shipped appsettings.json "Matching"
        // section (verified by review against the file); this test pins the
        // code side so silent default drift fails loudly.
        var p = MatchingPolicy.FrozenW5Baseline();
        Assert.Equal(5.0, p.SearchRadiusKm);
        Assert.Equal(20, p.MaxCandidates);
        Assert.Equal(3, p.DiscoveryOversample);
        Assert.Equal(100, p.DiscoveryHardCap);
        Assert.Equal(TimeSpan.FromMinutes(5), p.LocationFreshnessWindow);
        Assert.Equal(0.0, p.FreshnessWeightKmPerSecond);
        Assert.Null(p.RequiredCapability);
        Assert.Equal("v1", p.RankingConfigVersion);
        Assert.Null(MatchingPolicyValidator.Validate(p));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(-1.0)]
    [InlineData(0.0)]
    public void Validator_Rejects_Bad_Radius(double radius)
    {
        var p = MatchingPolicy.FrozenW5Baseline();
        p.SearchRadiusKm = radius;
        Assert.NotNull(MatchingPolicyValidator.Validate(p));
    }

    [Theory]
    [InlineData(-1)]
    public void Validator_Rejects_Negative_MaxCandidates(int max)
    {
        var p = MatchingPolicy.FrozenW5Baseline();
        p.MaxCandidates = max;
        Assert.NotNull(MatchingPolicyValidator.Validate(p));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    public void Validator_Rejects_Bad_Oversample_And_HardCap(int value)
    {
        var over = MatchingPolicy.FrozenW5Baseline();
        over.DiscoveryOversample = value;
        Assert.NotNull(MatchingPolicyValidator.Validate(over));

        var cap = MatchingPolicy.FrozenW5Baseline();
        cap.DiscoveryHardCap = value;
        Assert.NotNull(MatchingPolicyValidator.Validate(cap));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Validator_Rejects_NonPositive_Freshness_Window(double minutes)
    {
        var p = MatchingPolicy.FrozenW5Baseline();
        p.LocationFreshnessWindow = TimeSpan.FromMinutes(minutes);
        Assert.NotNull(MatchingPolicyValidator.Validate(p));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-0.5)]
    public void Validator_Rejects_Bad_Freshness_Weight(double weight)
    {
        var p = MatchingPolicy.FrozenW5Baseline();
        p.FreshnessWeightKmPerSecond = weight;
        Assert.NotNull(MatchingPolicyValidator.Validate(p));
    }

    [Fact]
    public void Validator_Rejects_Empty_Config_Version()
    {
        var p = MatchingPolicy.FrozenW5Baseline();
        p.RankingConfigVersion = " ";
        Assert.NotNull(MatchingPolicyValidator.Validate(p));
    }

    [Fact]
    public void Validator_Rejects_Null_Policy()
    {
        Assert.NotNull(MatchingPolicyValidator.Validate(null));
    }

    [Fact]
    public void Options_Pipeline_Fails_Fast_On_Invalid_Config()
    {
        // Exercises the REAL options stack (bind + Validate), not the predicate:
        // this is what ValidateOnStart enforces at service boot.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Matching:SearchRadiusKm"] = "-3",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddOptions<MatchingPolicy>()
            .Bind(config.GetSection("Matching"))
            .Validate(p => MatchingPolicyValidator.Validate(p) is null, "Invalid Matching policy configuration.");
        using var provider = services.BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<MatchingPolicy>>().Value);
    }

    [Fact]
    public void Options_Pipeline_Accepts_Frozen_Baseline()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Matching:SearchRadiusKm"] = "5.0",
                ["Matching:MaxCandidates"] = "20",
                ["Matching:DiscoveryOversample"] = "3",
                ["Matching:DiscoveryHardCap"] = "100",
                ["Matching:LocationFreshnessWindow"] = "00:05:00",
                ["Matching:FreshnessWeightKmPerSecond"] = "0",
                ["Matching:RankingConfigVersion"] = "v1",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddOptions<MatchingPolicy>()
            .Bind(config.GetSection("Matching"))
            .Validate(p => MatchingPolicyValidator.Validate(p) is null, "Invalid Matching policy configuration.");
        using var provider = services.BuildServiceProvider();
        var value = provider.GetRequiredService<IOptions<MatchingPolicy>>().Value;
        Assert.Equal(5.0, value.SearchRadiusKm);
        Assert.Null(MatchingPolicyValidator.Validate(value));
    }
}

public sealed class MatchingRoundTests
{
    [Fact]
    public void Create_Starts_Processing_S0()
    {
        var round = new MatchingRound(Guid.NewGuid(), 1, "01K8EXAMPLEMESSAGE", "v1");
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
        Assert.Throws<ArgumentException>(() => new MatchingRound(Guid.NewGuid(), round, "01K8EXAMPLEMESSAGE", "v1"));
    }

    [Fact]
    public void Create_Rejects_Empty_Identities()
    {
        Assert.Throws<ArgumentException>(() => new MatchingRound(Guid.Empty, 1, "01K8EXAMPLEMESSAGE", "v1"));
        Assert.Throws<ArgumentException>(() => new MatchingRound(Guid.NewGuid(), 1, "", "v1"));
        Assert.Throws<ArgumentException>(() => new MatchingRound(Guid.NewGuid(), 1, "   ", "v1"));
    }

    [Fact]
    public void AttachSnapshot_Stores_All_Fields()
    {
        var round = new MatchingRound(Guid.NewGuid(), 2, "ride-requested-event-42", "v1");
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
