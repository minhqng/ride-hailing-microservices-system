// RideSaga status vocabulary (Doc 05A §18, Doc 02 §14.2).
//
// The status is the saga-level workflow position, deliberately separate from
// the current step: COMPENSATING says the workflow is undoing pre-pivot
// obligations, while the step says which obligation is being awaited.

namespace RideHailing.TripService.Domain.Sagas;

public enum RideSagaStatus
{
    /// <summary>Workflow progressing; awaiting the current step's authority.</summary>
    Active = 0,

    /// <summary>
    /// Pre-pivot undo in progress (release/void/cancel-round obligations).
    /// Entered on terminal authorization failure or cancellation racing an
    /// in-flight authorization (Doc 02 §14.5).
    /// </summary>
    Compensating = 1,

    /// <summary>Terminal success: capture recorded as succeeded.</summary>
    Completed = 2,

    /// <summary>Terminal success of the undo path: compensation finished.</summary>
    Compensated = 3,

    /// <summary>
    /// Visible manual-resolution state (DSI-028/DSL-005): the ride itself is
    /// never regressed; a human or policy resolves the pending obligation.
    /// </summary>
    RequiresReview = 4,
}

public static class RideSagaStatusRules
{
    /// <summary>Canonical persisted/wire names (CHECK-constraint domain set).</summary>
    public static string ToCanonicalName(this RideSagaStatus status) => status switch
    {
        RideSagaStatus.Active => "ACTIVE",
        RideSagaStatus.Compensating => "COMPENSATING",
        RideSagaStatus.Completed => "COMPLETED",
        RideSagaStatus.Compensated => "COMPENSATED",
        RideSagaStatus.RequiresReview => "REQUIRES_REVIEW",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown RideSagaStatus value."),
    };

    public static RideSagaStatus FromCanonicalName(string value) => value switch
    {
        "ACTIVE" => RideSagaStatus.Active,
        "COMPENSATING" => RideSagaStatus.Compensating,
        "COMPLETED" => RideSagaStatus.Completed,
        "COMPENSATED" => RideSagaStatus.Compensated,
        "REQUIRES_REVIEW" => RideSagaStatus.RequiresReview,
        _ => throw new InvalidOperationException($"Unknown persisted RideSagaStatus '{value}'."),
    };

    /// <summary>Saga-level terminal statuses (DSL-005 resolution states).</summary>
    public static bool IsTerminal(this RideSagaStatus status) =>
        status is RideSagaStatus.Completed or RideSagaStatus.Compensated;
}
