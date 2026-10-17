// B-W02-02/B-W02-08: Driver HTTP contract shapes (PROPOSED freeze, needs A/C sign-off).
// Source: Doc 02 §12.5 (sync Reserve with same reservationId), §12.6 (reconcile
// query + 7 outcomes), §19.3/19.4 (pipeline, 409 classification).
// Transport rule (frozen): timeout/connection failure is NOT a business outcome.
// It propagates as an exception; the CALLER (W6/W7) classifies UNKNOWN and
// reconciles with the same reservationId. This client never retries.
namespace RideHailing.MatchingService.Infrastructure.DriverClient;

/// <summary>
/// Reserve request: one guarded reservation attempt for one driver.
/// ExpectedVersion is the caller-observed Driver version (read-then-attempt
/// OCC dance); a stale fence value yields STALE_VERSION, never an overwrite.
/// MatchingRound is the durable generation the attempt belongs to; Driver
/// records it with the reservation so DriverReservedV1 can carry the SAME
/// generation across replay/restart (Doc 02 §21.3). It is required here
/// because Matching only ever reserves from a durable (tripId, round) row —
/// there is no context in which a reserve may be sent without one.
/// </summary>
public sealed record DriverReserveRequest(Guid DriverId, Guid TripId, Guid ReservationId, int ExpectedVersion, int MatchingRound);

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

/// <summary>
/// Authoritative Driver reconcile answer with fencing version.
/// Outcome is the domain truth; DriverVersion is the current Driver version
/// from the same SELECT (null only for NotFound/missing). Matching must not
/// treat NotCommittedOrAvailable as conclusive unless DriverVersion is
/// greater than every dispatched fence (see max_dispatched_version).
/// </summary>
public sealed record ReservationReconcileResult(
    ReservationReconcileOutcome Outcome,
    int? DriverVersion);
