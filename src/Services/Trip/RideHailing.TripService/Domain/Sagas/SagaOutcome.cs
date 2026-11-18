// RideSaga domain operation outcome: explicit success/failure without thrown
// exceptions for normal conflicts, mirroring the TripOutcome taxonomy.
// Failed and replay outcomes never advance StepVersion.

namespace RideHailing.TripService.Domain.Sagas;

public enum RideSagaErrorCode
{
    /// <summary>No error; the operation succeeded.</summary>
    None = 0,

    /// <summary>The operation is not a legal edge from the current step/status.</summary>
    IllegalTransition = 1,

    /// <summary>The saga is terminal and no transition may reactivate it.</summary>
    TerminalState = 2,

    /// <summary>The supplied operation identity does not match this step's
    /// lineage: a different operationKey cannot drive this saga.</summary>
    OperationKeyMismatch = 3,

    /// <summary>Malformed input: empty identity or blank key.</summary>
    IdentityRequired = 4,

    /// <summary>The saga status contradicts the requested transition
    /// (for example starting a ride while compensation is in progress).</summary>
    StatusConflict = 5,
}

/// <summary>
/// Outcome of one RideSaga domain operation. Success carries the replay flag;
/// failure carries a stable <see cref="RideSagaErrorCode"/> plus diagnostics.
/// </summary>
public sealed record SagaOutcome(
    bool IsSuccess,
    RideSagaErrorCode ErrorCode,
    string Message,
    bool IsReplay)
{
    public static SagaOutcome Success(bool isReplay = false) =>
        new(true, RideSagaErrorCode.None, string.Empty, isReplay);

    public static SagaOutcome Fail(RideSagaErrorCode code, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        if (code == RideSagaErrorCode.None)
        {
            throw new ArgumentException("Failure outcome requires a non-None error code.", nameof(code));
        }

        return new(false, code, message, false);
    }
}
