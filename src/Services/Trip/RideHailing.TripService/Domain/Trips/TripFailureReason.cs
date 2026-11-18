// Driver Reject policy: MATCH_FAILED carries a durable,
// bounded reason so a Driver rejection is never relabeled as "no Driver
// available" or as a Rider cancellation.

namespace RideHailing.TripService.Domain.Trips;

/// <summary>
/// Why a Trip reached MATCH_FAILED. Historical rows created before the reason
/// was recorded may carry no reason and are never backfilled by guesswork.
/// </summary>
public enum TripFailureReason
{
    /// <summary>Matching exhausted its bounded candidates without a reservation.</summary>
    NoDriverAvailable = 0,

    /// <summary>A reserved Driver rejected the exact current assignment.</summary>
    DriverRejected = 1,
}

/// <summary>Canonical wire names and parsing for <see cref="TripFailureReason"/>.</summary>
public static class TripFailureReasonRules
{
    public static string ToCanonicalName(this TripFailureReason reason) => reason switch
    {
        TripFailureReason.NoDriverAvailable => "NO_DRIVER_AVAILABLE",
        TripFailureReason.DriverRejected => "DRIVER_REJECTED",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown TripFailureReason value."),
    };

    public static TripFailureReason FromCanonicalName(string value) => value switch
    {
        "NO_DRIVER_AVAILABLE" => TripFailureReason.NoDriverAvailable,
        "DRIVER_REJECTED" => TripFailureReason.DriverRejected,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown persisted TripFailureReason."),
    };
}
