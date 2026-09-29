// FareSnapshot value object (Doc 01 §7).
//
// W3 responsibility is REPRESENTATION and INVARIANTS: the frozen
// authoritative fare for one Trip (DSI-014). Fare calculation (distance/time
// formula, dynamic pricing, CreateRide-time computation) is intentionally
// deferred and must not appear here.

namespace RideHailing.TripService.Domain.Trips;

/// <summary>
/// Immutable identity-less frozen fare for one Trip. Equality is by value;
/// there is no mutating API, so a frozen snapshot cannot drift when pricing
/// configuration changes or a stale message arrives (DSI-014).
/// </summary>
public sealed record FareSnapshot
{
    /// <summary>Fare currency marker as supplied by pricing (for example "VND").</summary>
    public string Currency { get; }

    /// <summary>Authoritative monetary amount. Never negative.</summary>
    public decimal Amount { get; }

    /// <summary>Pricing policy version the fare was computed under.</summary>
    public string PricingPolicyVersion { get; }

    /// <summary>When this snapshot was frozen.</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>
    /// Freezes a fare snapshot. Rejects empty currency/policy markers,
    /// negative amounts and the default timestamp.
    /// </summary>
    public FareSnapshot(string currency, decimal amount, string pricingPolicyVersion, DateTimeOffset createdAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);
        ArgumentException.ThrowIfNullOrWhiteSpace(pricingPolicyVersion);

        if (amount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), amount, "Fare amount must not be negative.");
        }

        if (createdAt == default)
        {
            throw new ArgumentException("Fare snapshot requires a concrete creation timestamp.", nameof(createdAt));
        }

        Currency = currency;
        Amount = amount;
        PricingPolicyVersion = pricingPolicyVersion;
        CreatedAt = createdAt;
    }
}
