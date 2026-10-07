// B-W03-07: persistence round-trip on REAL PostgreSQL (Npgsql raw).
// Asserts the FULL persisted shape: every domain field must survive the
// round-trip, not just status/version (lossless proof).
using RideHailing.DriverService.Domain.Drivers;
using RideHailing.DriverService.Infrastructure.Persistence;

namespace RideHailing.DriverService.IntegrationTests;

[Collection(DriverPostgresCollection.Name)]
public class DriverPersistenceTests
{
    private readonly DriverPostgresDatabase _db;

    public DriverPersistenceTests(DriverPostgresDatabase db)
    {
        _db = db;
    }

    private DriverRepository Repo() => new(_db.ConnectionString);

    [Fact]
    public async Task Save_Load_RoundTrip_Preserves_Full_Shape()
    {
        var repo = Repo();
        var vehicle = Guid.NewGuid();
        var driver = Driver.Create(Guid.NewGuid(), Guid.NewGuid(), vehicle);
        driver.GoOnline();
        await repo.SaveAsync(driver);

        var loaded = await repo.LoadAsync(driver.DriverId);
        Assert.NotNull(loaded);
        Assert.Equal(DriverStatus.Available, loaded!.Status);
        Assert.Equal(driver.Version, loaded.Version);
        Assert.Equal(driver.UserId, loaded.UserId);
        Assert.Equal(vehicle, loaded.VehicleId);
        Assert.Null(loaded.ReservedTripId);
        Assert.Null(loaded.ReservationId);
        Assert.Null(loaded.ActiveTripId);
        // timestamptz stores microseconds; .NET ticks carry 100ns: allow sub-ms drift.
        Assert.True((loaded.UpdatedAt - driver.UpdatedAt).Duration() < TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task Save_Load_Reserved_State()
    {
        var repo = Repo();
        var driver = Driver.Create(Guid.NewGuid(), Guid.NewGuid());
        driver.GoOnline();
        var t = Guid.NewGuid();
        var r = Guid.NewGuid();
        driver.Reserve(t, r, driver.Version);
        await repo.SaveAsync(driver);

        var loaded = await repo.LoadAsync(driver.DriverId);
        Assert.NotNull(loaded);
        Assert.Equal(DriverStatus.Reserved, loaded!.Status);
        Assert.Equal(t, loaded.ReservedTripId);
        Assert.Equal(r, loaded.ReservationId);
        Assert.Null(loaded.ActiveTripId);
    }

    [Fact]
    public async Task Save_Load_OnTrip_State()
    {
        var repo = Repo();
        var driver = Driver.Create(Guid.NewGuid(), Guid.NewGuid());
        driver.GoOnline();
        var t = Guid.NewGuid();
        var r = Guid.NewGuid();
        driver.Reserve(t, r, driver.Version);
        driver.MarkTripStarted(t, r);
        await repo.SaveAsync(driver);

        var loaded = await repo.LoadAsync(driver.DriverId);
        Assert.NotNull(loaded);
        Assert.Equal(DriverStatus.OnTrip, loaded!.Status);
        Assert.Equal(t, loaded.ActiveTripId);
        Assert.Null(loaded.ReservedTripId);
        Assert.Null(loaded.ReservationId);
    }

    [Fact]
    public async Task TryInsert_ConcurrentDuplicates_ExactlyOneWins()
    {
        // Eight concurrent creates with the same driver id must linearize on
        // the primary key: exactly one insert wins, losers observe the
        // conflict instead of resetting state (DSR-002 duplicate delivery).
        var repo = Repo();
        var id = Guid.NewGuid();
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            repo.TryInsertAsync(Driver.Create(id, Guid.NewGuid()))));

        Assert.Equal(1, results.Count(r => r));

        var loaded = await repo.LoadAsync(id);
        Assert.NotNull(loaded);
        Assert.Equal(DriverStatus.Offline, loaded!.Status);
    }
}
