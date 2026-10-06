// B-W05-06: MatchingAttempt ledger base (Doc 01 §8.3, Doc 02 §12.3).
// W5 scope: READY rows with stable reservationIds (DSI-018 groundwork) +
// REJECTED marking vocabulary (read by eligibility for DSI-023).
// IN_FLIGHT/UNKNOWN/RESERVED transitions belong to the reserve flow (W6/W7)
// and do not exist here by design.
namespace RideHailing.MatchingService.Domain.Matching;

public enum MatchingAttemptState
{
    Ready = 0,
    InFlight = 1,
    Unknown = 2,
    Rejected = 3,
    Reserved = 4,
}

public sealed class MatchingAttempt
{
    public Guid TripId { get; }
    public int Round { get; }
    public int Ordinal { get; }
    public Guid DriverId { get; }
    public Guid ReservationId { get; }
    public MatchingAttemptState State { get; private set; }

    public MatchingAttempt(Guid tripId, int round, int ordinal, Guid driverId, Guid reservationId)
    {
        if (tripId == Guid.Empty)
        {
            throw new ArgumentException("tripId must not be empty.", nameof(tripId));
        }

        if (round < 1)
        {
            throw new ArgumentException("matchingRound must be >= 1.", nameof(round));
        }

        if (ordinal < 0)
        {
            throw new ArgumentException("ordinal must be >= 0.", nameof(ordinal));
        }

        if (driverId == Guid.Empty)
        {
            throw new ArgumentException("driverId must not be empty.", nameof(driverId));
        }

        if (reservationId == Guid.Empty)
        {
            throw new ArgumentException("reservationId must not be empty.", nameof(reservationId));
        }

        TripId = tripId;
        Round = round;
        Ordinal = ordinal;
        DriverId = driverId;
        ReservationId = reservationId;
        State = MatchingAttemptState.Ready;
    }

    /// <summary>
    /// Marks a conclusively rejected candidate (eligibility reads this for DSI-023).
    /// Only READY → REJECTED exists in W5; reserve-driven transitions are W6/W7.
    /// </summary>
    public void MarkRejected()
    {
        if (State != MatchingAttemptState.Ready)
        {
            throw new InvalidOperationException($"Cannot reject attempt in {State}.");
        }

        State = MatchingAttemptState.Rejected;
    }

    public static string ToDb(MatchingAttemptState state) => state switch
    {
        MatchingAttemptState.Ready => "READY",
        MatchingAttemptState.InFlight => "IN_FLIGHT",
        MatchingAttemptState.Unknown => "UNKNOWN",
        MatchingAttemptState.Rejected => "REJECTED",
        MatchingAttemptState.Reserved => "RESERVED",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    public static MatchingAttemptState FromDb(string state) => state switch
    {
        "READY" => MatchingAttemptState.Ready,
        "IN_FLIGHT" => MatchingAttemptState.InFlight,
        "UNKNOWN" => MatchingAttemptState.Unknown,
        "REJECTED" => MatchingAttemptState.Rejected,
        "RESERVED" => MatchingAttemptState.Reserved,
        _ => throw new ArgumentOutOfRangeException(nameof(state), $"Unknown attempt state '{state}'."),
    };
}
