// TripLocation domain validation tests (Doc 01 §5.3).

using RideHailing.TripService.Domain.Trips;

namespace RideHailing.TripService.UnitTests.Domain.Trips;

public sealed class TripLocationTests
{
    [Fact]
    public void ValidCoordinate_PreservesValues()
    {
        var location = new TripLocation(10.762622, 106.660172);

        Assert.Equal(10.762622, location.Latitude);
        Assert.Equal(106.660172, location.Longitude);
    }

    [Theory]
    [InlineData(-90.01)]
    [InlineData(90.01)]
    public void LatitudeOutsideRange_IsRejected(double latitude)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TripLocation(latitude, 0));
    }

    [Theory]
    [InlineData(-180.01)]
    [InlineData(180.01)]
    public void LongitudeOutsideRange_IsRejected(double longitude)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TripLocation(0, longitude));
    }

    [Theory]
    [InlineData(double.NaN, 0)]
    [InlineData(0, double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity, double.NaN)]
    public void NonFiniteCoordinate_IsRejected(double latitude, double longitude)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TripLocation(latitude, longitude));
    }

    [Fact]
    public void EqualCoordinates_AreValueEqual()
    {
        Assert.Equal(new TripLocation(10, 106), new TripLocation(10, 106));
        Assert.NotEqual(new TripLocation(10, 106), new TripLocation(11, 106));
    }
}
