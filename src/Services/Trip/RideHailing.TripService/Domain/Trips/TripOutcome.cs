// Trip domain operation outcome: explicit success/failure without thrown
// exceptions for normal conflicts and without any external Result library.
//
// Outcome taxonomy (Doc 01 §15.2):
//   APPLIED            Success with IsReplay=false; exactly one committed
//                      transition (State and/or round advanced, Version + 1).
//   IDEMPOTENT_REPLAY  Success with IsReplay=true; the same legal fact was
//                      already current, so nothing mutated (Version unchanged).
//   STALE              Fail(StaleIdentity); an older/superseded (or unknown)
//                      round/reservation generation. No mutation.
//   CONFLICT           Fail(AssignmentIdentityMismatch/AssignmentAlreadyPresent);
//                      a different current business fact. No mutation.
//   ILLEGAL_STATE      Fail(IllegalTransition/TerminalState); not a legal edge
//                      from the current state. No mutation.
//   VALIDATION_ERROR   Fail(AssignmentRequired); malformed input at the
//                      operation boundary (empty identities). No mutation.
//   CORRUPTION         thrown (InvalidOperationException), never an outcome:
//                      persisted/loaded state violates structural invariants.
// Failed and replay outcomes never advance Version or timestamps.

namespace RideHailing.TripService.Domain.Trips;

/// <summary>
/// Outcome of a Trip domain operation. Success carries replay and
/// future-compensation data; failure carries a stable
/// <see cref="TripErrorCode"/> plus a diagnostic message.
/// </summary>
public sealed record TripOutcome(
    bool IsSuccess,
    TripErrorCode ErrorCode,
    string Message,
    bool IsReplay,
    TripCompensation Compensation)
{
    /// <summary>
    /// Successful operation. <paramref name="isReplay"/> marks an idempotent
    /// no-op replay of an already-current fact (no mutation performed).
    /// <paramref name="compensation"/> exposes future-work requirements.
    /// </summary>
    public static TripOutcome Success(
        bool isReplay = false,
        TripCompensation compensation = TripCompensation.None) =>
        new(true, TripErrorCode.None, string.Empty, isReplay, compensation);

    /// <summary>Rejected operation. <paramref name="code"/> must not be <see cref="TripErrorCode.None"/>.</summary>
    public static TripOutcome Fail(TripErrorCode code, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        if (code == TripErrorCode.None)
        {
            throw new ArgumentException("Failure outcome requires a non-None error code.", nameof(code));
        }

        return new(false, code, message, false, TripCompensation.None);
    }
}
