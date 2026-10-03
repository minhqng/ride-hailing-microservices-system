// B-W03-07: persistence round-trip on REAL PostgreSQL (Npgsql raw, chốt ③).
// Infra semantics live in the shared DriverPostgresDatabase: fail loud,
// skip only without Docker.
using RideHailing.DriverService.Domain.Drivers;
using RideHailing.DriverService.Infrastructure.Persistence;
using Xunit.Abstractions;

namespace RideHailing.DriverService.IntegrationTests;

[Collection(DriverPostgresCollection.Name)]
public class DriverPersistenceTests
{
    private readonly ITestOutputHelper _out;
    private readonly DriverPostgresDatabase _db;

    public DriverPersistenceTests(ITestOutputHelper output, DriverPostgresDatabase db)
    {
        _out = output;
        _db = db;
    }

    private DriverRepository Repo() => new(_db.ConnectionString);

    [Fact]
    public async Task Save_Load_RoundTrip_Preserves_Coherence()
    {
        if (!_db.Ready())
        {
            _out.WriteLine("SKIP: " + _db.SkipReason);
            return;
        }

        var repo = Repo();
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
        if (!_db.Ready())
        {
            _out.WriteLine("SKIP: " + _db.SkipReason);
            return;
        }

        var repo = Repo();
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
