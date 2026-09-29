// B-W03-01: canonical P0 Driver states (Doc 01 §4.2).
namespace RideHailing.DriverService.Domain.Drivers;

public enum DriverStatus
{
    Offline = 0,
    Available = 1,
    Reserved = 2,
    OnTrip = 3,
}
