// B-W03-07: persistence round-trip on REAL PostgreSQL (Npgsql raw).
// Asserts the FULL persisted shape: every domain field must survive the
// round-trip, not just status/version (lossless proof).
using RideHailing.DriverService.Application.Messaging;
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
        driver.GoOnline(driver.Version);
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
        driver.GoOnline(driver.Version);
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
    public async Task Reserve_With_Generation_Survives_Repository_Restart()
    {
        // W08 matchingRound lineage: the winning reserve records the caller's
        // generation durably, and a FRESH repository instance (process restart
        // simulation) replays the same identity with the SAME generation —
        // never re-derived, never defaulted. An unknown-generation event
        // (historical row) publishes field-absent provenance, still safely
        // parked by Trip rather than fabricated.
        var driver = Driver.Create(Guid.NewGuid(), Guid.NewGuid());
        driver.GoOnline(driver.Version);
        await Repo().TryInsertAsync(driver);
        var loaded = (await Repo().LoadAsync(driver.DriverId))!;
        var trip = Guid.NewGuid();
        var reservation = Guid.NewGuid();
        const int round = 7;

        var won = await Repo().TryReserveAsync(driver.DriverId, trip, reservation, loaded.Version, round);
        Assert.Equal(ReserveOutcome.Won, won.Outcome);

        var restartedRepo = Repo();
        var persisted = (await restartedRepo.LoadAsync(driver.DriverId))!;
        Assert.Equal(round, persisted.ReservationRound);

        var replay = await restartedRepo.TryReserveAsync(driver.DriverId, trip, reservation, loaded.Version, 9);
        Assert.Equal(ReserveOutcome.Replayed, replay.Outcome);
        Assert.Equal(round, replay.Driver!.ReservationRound);

        var mapped = DriverEventMapper.ForReserveResult(driver.DriverId, trip, reservation, replay);
        Assert.NotNull(mapped);
        Assert.Equal(round, mapped!.Payload.MatchingRound);

        // Historical row (no provenance) maps to field-absence, not null.
        var legacy = Driver.Create(Guid.NewGuid(), Guid.NewGuid());
        legacy.GoOnline(legacy.Version);
        legacy.Reserve(Guid.NewGuid(), Guid.NewGuid(), legacy.Version);
        var legacyEvent = DriverEventMapper.ForReserveResult(
            legacy.DriverId, legacy.ReservedTripId!.Value, legacy.ReservationId!.Value,
            ReserveResult.Won(legacy));
        Assert.NotNull(legacyEvent);
        Assert.Null(legacyEvent!.Payload.MatchingRound);
    }

    [Fact]
    public async Task Reservation_Generation_Is_Cleared_On_Release()
    {
        var repo = Repo();
        var driver = Driver.Create(Guid.NewGuid(), Guid.NewGuid());
        driver.GoOnline(driver.Version);
        await repo.TryInsertAsync(driver);
        var loaded = (await repo.LoadAsync(driver.DriverId))!;
        var trip = Guid.NewGuid();
        var reservation = Guid.NewGuid();
        var won = await repo.TryReserveAsync(driver.DriverId, trip, reservation, loaded.Version, 4);
        Assert.Equal(ReserveOutcome.Won, won.Outcome);
        Assert.Equal(4, won.Driver!.ReservationRound);

        var release = await repo.TryReleaseAsync(driver.DriverId, trip, reservation);
        Assert.Equal(DriverMutationOutcome.Applied, release.Outcome);

        var cleared = (await repo.LoadAsync(driver.DriverId))!;
        Assert.Equal(DriverStatus.Available, cleared.Status);
        Assert.Null(cleared.ReservationRound);
        Assert.Null(cleared.ReservationId);
    }

    [Fact]
    public async Task Save_Load_OnTrip_State()
    {
        var repo = Repo();
        var driver = Driver.Create(Guid.NewGuid(), Guid.NewGuid());
        driver.GoOnline(driver.Version);
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
