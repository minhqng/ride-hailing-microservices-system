// Canonical faultpoint vocabulary.
//
// Until the shared deterministic fault platform lands, Trip integrates ONLY
// its own named faultpoint at the durable boundary the canonical documents
// define, through the same seam the shared platform will adopt. The frozen
// integration contract is documented on TripFaultPointGate: name, activation
// gates, signaling protocol and bounded wait. No competing production
// faultpoint framework is introduced.

using RideHailing.TripService.Integration.Messaging;

namespace RideHailing.TripService.Integration.FaultPoints;

/// <summary>Canonical faultpoint names Trip integrates.</summary>
public static class TripFaultPointNames
{
    /// <summary>
    /// The window between the local COMMIT of a Trip/Saga transition together
    /// with its required Outbox intent and the relay DISPATCH of that saga
    /// intent to the broker ("after Saga COMMIT
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
    /// three durable Payment commands a RideSaga step owns (lineage).
    /// Non-saga dispatches (matching, driver, cleanup commands) are out of
    /// this faultpoint's scope by name.
    /// </summary>
    public static bool IsSagaDispatch(string messageType) =>
        messageType is TripSagaIntents.AuthorizePaymentType
            or TripSagaIntents.CapturePaymentType
            or TripSagaIntents.VoidPaymentAuthorizationType;
}
