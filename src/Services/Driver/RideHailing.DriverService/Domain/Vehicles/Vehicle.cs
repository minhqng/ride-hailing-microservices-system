// B-W03-02: Vehicle baseline — operational association only, no fare policy (05B §4.2).
using RideHailing.DriverService.Domain.Drivers;

namespace RideHailing.DriverService.Domain.Vehicles;

public sealed class Vehicle
{
    public Guid VehicleId { get; }
    public Guid DriverId { get; }
    public string Plate { get; }
    public string VehicleType { get; }
    public bool Active { get; private set; }

    public Vehicle(Guid vehicleId, Guid driverId, string plate, string vehicleType, bool active = true)
    {
        if (vehicleId == Guid.Empty || driverId == Guid.Empty)
        {
            throw new DriverDomainException(DriverErrors.IllegalTransition, "vehicleId/driverId must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(plate))
        {
            throw new DriverDomainException(DriverErrors.IllegalTransition, "plate must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(vehicleType))
        {
            throw new DriverDomainException(DriverErrors.IllegalTransition, "vehicleType must not be empty.");
        }

        VehicleId = vehicleId;
        DriverId = driverId;
        Plate = plate.Trim();
        VehicleType = vehicleType.Trim();
        Active = active;
    }

    public void Deactivate()
    {
        Active = false;
    }
}
