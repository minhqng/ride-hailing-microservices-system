// FareSnapshot value-semantics tests: representation and invariants only.
// No pricing formula, retry, transaction or persistence behavior.

using RideHailing.TripService.Domain.Trips;

namespace RideHailing.TripService.UnitTests.Domain.Trips;

public sealed class FareSnapshotTests
{
    [Fact]
    public void ValidConstruction_PreservesFields()
    {
        var createdAt = new DateTimeOffset(2026, 9, 26, 7, 0, 0, TimeSpan.Zero);

        var fare = new FareSnapshot("VND", 50000m, "p0-test", createdAt);

        Assert.Equal("VND", fare.Currency);
        Assert.Equal(50000m, fare.Amount);
        Assert.Equal("p0-test", fare.PricingPolicyVersion);
        Assert.Equal(createdAt, fare.CreatedAt);
    }

    [Fact]
    public void EqualSnapshots_AreValueEqual()
    {
        var createdAt = new DateTimeOffset(2026, 9, 26, 7, 0, 0, TimeSpan.Zero);

        var left = new FareSnapshot("VND", 50000m, "p0-test", createdAt);
        var right = new FareSnapshot("VND", 50000m, "p0-test", createdAt);

        Assert.Equal(left, right);
        Assert.Equal(right.GetHashCode(), left.GetHashCode());
    }

    [Fact]
    public void DifferingAmount_IsNotValueEqual()
    {
        var createdAt = new DateTimeOffset(2026, 9, 26, 7, 0, 0, TimeSpan.Zero);

        var left = new FareSnapshot("VND", 50000m, "p0-test", createdAt);
        var right = new FareSnapshot("VND", 60000m, "p0-test", createdAt);

        Assert.NotEqual(left, right);
    }

    [Fact]
    public void NegativeAmount_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new FareSnapshot("VND", -1m, "p0-test", DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyCurrency_IsRejected(string currency)
    {
        Assert.Throws<ArgumentException>(() =>
            new FareSnapshot(currency, 50000m, "p0-test", DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyPricingPolicyVersion_IsRejected(string version)
    {
        Assert.Throws<ArgumentException>(() =>
            new FareSnapshot("VND", 50000m, version, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void DefaultTimestamp_IsRejected()
    {
        Assert.Throws<ArgumentException>(() =>
            new FareSnapshot("VND", 50000m, "p0-test", default));
    }

    [Fact]
    public void PublicSurface_IsImmutable()
    {
        var writable = typeof(FareSnapshot)
            .GetProperties()
            .Where(p => p.CanWrite)
            .Select(p => p.Name)
            .ToList();

        Assert.Empty(writable);
    }
}
