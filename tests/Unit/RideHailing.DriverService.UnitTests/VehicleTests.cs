// Hardening: Vehicle input classification (Doc 01 §22).
// Unfixable input must be 400-class (INVALID_IDENTITY/INVALID_REQUEST),
// never 409-conflict (a conflict retry could never succeed).
using RideHailing.DriverService.Domain.Drivers;
using RideHailing.DriverService.Domain.Vehicles;

namespace RideHailing.DriverService.UnitTests;

public sealed class VehicleTests
{
    [Fact]
    public void Empty_VehicleId_Is_Invalid()
    {
        var ex = Assert.Throws<DriverDomainException>(
            () => new Vehicle(Guid.Empty, Guid.NewGuid(), "P", "T"));
        Assert.Equal(DriverErrors.InvalidIdentity, ex.Code);
    }

    [Fact]
    public void Empty_DriverId_Is_Invalid()
    {
        var ex = Assert.Throws<DriverDomainException>(
            () => new Vehicle(Guid.NewGuid(), Guid.Empty, "P", "T"));
        Assert.Equal(DriverErrors.InvalidIdentity, ex.Code);
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("")]
    public void Blank_Plate_Is_Invalid_Request(string plate)
    {
        var ex = Assert.Throws<DriverDomainException>(
            () => new Vehicle(Guid.NewGuid(), Guid.NewGuid(), plate, "T"));
        Assert.Equal(DriverErrors.InvalidRequest, ex.Code);
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("")]
    public void Blank_VehicleType_Is_Invalid_Request(string vehicleType)
    {
        var ex = Assert.Throws<DriverDomainException>(
            () => new Vehicle(Guid.NewGuid(), Guid.NewGuid(), "P", vehicleType));
        Assert.Equal(DriverErrors.InvalidRequest, ex.Code);
    }

    [Fact]
    public void Valid_Vehicle_Trims_And_Deactivates()
    {
        var v = new Vehicle(Guid.NewGuid(), Guid.NewGuid(), "  P  ", " T ");
        Assert.Equal("P", v.Plate);
        Assert.True(v.Active);
        v.Deactivate();
        Assert.False(v.Active);
    }
}
