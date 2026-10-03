// B-W02-02: Matching → Driver seam (foundation rule 05B §11.3: Matching may depend
// on the Driver HTTP contract, never on Driver DB credentials).
// W2 scope: interface + shapes only. No reserve behavior, no retry, no UNKNOWN
// classification here — those belong to W6/W7 callers.
namespace RideHailing.MatchingService.Infrastructure.DriverClient;

public interface IDriverClient
{
    /// <summary>
    /// Calls Driver Reserve with the SAME reservationId supplied by the caller.
    /// Returns Reserved/Rejected. Transport failure (timeout/connection/5xx)
    /// throws and must be treated as UNKNOWN by the caller, never as failure.
    /// No retry inside: Doc 02 §19 forbids generic retry for side-effecting calls.
    /// </summary>
    Task<DriverReserveResult> ReserveAsync(
        DriverReserveRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reconciles one reservation attempt: GET
    /// /internal/drivers/{driverId}/reservations/{reservationId} (Doc 02 §12.6).
    /// Unknown outcome strings throw (contract drift must fail loud).
    /// </summary>
    Task<ReservationReconcileOutcome> GetReservationAsync(
        Guid driverId,
        Guid reservationId,
        CancellationToken cancellationToken = default);
}
