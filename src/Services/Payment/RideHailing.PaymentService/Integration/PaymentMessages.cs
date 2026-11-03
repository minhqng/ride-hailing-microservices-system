using System.Text.Json.Serialization;
using RideHailing.PaymentService.Domain;

namespace RideHailing.PaymentService.Integration.Payment;

/// <summary>Payment command/event payloads for the Payment-to-Saga integration (W10).</summary>
// The stable operationKey is ENVELOPE metadata (Doc 05A §5.1 / Doc 05C §6):
// Trip producers must not duplicate it inside the payload. The optional
// payload property is accepted only when it exactly echoes the envelope, so a
// mixed producer cannot smuggle a second logical identity into one command.
public sealed record AuthorizePaymentV1
{
    [JsonPropertyName("tripId")] public required Guid TripId { get; init; }
    [JsonPropertyName("operationKey")] public string? OperationKey { get; init; }
    [JsonPropertyName("amountMinor")] public required long AmountMinor { get; init; }
    [JsonPropertyName("currency")] public required string Currency { get; init; }
    [JsonPropertyName("paymentType")] public PaymentOperationType PaymentType { get; init; } = PaymentOperationType.Authorize;
}

public sealed record VoidPaymentAuthorizationV1
{
    [JsonPropertyName("tripId")] public required Guid TripId { get; init; }
    [JsonPropertyName("operationKey")] public string? OperationKey { get; init; }
    [JsonPropertyName("reason")] public string? Reason { get; init; }
    [JsonPropertyName("amountMinor")] public long AmountMinor { get; init; } = 1;
    [JsonPropertyName("currency")] public string Currency { get; init; } = "VND";
}

public sealed record CapturePaymentV1
{
    [JsonPropertyName("tripId")] public required Guid TripId { get; init; }
    [JsonPropertyName("operationKey")] public string? OperationKey { get; init; }
    [JsonPropertyName("amountMinor")] public required long AmountMinor { get; init; }
    [JsonPropertyName("currency")] public required string Currency { get; init; }
}

public sealed record PaymentAuthorizedV1
{
    [JsonPropertyName("tripId")] public required Guid TripId { get; init; }
    [JsonPropertyName("operationKey")] public required string OperationKey { get; init; }
    [JsonPropertyName("providerEffectId")] public required string ProviderEffectId { get; init; }
    [JsonPropertyName("amountMinor")] public required long AmountMinor { get; init; }
    [JsonPropertyName("currency")] public required string Currency { get; init; }
}

public sealed record PaymentAuthorizationFailedV1
{
    [JsonPropertyName("tripId")] public required Guid TripId { get; init; }
    [JsonPropertyName("operationKey")] public required string OperationKey { get; init; }
    [JsonPropertyName("reasonCode")] public required string ReasonCode { get; init; }
    [JsonPropertyName("retryable")] public required bool Retryable { get; init; }
}

public sealed record PaymentAuthorizationVoidedV1
{
    [JsonPropertyName("tripId")] public required Guid TripId { get; init; }
    [JsonPropertyName("operationKey")] public required string OperationKey { get; init; }
    [JsonPropertyName("providerEffectId")] public required string ProviderEffectId { get; init; }
}

/// <summary>
/// Void-specific failure outcome. It carries the VOID operation lineage, so a
/// cancelled-but-authorized Saga never mistakes it for an authorization
/// failure and never parks a false key mismatch.
/// </summary>
public sealed record PaymentAuthorizationVoidFailedV1
{
    [JsonPropertyName("tripId")] public required Guid TripId { get; init; }
    [JsonPropertyName("operationKey")] public required string OperationKey { get; init; }
    [JsonPropertyName("reasonCode")] public required string ReasonCode { get; init; }
    [JsonPropertyName("retryable")] public required bool Retryable { get; init; }
}

public sealed record PaymentCapturedV1
{
    [JsonPropertyName("tripId")] public required Guid TripId { get; init; }
    [JsonPropertyName("operationKey")] public required string OperationKey { get; init; }
    [JsonPropertyName("providerEffectId")] public required string ProviderEffectId { get; init; }
    [JsonPropertyName("amountMinor")] public required long AmountMinor { get; init; }
    [JsonPropertyName("currency")] public required string Currency { get; init; }
}

public sealed record PaymentCapturePendingV1
{
    [JsonPropertyName("tripId")] public required Guid TripId { get; init; }
    [JsonPropertyName("operationKey")] public required string OperationKey { get; init; }
    [JsonPropertyName("reasonCode")] public required string ReasonCode { get; init; }
}

public sealed record PaymentRequiresReviewV1
{
    [JsonPropertyName("tripId")] public required Guid TripId { get; init; }
    [JsonPropertyName("operationKey")] public required string OperationKey { get; init; }
    [JsonPropertyName("reasonCode")] public required string ReasonCode { get; init; }
}
