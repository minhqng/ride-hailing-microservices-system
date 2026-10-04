// Frozen P0 deterministic fare policy (A-W04-05, DSI-014 support).
//
// MECHANICAL W4 values, not real Grab/Uber pricing: VND currency, policy
// version "p0-w4-v1", 10_000 base fare plus 5_000 per straight-line Haversine
// kilometre. No routing API, no traffic, no surge, no dynamic config, no
// Pricing service. The snapshot freezes Amount/Currency/PolicyVersion at the
// application-supplied creation timestamp; replay never recalculates.

using RideHailing.TripService.Domain.Trips;

namespace RideHailing.TripService.Application.Trips.CreateRide;

/// <summary>
/// Deterministic Trip-local P0 fare computation for CreateRide.
/// </summary>
public static class DeterministicFareCalculator
{
    /// <summary>Frozen P0 currency marker.</summary>
    public const string Currency = "VND";

    /// <summary>Frozen P0 pricing policy version.</summary>
    public const string PricingPolicyVersion = "p0-w4-v1";

    /// <summary>Frozen P0 base fare; also the exact zero-distance fare.</summary>
    public const decimal BaseFare = 10_000m;

    /// <summary>Frozen P0 per-kilometre rate.</summary>
    public const decimal PerKilometre = 5_000m;

    // Mean-earth-radius Haversine straight-line distance (frozen P0 choice).
    private const double EarthRadiusKm = 6371.0;

    /// <summary>
    /// Computes the frozen fare for one pickup/dropoff pair at
    /// <paramref name="createdAt"/>. Same input and timestamp always yield
    /// the same snapshot.
    /// </summary>
    public static FareSnapshot Calculate(TripLocation pickup, TripLocation dropoff, DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(pickup);
        ArgumentNullException.ThrowIfNull(dropoff);

        if (createdAt == default)
        {
            throw new ArgumentException("Fare calculation requires a concrete creation timestamp.", nameof(createdAt));
        }

        var distanceKm = HaversineKm(pickup, dropoff);
        var amount = BaseFare + PerKilometre * (decimal)distanceKm;
        amount = Math.Round(amount, 2, MidpointRounding.AwayFromZero);

        return new FareSnapshot(Currency, amount, PricingPolicyVersion, createdAt);
    }

    private static double HaversineKm(TripLocation pickup, TripLocation dropoff)
    {
        static double ToRadians(double degrees) => degrees * Math.PI / 180.0;

        var lat1 = ToRadians(pickup.Latitude);
        var lat2 = ToRadians(dropoff.Latitude);
        var dLat = ToRadians(dropoff.Latitude - pickup.Latitude);
        var dLon = ToRadians(dropoff.Longitude - pickup.Longitude);

        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(lat1) * Math.Cos(lat2) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        var c = 2 * Math.Asin(Math.Min(1.0, Math.Sqrt(a)));

        return EarthRadiusKm * c;
    }
}
