// Trip business-state vocabulary (Doc 01 §6.2-§6.4).
//
// The nine P0 states below are the canonical ride lifecycle. Workflow-only
// states (for example payment tracking or compensation tracking) belong to
// later Saga state, not to this business-state set.

namespace RideHailing.TripService.Domain.Trips;

/// <summary>
/// Canonical P0 Trip business state (Doc 01 §6.2).
/// </summary>
public enum TripState
{
    Created = 0,
    Matching = 1,
    DriverReserved = 2,
    DriverAccepted = 3,
    ReadyToStart = 4,
    InProgress = 5,
    Completed = 6,
    Cancelled = 7,
    MatchFailed = 8,
}

/// <summary>
/// Durable Trip-state helpers: the canonical terminal set (Doc 01 §6.4,
/// DSI-010) and canonical wire names.
/// </summary>
public static class TripStateRules
{
    /// <summary>
    /// Canonical terminal states: COMPLETED, CANCELLED, MATCH_FAILED.
    /// A terminal Trip never regresses to an active state (DSI-010).
    /// </summary>
    public static bool IsTerminal(this TripState state) =>
        state is TripState.Completed or TripState.Cancelled or TripState.MatchFailed;

    /// <summary>
    /// Canonical state name as used on the wire and in domain messages
    /// (for example "DRIVER_RESERVED").
    /// </summary>
    public static string ToCanonicalName(this TripState state) => state switch
    {
        TripState.Created => "CREATED",
        TripState.Matching => "MATCHING",
        TripState.DriverReserved => "DRIVER_RESERVED",
        TripState.DriverAccepted => "DRIVER_ACCEPTED",
        TripState.ReadyToStart => "READY_TO_START",
        TripState.InProgress => "IN_PROGRESS",
        TripState.Completed => "COMPLETED",
        TripState.Cancelled => "CANCELLED",
        TripState.MatchFailed => "MATCH_FAILED",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown TripState value."),
    };
}
