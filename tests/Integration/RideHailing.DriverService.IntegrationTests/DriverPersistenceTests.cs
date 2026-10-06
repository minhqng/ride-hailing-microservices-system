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
    private readonly DriverPostgresDatabase _db;

    public DriverPersistenceTests(ITestOutputHelper output, DriverPostgresDatabase db)
    {
        _db = db;
    }

    private DriverRepository Repo() => new(_db.ConnectionString);

    [Fact]
    public async Task Save_Load_RoundTrip_Preserves_Coherence()
    {
        if (!_db.Ready())
        {
            Assert.Fail("Real PostgreSQL proof required but unavailable: " + _db.SkipReason);
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
            Assert.Fail("Real PostgreSQL proof required but unavailable: " + _db.SkipReason);
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

    [Fact]
    public async Task TryInsert_ConcurrentDuplicates_ExactlyOneWins()
    {
        if (!_db.Ready())
        {
            Assert.Fail("Real PostgreSQL proof required but unavailable: " + _db.SkipReason);
        }

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
