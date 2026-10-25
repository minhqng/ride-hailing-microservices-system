using Microsoft.Extensions.Logging;
using RideHailing.PaymentService.Domain;
using RideHailing.PaymentService.Infrastructure;
using RideHailing.PaymentService.Integration.Payment;

namespace RideHailing.PaymentService.Application.Payment;

public sealed class PaymentCommandHandlers(
    PaymentExecution execution,
    ProviderLedger provider,
    TimeProvider time,
    ILogger<PaymentCommandHandlers>? logger = null)
{
    public async Task<object> HandleAuthorizeAsync(AuthorizePaymentV1 command, PaymentSimulatorMode mode = PaymentSimulatorMode.Success, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var request = PaymentOperation.Create(command.OperationKey, command.TripId, command.PaymentType, command.AmountMinor, command.Currency, time.GetUtcNow());
        var result = await execution.ExecuteAsync(request, mode, cancellationToken);
        logger?.LogInformation("Authorize {TripId} key {OperationKey} state {State}.", command.TripId, command.OperationKey, result.State);
        return ToAuthorizeResult(result);
    }

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

    public async Task<object> HandleVoidAsync(VoidPaymentAuthorizationV1 command, PaymentSimulatorMode mode = PaymentSimulatorMode.Success, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var amount = command.AmountMinor <= 0 ? 1 : command.AmountMinor;
        var currency = string.IsNullOrWhiteSpace(command.Currency) ? "VND" : command.Currency;
        var request = PaymentOperation.Create(command.OperationKey, command.TripId, PaymentOperationType.Void, amount, currency, time.GetUtcNow());
        // Amount/currency for void are not money-moving; keep a stable placeholder. Caller should pass authoritative values in future revision.
        var result = await execution.ExecuteAsync(request, mode, cancellationToken);
        logger?.LogInformation("Void {TripId} key {OperationKey} state {State}.", command.TripId, command.OperationKey, result.State);
        return ToVoidResult(result);
    }

    public static object ToVoidResult(PaymentOperation result) => result.State switch
        {
            PaymentOperationState.Succeeded => new PaymentAuthorizationVoidedV1
            {
                TripId = result.TripId,
                OperationKey = result.OperationKey,
                ProviderEffectId = result.ProviderEffectId ?? throw new InvalidOperationException("Missing provider effect.")
            },
            _ => new PaymentAuthorizationFailedV1
            {
                TripId = result.TripId,
                OperationKey = result.OperationKey,
                ReasonCode = result.ResultCode ?? result.State.ToString().ToUpperInvariant(),
                Retryable = result.State is PaymentOperationState.Pending or PaymentOperationState.Unknown
            }
        };

    public async Task<object> HandleCaptureAsync(CapturePaymentV1 command, PaymentSimulatorMode mode = PaymentSimulatorMode.Success, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var request = PaymentOperation.Create(command.OperationKey, command.TripId, PaymentOperationType.Capture, command.AmountMinor, command.Currency, time.GetUtcNow());
        var result = await execution.ExecuteAsync(request, mode, cancellationToken);
        logger?.LogInformation("Capture {TripId} key {OperationKey} state {State}.", command.TripId, command.OperationKey, result.State);
        return ToCaptureResult(result);
    }

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

    public async Task<PaymentOperation> ReconcileAsync(string operationKey, CancellationToken cancellationToken = default)
        => await execution.ReconcileAsync(operationKey, provider, cancellationToken);
}
