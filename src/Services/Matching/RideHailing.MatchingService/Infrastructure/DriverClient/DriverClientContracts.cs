// B-W02-02/B-W02-08: Driver HTTP contract shapes (PROPOSED freeze, needs A/C sign-off).
// Source: Doc 02 §12.5 (sync Reserve with same reservationId), §12.6 (reconcile
// query + 7 outcomes), §19.3/19.4 (pipeline, 409 classification).
// Transport rule (frozen): timeout/connection failure is NOT a business outcome.
// It propagates as an exception; the CALLER (W6/W7) classifies UNKNOWN and
// reconciles with the same reservationId. This client never retries.
namespace RideHailing.MatchingService.Infrastructure.DriverClient;

/// <summary>Reserve request: one guarded reservation attempt for one driver.
/// ExpectedVersion is the caller-observed Driver version (read-then-attempt
/// OCC dance); a stale fence value yields STALE_VERSION, never an overwrite.
/// </summary>
public sealed record DriverReserveRequest(Guid DriverId, Guid TripId, Guid ReservationId, int ExpectedVersion);

public enum DriverReserveOutcomeKind
{
    Reserved,
    Rejected,
}

/// <summary>
/// Authoritative Driver answer. Rejected carries the domain code
/// (e.g. DRIVER_NOT_AVAILABLE); transport problems throw instead.
/// </summary>
public sealed record DriverReserveResult(
    DriverReserveOutcomeKind Kind,
    string? Code,
    int? DriverVersion);

/// <summary>Current Driver version observed for the OCC dance.</summary>
public sealed record DriverVersionRead(int Version, string Status);

/// <summary>Reconcile outcomes, Doc 02 §12.6 (caller maps transport failure
/// to DEPENDENCY_UNAVAILABLE).</summary>
public enum ReservationReconcileOutcome
{
    OwnedByThisReservation,
    NotCommittedOrAvailable,
    ReservedByOther,
    Offline,
    OnTrip,
    NotFound,
}
