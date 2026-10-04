// Trip-side assignment reference (Doc 01 §6.1, DSI-008, DSI-009).
//
// A single coherent current assignment: driver, reservation effect and the
// matching generation that produced it. A partial assignment is
// unrepresentable: all three identities are required together. This reference
// stores the Trip-side view of an authoritative Driver reservation result;
// it does not prove Driver reservation authority, which stays with the
// Driver service.

namespace RideHailing.TripService.Domain.Trips;

/// <summary>
/// Immutable coherent current-assignment reference. Equality is by value.
/// </summary>
public sealed record TripAssignment
{
    /// <summary>Currently assigned Driver.</summary>
    public Guid DriverId { get; }

    /// <summary>
    /// The logical Driver reservation effect this assignment refers to.
    /// Reused across retry/reconciliation of the same attempt.
    /// </summary>
    public Guid ReservationId { get; }

    /// <summary>
    /// The matching generation that produced this assignment. Kept as the
    /// mechanical integer representation; round lifecycle policy belongs to
    /// later matching-workflow work.
    /// </summary>
    public int MatchingRound { get; }

    /// <summary>
    /// Records one coherent current assignment. Empty driver or reservation
    /// identities are rejected so partial assignments cannot exist.
    /// </summary>
    public TripAssignment(Guid driverId, Guid reservationId, int matchingRound)
    {
        if (driverId == Guid.Empty)
        {
            throw new ArgumentException("Assignment requires a non-empty driver identity.", nameof(driverId));
        }

        if (reservationId == Guid.Empty)
        {
            throw new ArgumentException("Assignment requires a non-empty reservation identity.", nameof(reservationId));
        }

        DriverId = driverId;
        ReservationId = reservationId;
        MatchingRound = matchingRound;
    }
}
