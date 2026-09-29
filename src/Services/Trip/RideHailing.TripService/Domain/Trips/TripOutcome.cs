// Trip domain operation outcome: explicit success/failure without thrown
// exceptions for normal conflicts and without any external Result library.

namespace RideHailing.TripService.Domain.Trips;

/// <summary>
/// Outcome of a Trip domain operation. Success carries no payload; failure
/// carries a stable <see cref="TripErrorCode"/> plus a diagnostic message.
/// Failed operations leave the aggregate untouched.
/// </summary>
public sealed record TripOutcome(bool IsSuccess, TripErrorCode ErrorCode, string Message)
{
    /// <summary>Successful operation.</summary>
    public static TripOutcome Success() => new(true, TripErrorCode.None, string.Empty);

    /// <summary>Rejected operation. <paramref name="code"/> must not be <see cref="TripErrorCode.None"/>.</summary>
    public static TripOutcome Fail(TripErrorCode code, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        if (code == TripErrorCode.None)
        {
            throw new ArgumentException("Failure outcome requires a non-None error code.", nameof(code));
        }

        return new(false, code, message);
    }
}
