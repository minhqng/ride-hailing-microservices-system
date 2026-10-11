// B-W05 policy validation: invalid deployment policy must fail at startup,
// never leak into per-request behavior as silent mis-ranking.
// Pure predicate (unit-tested) + ValidateOnStart wiring in Program.cs.
namespace RideHailing.MatchingService.Domain.Matching;

public static class MatchingPolicyValidator
{
    /// <summary>Returns an error description, or null when valid.</summary>
    public static string? Validate(MatchingPolicy? policy)
    {
        if (policy is null)
        {
            return "matching policy is missing.";
        }

        if (!double.IsFinite(policy.SearchRadiusKm) || policy.SearchRadiusKm <= 0)
        {
            return "SearchRadiusKm must be a finite value > 0.";
        }

        if (policy.MaxCandidates < 0)
        {
            return "MaxCandidates must be >= 0.";
        }

        if (policy.DiscoveryOversample < 1 || policy.DiscoveryHardCap < 1)
        {
            return "DiscoveryOversample/DiscoveryHardCap must be >= 1.";
        }

        if (policy.LocationFreshnessWindow <= TimeSpan.Zero)
        {
            return "LocationFreshnessWindow must be > 0.";
        }

        if (!double.IsFinite(policy.FreshnessWeightKmPerSecond)
            || policy.FreshnessWeightKmPerSecond < 0)
        {
            // Negative weight silently inverts freshness preference (older =
            // better); fail closed instead of ranking upside-down.
            return "FreshnessWeightKmPerSecond must be a finite value >= 0.";
        }

        if (string.IsNullOrWhiteSpace(policy.RankingConfigVersion))
        {
            return "RankingConfigVersion must not be empty.";
        }

        return null;
    }
}
