// B-W03-07: persistence round-trip on real PG (Npgsql raw, chốt ③).
// Returns early when PG unavailable (same convention as W2 fixtures).
using RideHailing.DriverService.Domain.Drivers;
using RideHailing.DriverService.Infrastructure.Persistence;

namespace RideHailing.DriverService.IntegrationTests;

public class DriverPersistenceTests
{
    private static string ConnectionString() => DriverPostgresFixture.ResolveConnectionString();

    [Fact]
    public async Task Save_Load_RoundTrip_Preserves_Coherence()
    {
        DriverRepository repo;
        try
        {
            repo = new DriverRepository(ConnectionString());
            await repo.EnsureTablesAsync();
        }
        catch
        {
            return; // PG unavailable
        }

        var driver = Driver.Create(Guid.NewGuid(), Guid.NewGuid());
        driver.GoOnline();
        await repo.SaveAsync(driver);

        var loaded = await repo.LoadAsync(driver.DriverId);
        Assert.NotNull(loaded);
        Assert.Equal(DriverStatus.Available, loaded!.Status);
        Assert.Equal(driver.Version, loaded.Version);
        Assert.Null(loaded.ReservedTripId);
    }

    [Fact]
    public async Task Save_Load_Reserved_State()
    {
        DriverRepository repo;
        try
        {
            repo = new DriverRepository(ConnectionString());
            await repo.EnsureTablesAsync();
        }
        catch
        {
            return;
        }

        var driver = Driver.Create(Guid.NewGuid(), Guid.NewGuid());
        driver.GoOnline();
        var t = Guid.NewGuid();
        var r = Guid.NewGuid();
        driver.Reserve(t, r);
        await repo.SaveAsync(driver);

        var loaded = await repo.LoadAsync(driver.DriverId);
        Assert.NotNull(loaded);
        Assert.Equal(DriverStatus.Reserved, loaded!.Status);
        Assert.Equal(t, loaded.ReservedTripId);
        Assert.Equal(r, loaded.ReservationId);
    }
}
