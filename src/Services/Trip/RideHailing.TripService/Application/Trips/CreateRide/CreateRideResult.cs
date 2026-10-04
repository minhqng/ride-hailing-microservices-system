// CreateRide application result (A-W04-01).
//
// Minimal: new success vs idempotent-replay success vs the three W4 failure
// outcomes. Carries the canonical Trip identity and whether the caller is
// seeing a replay; no FareSnapshot (tests read authoritative database state),
// no HTTP status codes (transport mapping belongs to a later W4 slice).

namespace RideHailing.TripService.Application.Trips.CreateRide;

/// <summary>
/// Outcome of one CreateRide application call. Success carries the canonical
/// Trip identity; failure carries a stable <see cref="CreateRideError"/> plus
/// a diagnostic message.
/// </summary>
public sealed record CreateRideResult(
    bool IsSuccess,
    Guid TripId,
    bool IsReplay,
    CreateRideError Error,
    string ErrorMessage)
{
    /// <summary>Successful creation or replay of <paramref name="tripId"/>.</summary>
    public static CreateRideResult Success(Guid tripId, bool isReplay) =>
        new(true, tripId, isReplay, CreateRideError.None, string.Empty);

    /// <summary>Rejected request. <paramref name="error"/> must not be <see cref="CreateRideError.None"/>.</summary>
    public static CreateRideResult Fail(CreateRideError error, string errorMessage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorMessage);
        if (error == CreateRideError.None)
        {
            throw new ArgumentException("Failure result requires a non-None error.", nameof(error));
        }

        return new(false, Guid.Empty, false, error, errorMessage);
    }
}
