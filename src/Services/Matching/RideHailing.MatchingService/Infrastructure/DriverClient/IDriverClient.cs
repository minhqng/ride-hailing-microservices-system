// Matching → Driver seam: Matching may depend on the Driver HTTP contract,
// never on Driver DB credentials. This layer carries interface + shapes only.
// No reserve behavior, no retry, no UNKNOWN classification here — those belong
// to the reserve callers.
namespace RideHailing.MatchingService.Infrastructure.DriverClient;

public interface IDriverClient
{
    /// <summary>
    /// Calls Driver Reserve with the SAME reservationId supplied by the caller.
    /// Returns Reserved/Rejected. Transport failure (timeout/connection/5xx)
    /// throws and must be treated as UNKNOWN by the caller, never as failure.
    /// No retry inside: generic retry is forbidden for side-effecting calls.
    /// </summary>
    Task<DriverReserveResult> ReserveAsync(
        DriverReserveRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reconciles one reservation attempt: GET
    /// /internal/drivers/{driverId}/reservations/{reservationId}.
    /// Returns outcome plus the current Driver version from the same read
    /// (null only for NOT_FOUND). The version is required for fencing:
    /// AVAILABLE is conclusive only when version exceeds every dispatched
    /// fence; otherwise the caller must stay UNKNOWN.
    /// <see cref="ReservationReconcileOutcome.NotFound"/> arrives ONLY as a 200
    /// body outcome; a bare HTTP 404 (wrong server/proxy/missing route) throws
    /// because it carries no domain evidence (never advance on it).
    /// Unknown outcome strings in a 200 body throw (contract drift fails loud).
    /// </summary>
    Task<ReservationReconcileResult> GetReservationAsync(
        Guid driverId,
        Guid reservationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the current Driver version for the read-then-attempt OCC dance
    /// : the caller attempts reserve with the observed version and
    /// retries same-R on STALE_VERSION. Returns null when the driver is
    /// unknown (conclusive: no attempt is started). Transport failure throws
    /// and the caller stays UNKNOWN.
    /// </summary>
    Task<DriverVersionRead?> TryGetDriverVersionAsync(
        Guid driverId,
        CancellationToken cancellationToken = default);
}
