// Deterministic P0 fare unit proofs (A-W04-05): frozen policy markers,
// zero-distance base fare, determinism and timestamp flow. No live
// maps/network; the known-vector amount pins the frozen formula.

using RideHailing.TripService.Application.Trips.CreateRide;
using RideHailing.TripService.Domain.Trips;

namespace RideHailing.TripService.UnitTests.Application.Trips;

public sealed class DeterministicFareCalculatorTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 9, 29, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SameInputAndTime_YieldsSameSnapshot()
    {
        var pickup = new TripLocation(10.762622, 106.660172);
        var dropoff = new TripLocation(10.776889, 106.700806);

        var first = DeterministicFareCalculator.Calculate(pickup, dropoff, CreatedAt);
        var second = DeterministicFareCalculator.Calculate(pickup, dropoff, CreatedAt);

        Assert.Equal(first, second);
    }

    [Fact]
    public void ZeroDistance_YieldsBaseFare()
    {
        var point = new TripLocation(10.762622, 106.660172);

        var fare = DeterministicFareCalculator.Calculate(point, point, CreatedAt);

        Assert.Equal(DeterministicFareCalculator.BaseFare, fare.Amount);
        Assert.Equal(10_000m, fare.Amount);
    }

    [Fact]
    public void KnownPair_YieldsFrozenExpectedAmount()
    {
        var pickup = new TripLocation(10.762622, 106.660172);
        var dropoff = new TripLocation(10.776889, 106.700806);

        var fare = DeterministicFareCalculator.Calculate(pickup, dropoff, CreatedAt);

        // Frozen P0 vector: Haversine straight-line distance over the mean
        // earth radius, 10_000 base + 5_000/km, rounded to 2 decimals.
        Assert.Equal(33568.45m, fare.Amount);
    }

    [Fact]
    public void Snapshot_CarriesFrozenPolicyMarkers_AndSuppliedTimestamp()
    {
        var pickup = new TripLocation(10.762622, 106.660172);
        var dropoff = new TripLocation(10.776889, 106.700806);

        var fare = DeterministicFareCalculator.Calculate(pickup, dropoff, CreatedAt);

        Assert.Equal("VND", fare.Currency);
        Assert.Equal("p0-w4-v1", fare.PricingPolicyVersion);
        Assert.Equal(CreatedAt, fare.CreatedAt);
        Assert.True(fare.Amount >= 0);
        Assert.Equal(fare.Amount, Math.Round(fare.Amount, 2, MidpointRounding.AwayFromZero));
    }
}
