// Policy validation: invalid deployment policy must fail at startup,
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

        if (policy.Stages is { Count: > 0 })
        {
            if (policy.Stages.Count > 8)
            {
                return "Stages must contain at most 8 entries.";
            }

            var previousRadius = 0.0;
            for (var i = 0; i < policy.Stages.Count; i++)
            {
                var stage = policy.Stages[i];
                if (!string.Equals(stage.Name, $"S{i}", StringComparison.Ordinal))
                {
                    return $"Stages[{i}].Name must be 'S{i}' (contiguous S0..Sn).";
                }

                if (!double.IsFinite(stage.SearchRadiusKm) || stage.SearchRadiusKm <= 0)
                {
                    return $"Stages[{i}].SearchRadiusKm must be a finite value > 0.";
                }

                if (i > 0 && stage.SearchRadiusKm <= previousRadius)
                {
                    return "Stage search radii must be strictly increasing (S0 < S1 < ...).";
                }

                if (stage.MaxCandidates < 0)
                {
                    return $"Stages[{i}].MaxCandidates must be >= 0.";
                }

                previousRadius = stage.SearchRadiusKm;
            }
        }

        return null;
    }
}
