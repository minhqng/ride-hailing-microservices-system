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

    /// <summary>
    /// Progressive search stages (DSI-021). Empty keeps the frozen W5
    /// single-stage behavior (S0 only, using SearchRadiusKm/MaxCandidates).
    /// </summary>
    public List<MatchingStagePolicy> Stages { get; set; } = [];

    /// <summary>
    /// The stages a round may traverse. Legacy/unit configurations remain
    /// single-stage; production configuration binds an explicit S0..S2 list.
    /// </summary>
    public IReadOnlyList<MatchingStagePolicy> EffectiveStages() =>
        Stages.Count > 0
            ? Stages
            : [new MatchingStagePolicy { Name = "S0", SearchRadiusKm = SearchRadiusKm, MaxCandidates = MaxCandidates }];

    public MatchingStagePolicy Stage(string name) =>
        EffectiveStages().FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.Ordinal))
        ?? throw new InvalidOperationException($"Unknown matching stage '{name}'.");

    public bool IsFinalStage(string name) =>
        string.Equals(EffectiveStages()[^1].Name, name, StringComparison.Ordinal);

    public static MatchingPolicy FrozenW5Baseline() => new();
}
