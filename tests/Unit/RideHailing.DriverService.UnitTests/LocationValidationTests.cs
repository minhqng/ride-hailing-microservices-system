// B-W03-05: ingress validation (Doc 01 §5.3). Chốt ①: lat ±90 accepted here.
using RideHailing.DriverService.Application.Location;
using RideHailing.DriverService.Domain.Drivers;

namespace RideHailing.DriverService.UnitTests;

public class LocationValidationTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    private static LocationSample Base() => new(
        Guid.NewGuid(), "sess-1", 1, Now, 10.8, 106.6);

    [Fact]
    public void Valid_Sample_Passes()
    {
        LocationValidator.Validate(Base(), Now);
    }

    [Fact]
    public void Frozen_Baseline_Matches_W3_Values()
    {
        var baseline = LocationIngressOptions.FrozenW3Baseline();
        Assert.Equal(TimeSpan.FromMinutes(5), baseline.MaxFutureSkew);
        Assert.Equal(TimeSpan.FromHours(24), baseline.MaxPastAge);
    }

    [Fact]
    public void Custom_Policy_Overrides_Defaults()
    {
        // Same sample, stricter policy: 30-day-old sample rejected by default
        // but accepted when policy allows; proves windows are config, not code.
        var old = Base() with { CapturedAt = Now.AddDays(-2) };
        Assert.Throws<DriverDomainException>(
            () => LocationValidator.Validate(old, Now));
        LocationValidator.Validate(old, Now, new LocationIngressOptions
        {
            MaxFutureSkew = TimeSpan.FromMinutes(5),
            MaxPastAge = TimeSpan.FromDays(30),
        });
    }

    [Fact]
    public void Polar_Latitude_Accepted_At_Ingress()
    {
        // Chốt ①: 86° accepted by authority; W4 projection marks non-indexable.
        var s = Base() with { Latitude = 86.0 };
        LocationValidator.Validate(s, Now);
        Assert.False(LocationValidator.IsGeoIndexable(s.Latitude, s.Longitude));
        Assert.True(LocationValidator.IsGeoIndexable(10.8, 106.6));
    }

    [Theory]
    [InlineData(-91)]
    [InlineData(91)]
    public void Latitude_Out_Of_Range_Rejected(double lat)
    {
        var ex = Assert.Throws<DriverDomainException>(
            () => LocationValidator.Validate(Base() with { Latitude = lat }, Now));
        Assert.Equal(DriverErrors.InvalidCoordinate, ex.Code);
    }

    [Theory]
    [InlineData(-181)]
    [InlineData(181)]
    public void Longitude_Out_Of_Range_Rejected(double lon)
    {
        var ex = Assert.Throws<DriverDomainException>(
            () => LocationValidator.Validate(Base() with { Longitude = lon }, Now));
        Assert.Equal(DriverErrors.InvalidCoordinate, ex.Code);
    }

    [Fact]
    public void NaN_Rejected()
    {
        var ex = Assert.Throws<DriverDomainException>(
            () => LocationValidator.Validate(Base() with { Latitude = double.NaN }, Now));
        Assert.Equal(DriverErrors.InvalidCoordinate, ex.Code);
    }

    [Fact]
    public void Empty_Session_Rejected()
    {
        var ex = Assert.Throws<DriverDomainException>(
            () => LocationValidator.Validate(Base() with { DeviceSessionId = " " }, Now));
        Assert.Equal(DriverErrors.InvalidLocation, ex.Code);
    }

    [Fact]
    public void Future_CapturedAt_Rejected()
    {
        var ex = Assert.Throws<DriverDomainException>(
            () => LocationValidator.Validate(Base() with { CapturedAt = Now.AddMinutes(30) }, Now));
        Assert.Equal(DriverErrors.InvalidLocation, ex.Code);
    }
}
