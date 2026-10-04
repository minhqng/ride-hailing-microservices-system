// B-W03-03: thin application handlers. Pure domain delegation; no HTTP, no DB here.
using RideHailing.DriverService.Domain.Drivers;

namespace RideHailing.DriverService.Application.Drivers;

public static class GoOnlineHandler
{
    public static void Handle(Driver driver)
    {
        ArgumentNullException.ThrowIfNull(driver);
        driver.GoOnline();
    }
}

public static class GoOfflineHandler
{
    public static void Handle(Driver driver)
    {
        ArgumentNullException.ThrowIfNull(driver);
        driver.GoOffline();
    }
}
