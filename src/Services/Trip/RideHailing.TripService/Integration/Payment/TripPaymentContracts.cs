// Trip-produced Payment command payloads (A-W10, Doc 05A §5.1, Doc 02 §15).
//
// SHAPE ONLY for the W10 producer leg: Payment (C) owns consuming
// payment.commands.q and deciding provider effects; Trip owns the exact
// logical identities. Every command carries its stable operationKey and
// sagaId on the ENVELOPE (not the payload): one saga step = one operationKey
// = at most one logical Payment effect (DSI-034). The payload carries only
// what Payment authority needs to execute the operation: the ride identity
// and the frozen fare.

using System.Text.Json.Serialization;
using RideHailing.TripService.Domain.Trips;

namespace RideHailing.TripService.Integration.Payment;

/// <summary>Request a payment authorization hold for one accepted ride.</summary>
public sealed record AuthorizePaymentV1
{
    [JsonPropertyName("tripId")]
    public required Guid TripId { get; init; }

    [JsonPropertyName("amountMinor")]
    public required long AmountMinor { get; init; }

    [JsonPropertyName("currency")]
    public required string Currency { get; init; }
}

/// <summary>Capture the previously authorized hold after ride completion.</summary>
public sealed record CapturePaymentV1
{
    [JsonPropertyName("tripId")]
    public required Guid TripId { get; init; }

    [JsonPropertyName("amountMinor")]
    public required long AmountMinor { get; init; }

    [JsonPropertyName("currency")]
    public required string Currency { get; init; }
}

/// <summary>Void an authorization that committed under a cancelled ride.</summary>
public sealed record VoidPaymentAuthorizationV1
{
    [JsonPropertyName("tripId")]
    public required Guid TripId { get; init; }

    [JsonPropertyName("amountMinor")]
    public required long AmountMinor { get; init; }

    [JsonPropertyName("currency")]
    public required string Currency { get; init; }
}

/// <summary>
/// Deterministic frozen-fare → minor-unit conversion shared by all three
/// Payment commands, so authorize/capture/void always quote the same amount
/// for one ride (the fare snapshot is immutable, DSI-014).
/// </summary>
public static class PaymentAmounts
{
    /// <summary>Currencies with no minor unit. Everything else uses 2.</summary>
    private static readonly HashSet<string> ZeroDecimalCurrencies = new(StringComparer.Ordinal)
    {
        "VND", "JPY", "KRW",
    };

    public static long ToMinorUnits(FareSnapshot fare)
    {
        ArgumentNullException.ThrowIfNull(fare);
        var decimals = ZeroDecimalCurrencies.Contains(fare.Currency) ? 0 : 2;
        var minor = decimal.Round(fare.Amount, decimals, MidpointRounding.ToEven);
        var scaled = decimals == 0 ? minor : minor * (decimal)Math.Pow(10, decimals);
        var units = decimal.ToInt64(scaled);
        if (units <= 0)
        {
            throw new InvalidOperationException(
                $"Trip fare {fare.Amount} {fare.Currency} converts to a non-positive payment amount; Payment operations require a positive amount.");
        }

        return units;
    }
}
