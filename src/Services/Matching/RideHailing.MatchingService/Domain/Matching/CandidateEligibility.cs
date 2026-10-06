// B-W05-02: eligibility before ranking (DSI-020).
// A failing candidate can never become reservable via score: the ranker only
// accepts EligibleCandidate, enforced by TYPE (not by convention).
namespace RideHailing.MatchingService.Domain.Matching;

public enum EligibilityReason
{
    Eligible,
    UnknownProjection,
    NotAvailable,
    MissingLocation,
    InvalidDistance,
    StaleLocation,
    Incapable,
    ConclusivelyRejected,
}

/// <summary>
/// Pure eligibility decision. Capability source is injected: the W4 projection
/// carries no vehicle data, so the default source reports unknown and any
/// non-null requirement safely excludes (documented W6-feed limitation).
/// </summary>
public static class CandidateEligibility
{
    public static (EligibilityReason Reason, EligibleCandidate? Candidate) Decide(
        EnrichedCandidate candidate,
        ISet<Guid> conclusivelyRejectedDriverIds,
        MatchingPolicy policy,
        DateTimeOffset now)
    {
        if (!string.Equals(candidate.DerivedStatus, "AVAILABLE", StringComparison.Ordinal))
        {
            return (EligibilityReason.NotAvailable, null);
        }

        if (candidate.DriverAggregateVersion < 0)
        {
            // Unparseable version: no authority handshake possible — unknown, not eligible.
            return (EligibilityReason.UnknownProjection, null);
        }

        if (!double.IsFinite(candidate.DistanceKm) || candidate.DistanceKm < 0)
        {
            return (EligibilityReason.InvalidDistance, null);
        }

        if (!double.IsFinite(candidate.Longitude) || !double.IsFinite(candidate.Latitude)
            || candidate.Longitude is < -180 or > 180
            || candidate.Latitude is < -90 or > 90)
        {
            return (EligibilityReason.MissingLocation, null);
        }

        var age = now - candidate.CapturedAt;
        if (age < TimeSpan.Zero || age > policy.LocationFreshnessWindow)
        {
            return (EligibilityReason.StaleLocation, null);
        }

        if (policy.RequiredCapability is not null
            && !string.Equals(candidate.Capability, policy.RequiredCapability, StringComparison.Ordinal))
        {
            return (EligibilityReason.Incapable, null);
        }

        if (conclusivelyRejectedDriverIds.Contains(candidate.DriverId))
        {
            return (EligibilityReason.ConclusivelyRejected, null);
        }

        return (EligibilityReason.Eligible, new EligibleCandidate(
            candidate.DriverId,
            candidate.DistanceKm,
            candidate.DriverAggregateVersion,
            candidate.CapturedAt,
            age));
    }
}
