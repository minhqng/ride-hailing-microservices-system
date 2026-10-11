// Trip-side compensation requirement representation (Doc 01 §6.5, §12.4,
// DSI-033, DSL-002).
//
// A W5 domain result may establish that future workflow work is REQUIRED
// (exact Driver release, matching-round cancellation) without executing it:
// Saga, broker dispatch and Driver/Payment orchestration belong to later
// weeks. These flags are durable domain knowledge carried on the domain
// outcome; the future workflow layer reads them together with the Trip's
// current assignment/round to build exact fenced commands. No flag ever
// performs a side effect.

namespace RideHailing.TripService.Domain.Trips;

/// <summary>
/// Future-work requirement exposed by a Trip domain transition. Combinable.
/// </summary>
[Flags]
public enum TripCompensation
{
    /// <summary>No future cleanup is required by this transition.</summary>
    None = 0,

    /// <summary>
    /// The future workflow must release the exact current reservation
    /// (tripId + reservationId, DSI-033). Never a driver-only release.
    /// </summary>
    ReleaseReservation = 1,

    /// <summary>
    /// The future workflow must stop/cancel the current matching round so it
    /// produces no further assignment effects for this Trip.
    /// </summary>
    CancelMatching = 2,
}
