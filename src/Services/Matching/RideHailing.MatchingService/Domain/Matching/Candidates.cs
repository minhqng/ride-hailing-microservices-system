// B-W05-01/02/03: candidate pipeline records. Staging by TYPE enforces
// DSI-020 (eligibility before ranking): the ranker only accepts
// EligibleCandidate, so an ineligible driver can never be ranked in.
namespace RideHailing.MatchingService.Domain.Matching;

/// <summary>Raw discovery hit: Redis member + distance, nothing validated.</summary>
public sealed record DiscoveredCandidate(Guid DriverId, double DistanceKm);

/// <summary>Projection-enriched candidate (authoritative fields unknown yet).</summary>
public sealed record EnrichedCandidate(
    Guid DriverId,
    double DistanceKm,
    string DerivedStatus,
    int DriverAggregateVersion,
    double Longitude,
    double Latitude,
    DateTimeOffset CapturedAt,
    string? Capability);

/// <summary>Passed eligibility; the only legal ranker input (DSI-020).</summary>
public sealed record EligibleCandidate(
    Guid DriverId,
    double DistanceKm,
    int DriverAggregateVersion,
    DateTimeOffset CapturedAt,
    TimeSpan Age);

/// <summary>Ranked output with stable ordinal.</summary>
public sealed record RankedCandidate(
    int Ordinal,
    Guid DriverId,
    double DistanceKm,
    double Score,
    int DriverAggregateVersion);
