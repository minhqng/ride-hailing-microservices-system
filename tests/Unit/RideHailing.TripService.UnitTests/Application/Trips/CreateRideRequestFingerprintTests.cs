// CreateRide fingerprint unit proofs (A-W04-03): determinism, semantic
// sensitivity, +0.0/-0.0 equivalence and culture invariance. No hashing
// internals are tested; only the contract the handler depends on.

using System.Globalization;
using System.Text.RegularExpressions;
using RideHailing.TripService.Application.Trips.CreateRide;
using RideHailing.TripService.Domain.Trips;

namespace RideHailing.TripService.UnitTests.Application.Trips;

public sealed class CreateRideRequestFingerprintTests
{
    private static readonly Guid RiderA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid RiderB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private static TripLocation Loc(double lat, double lon) => new(lat, lon);

    [Fact]
    public void SameSemanticRequest_YieldsSameFingerprint_Deterministically()
    {
        var first = CreateRideRequestFingerprint.Compute(RiderA, Loc(10.762622, 106.660172), Loc(10.776889, 106.700806));
        var second = CreateRideRequestFingerprint.Compute(RiderA, Loc(10.762622, 106.660172), Loc(10.776889, 106.700806));

        Assert.Equal(first, second);
        Assert.Matches(new Regex("^[0-9a-f]{64}$"), first);
    }

    [Fact]
    public void ChangedRider_YieldsDifferentFingerprint()
    {
        var a = CreateRideRequestFingerprint.Compute(RiderA, Loc(10.762622, 106.660172), Loc(10.776889, 106.700806));
        var b = CreateRideRequestFingerprint.Compute(RiderB, Loc(10.762622, 106.660172), Loc(10.776889, 106.700806));

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ChangedPickup_YieldsDifferentFingerprint()
    {
        var a = CreateRideRequestFingerprint.Compute(RiderA, Loc(10.762622, 106.660172), Loc(10.776889, 106.700806));
        var b = CreateRideRequestFingerprint.Compute(RiderA, Loc(10.762700, 106.660172), Loc(10.776889, 106.700806));

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ChangedDropoff_YieldsDifferentFingerprint()
    {
        var a = CreateRideRequestFingerprint.Compute(RiderA, Loc(10.762622, 106.660172), Loc(10.776889, 106.700806));
        var b = CreateRideRequestFingerprint.Compute(RiderA, Loc(10.762622, 106.660172), Loc(10.776889, 106.700900));

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void NegativeZero_EqualsPositiveZero()
    {
        var positive = CreateRideRequestFingerprint.Compute(RiderA, Loc(0.0, 106.660172), Loc(10.776889, 0.0));
        var negative = CreateRideRequestFingerprint.Compute(RiderA, Loc(-0.0, 106.660172), Loc(10.776889, -0.0));

        Assert.Equal(positive, negative);
    }

    [Fact]
    public void Fingerprint_IsInvariantUnderProcessCulture()
    {
        var baseline = CreateRideRequestFingerprint.Compute(RiderA, Loc(10.762622, 106.660172), Loc(10.776889, 106.700806));

        var previousCulture = CultureInfo.CurrentCulture;
        var previousUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("fr-FR");
            CultureInfo.CurrentUICulture = new CultureInfo("tr-TR");

            var underForeignCulture = CreateRideRequestFingerprint.Compute(
                RiderA, Loc(10.762622, 106.660172), Loc(10.776889, 106.700806));

            Assert.Equal(baseline, underForeignCulture);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }
    }

    [Fact]
    public void BuildScope_IsRiderDeterministic_AndWithinColumnLimit()
    {
        var a = CreateRideRequestFingerprint.BuildScope(RiderA);
        var b = CreateRideRequestFingerprint.BuildScope(RiderA);
        var other = CreateRideRequestFingerprint.BuildScope(RiderB);

        Assert.Equal(a, b);
        Assert.NotEqual(a, other);
        Assert.StartsWith("trip:create:", a, StringComparison.Ordinal);
        Assert.True(a.Length <= CreateRideRequestFingerprint.MaxScopeLength);
    }
}
