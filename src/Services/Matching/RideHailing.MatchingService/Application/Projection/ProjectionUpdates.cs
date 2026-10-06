// B-W04-01 (revised): two independent update intents, one shared FCALL.
// A Driver state event carries NO location fields, so it can never smuggle
// a stale location into the projection (review correction #2).
namespace RideHailing.MatchingService.Application.Projection;

/// <summary>Authoritative Driver state for the projection (status + aggregate version).</summary>
public sealed record DriverStateProjectionUpdate(Guid DriverId, string DerivedStatus, long AggregateVersion);

/// <summary>
/// Accepted location sample for the projection (freshness/session guarded).
/// Wire contract: <see cref="CapturedAt"/> must be UTC ISO-8601 in "O" format
/// (fixed-width, Z-suffixed) because the atomic function compares samples
/// lexicographically; any other rendering silently breaks tie-break ordering.
/// </summary>
public sealed record LocationProjectionUpdate(
    Guid DriverId,
    double Longitude,
    double Latitude,
    string SessionId,
    long SequenceNo,
    DateTimeOffset CapturedAt);

public enum SectionOutcome
{
    Skipped,
    Applied,
    Stale,
    StaleSession,
    Degraded,
}

/// <summary>
/// Outcome of one atomic FCALL: per-section results + resulting GEO membership.
/// <see cref="Degraded"/> is DERIVED (both sections degraded): transport failure
/// or a clean function-level failure (e.g. key-type gate). Individual
/// stale/skipped outcomes with <see cref="Degraded"/>=false are normal
/// (idempotent replays, absent sections) and report per-section truth.
/// Callers must check <see cref="Degraded"/> first and never treat GeoMember
/// as evidence.
/// </summary>
public sealed record ProjectionResult(
    SectionOutcome State,
    SectionOutcome Location,
    bool GeoMember)
{
    public bool Degraded => State == SectionOutcome.Degraded
        && Location == SectionOutcome.Degraded;

    public static ProjectionResult DegradedResult() =>
        new(SectionOutcome.Degraded, SectionOutcome.Degraded, false);
}
