// B-W05-02/03: matching policy is CONFIGURATION-owned (radii, bounds,
// freshness windows), never hard-coded business truth (Doc 01 §9.1: exact
// radius/budget values are configuration). Frozen W5 baseline = defaults.
namespace RideHailing.MatchingService.Domain.Matching;

public sealed class MatchingPolicy
{
    public const string DefaultRankingConfigVersion = "v1";

    /// <summary>S0 search radius in km.</summary>
    public double SearchRadiusKm { get; set; } = 5.0;

    /// <summary>Max candidates kept in the bounded snapshot.</summary>
    public int MaxCandidates { get; set; } = 20;

    /// <summary>How many times MaxCandidates GEOSEARCH may return for filtering.</summary>
    public int DiscoveryOversample { get; set; } = 3;

    /// <summary>Absolute cap on GEOSEARCH COUNT (bounded memory per query).</summary>
    public int DiscoveryHardCap { get; set; } = 100;

    /// <summary>Candidate location older than this is stale (ineligible).</summary>
    public TimeSpan LocationFreshnessWindow { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Freshness penalty weight: score += ageSeconds * value. 0 = pure distance.
    /// "Freshness penalty if used" (05B §14.2): the mechanism exists, default off.
    /// </summary>
    public double FreshnessWeightKmPerSecond { get; set; } = 0.0;

    /// <summary>Optional capability requirement; null = no requirement.</summary>
    public string? RequiredCapability { get; set; }

    public string RankingConfigVersion { get; set; } = DefaultRankingConfigVersion;

    public static MatchingPolicy FrozenW5Baseline() => new();
}
