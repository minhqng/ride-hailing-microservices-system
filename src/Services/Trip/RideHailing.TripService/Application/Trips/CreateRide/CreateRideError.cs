// CreateRide application error vocabulary (A-W04-01).
//
// W4-local outcomes, separate from the Trip lifecycle TripErrorCode: the
// aggregate reports rejected transitions, while the handler reports request
// validity, idempotency-key reuse with a different request meaning, and the
// DSI-013 single-booking conflict. No project-wide error framework.

namespace RideHailing.TripService.Application.Trips.CreateRide;

/// <summary>
/// W4 CreateRide application outcomes for non-success results.
/// </summary>
public enum CreateRideError
{
    /// <summary>No error; the operation succeeded.</summary>
    None = 0,

    /// <summary>
    /// The request or Idempotency-Key is invalid (empty Rider identity,
    /// missing locations, missing/blank/oversized key).
    /// </summary>
    Validation = 1,

    /// <summary>
    /// The same Rider-scoped key was already used for a semantically
    /// different request (fingerprint mismatch on replay lookup).
    /// </summary>
    IdempotencyConflict = 2,

    /// <summary>
    /// The Rider already holds an active Trip (DSI-013) and the new logical
    /// request uses a fresh key.
    /// </summary>
    ActiveTripConflict = 3,
}
