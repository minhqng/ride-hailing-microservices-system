namespace RideHailing.PaymentService.Integration.Payment;

/// <summary>
/// Single mapping from a Payment outcome payload to its stable message type
/// and routing key, shared by the command processor and the Production
/// reconciliation worker. The message identity must match whichever path
/// produced the outcome.
/// </summary>
public static class PaymentOutcomeContract
{
    public static (string MessageType, string RoutingKey) Describe(object outcome) => outcome switch
    {
        PaymentAuthorizedV1 => ("PaymentAuthorizedV1", "payment.authorized.v1"),
        PaymentAuthorizationFailedV1 => ("PaymentAuthorizationFailedV1", "payment.authorization-failed.v1"),
        PaymentAuthorizationVoidedV1 => ("PaymentAuthorizationVoidedV1", "payment.authorization-voided.v1"),
        PaymentAuthorizationVoidFailedV1 => ("PaymentAuthorizationVoidFailedV1", "payment.void-failed.v1"),
        PaymentCapturedV1 => ("PaymentCapturedV1", "payment.captured.v1"),
        PaymentCapturePendingV1 => ("PaymentCapturePendingV1", "payment.capture-pending.v1"),
        PaymentRequiresReviewV1 => ("PaymentRequiresReviewV1", "payment.requires-review.v1"),
        _ => throw new InvalidOperationException(
            $"Unsupported Payment result {outcome.GetType().Name}."),
    };
}
