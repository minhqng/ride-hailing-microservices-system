// RideSaga step vocabulary.
//
// The current step is the saga's own position in the payment orchestration,
// not a copy of Trip state: Trip owns the ride FSM, the saga owns
// which logical Payment operation / compensation obligation is awaited.

namespace RideHailing.TripService.Domain.Sagas;

public enum RideSagaStep
{
    /// <summary>AuthorizePayment(K) dispatched; awaiting the Payment outcome.</summary>
    AuthorizePending = 0,

    /// <summary>
    /// Authorization succeeded (authoritative); the ride may legally
    /// start and complete; capture has not been dispatched yet.
    /// </summary>
    Authorized = 1,

    /// <summary>CapturePayment(K_capture) dispatched after completion; awaiting outcome.</summary>
    CapturePending = 2,

    /// <summary>Capture succeeded; forward recovery finished.</summary>
    Captured = 3,

    /// <summary>
    /// Compensation: exact reservation release awaited.
    /// Void, when it exists, has already resolved or was never needed.
    /// </summary>
    ReleasePending = 4,

    /// <summary>
    /// Compensation: an authorization that had committed must be voided;
    /// PaymentAuthorizationVoided(K_void) awaited.
    /// </summary>
    VoidPending = 5,
}

public static class RideSagaStepRules
{
    /// <summary>Canonical persisted/wire names (CHECK-constraint domain set).</summary>
    public static string ToCanonicalName(this RideSagaStep step) => step switch
    {
        RideSagaStep.AuthorizePending => "AUTHORIZE_PENDING",
        RideSagaStep.Authorized => "AUTHORIZED",
        RideSagaStep.CapturePending => "CAPTURE_PENDING",
        RideSagaStep.Captured => "CAPTURED",
        RideSagaStep.ReleasePending => "RELEASE_PENDING",
        RideSagaStep.VoidPending => "VOID_PENDING",
        _ => throw new ArgumentOutOfRangeException(nameof(step), step, "Unknown RideSagaStep value."),
    };

    public static RideSagaStep FromCanonicalName(string value) => value switch
    {
        "AUTHORIZE_PENDING" => RideSagaStep.AuthorizePending,
        "AUTHORIZED" => RideSagaStep.Authorized,
        "CAPTURE_PENDING" => RideSagaStep.CapturePending,
        "CAPTURED" => RideSagaStep.Captured,
        "RELEASE_PENDING" => RideSagaStep.ReleasePending,
        "VOID_PENDING" => RideSagaStep.VoidPending,
        _ => throw new InvalidOperationException($"Unknown persisted RideSagaStep '{value}'."),
    };
}
