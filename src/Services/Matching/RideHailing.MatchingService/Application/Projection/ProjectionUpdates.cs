// B-W04-01 (revised): two independent update intents, one shared FCALL.
// A Driver state event carries NO location fields, so it can never smuggle
// a stale location into the projection (review correction #2).
namespace RideHailing.MatchingService.Application.Projection;

/// <summary>Authoritative Driver state for the projection (status + aggregate version).</summary>
public sealed record DriverStateProjectionUpdate(Guid DriverId, string DerivedStatus, long AggregateVersion);

/// <summary>Accepted location sample for the projection (freshness/session guarded).</summary>
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

/// <summary>Outcome of one atomic FCALL: per-section results + resulting GEO membership.</summary>
public sealed record ProjectionResult(
    SectionOutcome State,
    SectionOutcome Location,
    bool GeoMember,
    bool Degraded = false)
{
    public static ProjectionResult DegradedResult() =>
        new(SectionOutcome.Degraded, SectionOutcome.Degraded, false, true);
}
