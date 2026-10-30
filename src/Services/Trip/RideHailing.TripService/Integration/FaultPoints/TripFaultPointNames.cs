// Canonical A-owned faultpoint vocabulary (A-W11-01, Doc 05A §19.2, Doc 02 §27).
//
// C owns the shared W11 deterministic fault platform (Doc 05C §30); until it
// is delivered, Trip integrates ONLY the A-owned named faultpoint at the
// durable boundary the canonical documents define, through the same seam the
// shared platform will adopt. The frozen integration contract is documented
// on TripFaultPointGate: name, activation gates, signaling protocol and
// bounded wait. No competing production faultpoint framework is introduced.

using RideHailing.TripService.Integration.Messaging;

namespace RideHailing.TripService.Integration.FaultPoints;

/// <summary>Canonical faultpoint names Trip integrates (Doc 02 §27.2).</summary>
public static class TripFaultPointNames
{
    /// <summary>
    /// The window between the local COMMIT of a Trip/Saga transition together
    /// with its required Outbox intent and the relay DISPATCH of that saga
    /// intent to the broker (Doc 03 REL-04, Doc 02 §27.1 "after Saga COMMIT
    /// before dispatch"). The relay can only observe rows a committed
    /// transaction produced, so a hold placed at the relay's pre-publish seam
    /// is necessarily after-commit; it pauses no business code and creates no
    /// extra transaction.
    /// </summary>
    public const string SagaAfterPersistBeforeDispatch = "Saga.AfterPersistBeforeDispatch";

    /// <summary>
    /// Every canonical faultpoint name Trip knows. Activation with an unknown
    /// name is rejected at startup (a typo must never silently disable a
    /// deterministic proof).
    /// </summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        SagaAfterPersistBeforeDispatch,
    };

    /// <summary>
    /// The saga's outbound payment dispatches this faultpoint gates: the
    /// three durable Payment commands a RideSaga step owns (DSI-034 lineage).
    /// Non-saga dispatches (matching, driver, cleanup commands) are out of
    /// this faultpoint's scope by name.
    /// </summary>
    public static bool IsSagaDispatch(string messageType) =>
        messageType is TripSagaIntents.AuthorizePaymentType
            or TripSagaIntents.CapturePaymentType
            or TripSagaIntents.VoidPaymentAuthorizationType;
}
