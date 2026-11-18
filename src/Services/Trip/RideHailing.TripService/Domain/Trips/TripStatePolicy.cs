// One-active-Trip-per-Rider policy.
//
// Single domain definition of the active-state set for Rider booking: exactly
// CREATED, MATCHING, DRIVER_RESERVED, DRIVER_ACCEPTED, READY_TO_START and
// IN_PROGRESS. The terminal set (COMPLETED, CANCELLED, MATCH_FAILED) is owned
// by TripStateRules.IsTerminal and is unchanged here. Infrastructure builds the
// PostgreSQL partial-unique-index predicate from this set so the database
// defense cannot drift from the domain rule.

namespace RideHailing.TripService.Domain.Trips;

/// <summary>
/// Booking-activity classification: which Trip states count as an
/// active Trip holding the Rider's single-booking slot.
/// </summary>
public static class TripStatePolicy
{
    /// <summary>
    /// States that occupy the Rider's one-active-Trip slot.
    /// Exactly six: CREATED, MATCHING, DRIVER_RESERVED, DRIVER_ACCEPTED,
    /// READY_TO_START, IN_PROGRESS.
    /// </summary>
    public static IReadOnlySet<TripState> ActiveForRiderBooking { get; } = new HashSet<TripState>
    {
        TripState.Created,
        TripState.Matching,
        TripState.DriverReserved,
        TripState.DriverAccepted,
        TripState.ReadyToStart,
        TripState.InProgress,
    };

    /// <summary>
    /// True when <paramref name="state"/> occupies the Rider's booking slot.
    /// </summary>
    public static bool IsActiveForRiderBooking(this TripState state) =>
        ActiveForRiderBooking.Contains(state);
}
