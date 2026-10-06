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
    int DriverAggregateVersion,
    DateTimeOffset CapturedAt);

/// <summary>
/// Persisted snapshot entry: ranked order + attempt identity + the full
/// ordering determinants (score inputs), so the hash binds what the order
/// depended on (DSI-019), and W6 can resume without recomputation.
/// </summary>
public sealed record SnapshotEntry(
    int Ordinal,
    Guid DriverId,
    Guid ReservationId,
    double DistanceKm,
    double Score,
    int Version,
    DateTimeOffset CapturedAt);
