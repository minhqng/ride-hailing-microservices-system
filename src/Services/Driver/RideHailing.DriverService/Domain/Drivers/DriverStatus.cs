// Canonical P0 Driver states.
namespace RideHailing.DriverService.Domain.Drivers;

public enum DriverStatus
{
    Offline = 0,
    Available = 1,
    Reserved = 2,
    OnTrip = 3,
}
