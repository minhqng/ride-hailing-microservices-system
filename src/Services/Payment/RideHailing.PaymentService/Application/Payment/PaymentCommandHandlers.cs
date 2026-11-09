using RideHailing.PaymentService.Domain;
using RideHailing.PaymentService.Integration.Payment;

namespace RideHailing.PaymentService.Application.Payment;

/// <summary>
/// Canonical mapping from a persisted <see cref="PaymentOperation"/> to its
/// published outcome contract. The Payment message processor and the
/// reconciliation worker both publish through these mappers, so a replayed or
/// reconciled operation always emits the same wire shape as the original
/// execution. There is no second command path here: execution is owned by
/// <c>PaymentExecution</c> under the envelope's operationKey.
/// </summary>
public static class PaymentCommandHandlers
{
    public static object ToAuthorizeResult(PaymentOperation result) => result.State switch
        {
            PaymentOperationState.Succeeded => new PaymentAuthorizedV1
            {
                TripId = result.TripId,
                OperationKey = result.OperationKey,
                ProviderEffectId = result.ProviderEffectId ?? throw new InvalidOperationException("Missing provider effect."),
                AmountMinor = result.AmountMinor,
                Currency = result.Currency
            },
            PaymentOperationState.Declined => new PaymentAuthorizationFailedV1
            {
                TripId = result.TripId,
                OperationKey = result.OperationKey,
                ReasonCode = result.ResultCode ?? "DECLINED",
                Retryable = false
            },
            PaymentOperationState.Unknown => new PaymentAuthorizationFailedV1
            {
                TripId = result.TripId,
                OperationKey = result.OperationKey,
                ReasonCode = result.ResultCode ?? "UNKNOWN",
                Retryable = true
            },
            PaymentOperationState.RequiresReview => new PaymentRequiresReviewV1
            {
                TripId = result.TripId,
                OperationKey = result.OperationKey,
                ReasonCode = result.ResultCode ?? "REQUIRES_REVIEW"
            },
            _ => new PaymentAuthorizationFailedV1
            {
                TripId = result.TripId,
                OperationKey = result.OperationKey,
                ReasonCode = result.ResultCode ?? "PENDING",
                Retryable = true
            }
        };

    public static object ToVoidResult(PaymentOperation result) => result.State switch
        {
            PaymentOperationState.Succeeded => new PaymentAuthorizationVoidedV1
            {
                TripId = result.TripId,
                OperationKey = result.OperationKey,
                ProviderEffectId = result.ProviderEffectId ?? throw new InvalidOperationException("Missing provider effect.")
            },
            _ => new PaymentAuthorizationVoidFailedV1
            {
                TripId = result.TripId,
                OperationKey = result.OperationKey,
                ReasonCode = result.ResultCode ?? result.State.ToString().ToUpperInvariant(),
                Retryable = result.State is PaymentOperationState.Pending or PaymentOperationState.Unknown
            }
        };

    public static object ToCaptureResult(PaymentOperation result) => result.State switch
        {
            PaymentOperationState.Succeeded => new PaymentCapturedV1
            {
                TripId = result.TripId,
                OperationKey = result.OperationKey,
                ProviderEffectId = result.ProviderEffectId ?? throw new InvalidOperationException("Missing provider effect."),
                AmountMinor = result.AmountMinor,
                Currency = result.Currency
            },
            PaymentOperationState.RequiresReview => new PaymentRequiresReviewV1
            {
                TripId = result.TripId,
                OperationKey = result.OperationKey,
                ReasonCode = result.ResultCode ?? "REQUIRES_REVIEW"
            },
            PaymentOperationState.Unknown => new PaymentCapturePendingV1
            {
                TripId = result.TripId,
                OperationKey = result.OperationKey,
                ReasonCode = result.ResultCode ?? "UNKNOWN"
            },
            PaymentOperationState.Pending => new PaymentCapturePendingV1
            {
                TripId = result.TripId,
                OperationKey = result.OperationKey,
                ReasonCode = result.ResultCode ?? "PENDING"
            },
            _ => new PaymentCapturePendingV1
            {
                TripId = result.TripId,
                OperationKey = result.OperationKey,
                ReasonCode = result.ResultCode ?? result.State.ToString().ToUpperInvariant()
            }
        };
}
