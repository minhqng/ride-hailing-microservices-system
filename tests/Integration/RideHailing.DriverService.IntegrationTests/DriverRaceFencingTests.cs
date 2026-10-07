// B-W07-01/02/03/04: exact release, stale-release fencing (CON-04), and
// Race B GoOffline-vs-Reserve (CON-02) on REAL PostgreSQL. Final DB rows are
// the oracle throughout: R2 (or the single legal winner) survives with exact
// version accounting, losers conflict without mutation.
using RideHailing.DriverService.Domain.Drivers;
using RideHailing.DriverService.Infrastructure.Persistence;
using Xunit.Abstractions;

namespace RideHailing.DriverService.IntegrationTests;

[Collection(DriverPostgresCollection.Name)]
public class DriverRaceFencingTests
{
    private readonly DriverPostgresDatabase _db;

    public DriverRaceFencingTests(ITestOutputHelper output, DriverPostgresDatabase db)
    {
        _db = db;
    }

    private DriverRepository Repo() => new(_db.ConnectionString);

    // Fixtures fail loud at collection init (no skip path on this base):
    // reaching a test body proves the database is real.

    private static async Task<Guid> SeedAvailableDriverAsync(DriverRepository repo)
    {
        var driver = Driver.Create(Guid.NewGuid(), Guid.NewGuid());
        driver.GoOnline(driver.Version);
        Assert.True(await repo.TryInsertAsync(driver));
        return driver.DriverId;
    }

    [Fact]
    public async Task Concurrent_GoOffline_Vs_Reserve_Exactly_One_Wins()
    {

        // CON-02 (Doc 03 §8, DSR-004): barrier-released pair from the same
        // AVAILABLE version. Allowed finals: OFFLINE or RESERVED — never a
        // stale overwrite, never both committed. Contenders run on pool
        // threads (Task.Run) so the blocking barrier cannot wedge the
        // Select/WhenAll enumeration on the test thread.
        var repo = Repo();
        var driverId = await SeedAvailableDriverAsync(repo);
        var start = (await repo.LoadAsync(driverId))!;
        var trip = Guid.NewGuid();
        var reservation = Guid.NewGuid();

        ThreadPool.GetMinThreads(out var minW, out var minC);
        ThreadPool.SetMinThreads(Math.Max(minW, 52), Math.Max(minC, 50));
        try
        {
            using var barrier = new Barrier(2);
            var offlineTask = Task.Run(async () =>
            {
                barrier.SignalAndWait(TimeSpan.FromSeconds(60));
                return await repo.TryGoOfflineAsync(driverId, start.Version);
            });
            var reserveTask = Task.Run(async () =>
            {
                barrier.SignalAndWait(TimeSpan.FromSeconds(60));
                return await repo.TryReserveAsync(driverId, trip, reservation, start.Version);
            });
            await Task.WhenAll(offlineTask, reserveTask);
            var offlineResult = await offlineTask;
            var reserveResult = await reserveTask;

            var reserveWon = reserveResult.Outcome == ReserveOutcome.Won;
            var offlineWon = offlineResult.Outcome == DriverMutationOutcome.Applied;
            Assert.True(reserveWon ^ offlineWon);

            var final = (await repo.LoadAsync(driverId))!;
            Assert.Equal(start.Version + 1, final.Version);
            // Race losers always observe STALE_VERSION: the winner committed
            // first, so the loser's fence value is stale whichever state
            // check would fire next (fence-before-state precedence). Both
            // are 409 domain conflicts — the conclusive branch CON-02 needs.
            if (reserveWon)
            {
                Assert.Equal(DriverStatus.Reserved, final.Status);
                Assert.Equal(trip, final.ReservedTripId);
                Assert.Equal(reservation, final.ReservationId);
                Assert.Equal(ReserveOutcome.Won, reserveResult.Outcome);
                Assert.Equal(DriverMutationOutcome.Conflict, offlineResult.Outcome);
                Assert.Equal(DriverErrors.StaleVersion, offlineResult.ConflictCode);
            }
            else
            {
                Assert.Equal(DriverStatus.Offline, final.Status);
                Assert.Null(final.ReservedTripId);
                Assert.Null(final.ReservationId);
                Assert.Equal(DriverMutationOutcome.Applied, offlineResult.Outcome);
                Assert.Equal(ReserveOutcome.Conflict, reserveResult.Outcome);
                Assert.Equal(DriverErrors.StaleVersion, reserveResult.ConflictCode);
            }
        }
        finally
        {
            ThreadPool.SetMinThreads(minW, minC);
        }
    }

    [Fact]
    public async Task Stale_Release_After_ReReserve_Cannot_Free_Newer_Owner()
    {

        // CON-04 (Doc 03 §8, DSI-004/033): R1 → release R1 → R2 → late
        // release R1 ⇒ R2 survives with exact version accounting.
        var repo = Repo();
        var driverId = await SeedAvailableDriverAsync(repo);
        var start = (await repo.LoadAsync(driverId))!;
        var t1 = Guid.NewGuid();
        var r1 = Guid.NewGuid();

        Assert.Equal(ReserveOutcome.Won, (await repo.TryReserveAsync(driverId, t1, r1, start.Version)).Outcome);
        var released = await repo.TryReleaseAsync(driverId, t1, r1);
        Assert.Equal(DriverMutationOutcome.Applied, released.Outcome);

        var mid = (await repo.LoadAsync(driverId))!;
        var t2 = Guid.NewGuid();
        var r2 = Guid.NewGuid();
        Assert.Equal(ReserveOutcome.Won, (await repo.TryReserveAsync(driverId, t2, r2, mid.Version)).Outcome);

        var late = await repo.TryReleaseAsync(driverId, t1, r1);
        Assert.Equal(DriverMutationOutcome.Conflict, late.Outcome);
        Assert.Equal(DriverErrors.StaleReservation, late.ConflictCode);

        var final = (await repo.LoadAsync(driverId))!;
        Assert.Equal(DriverStatus.Reserved, final.Status);
        Assert.Equal(t2, final.ReservedTripId);
        Assert.Equal(r2, final.ReservationId);
        Assert.Equal(start.Version + 3, final.Version);
    }

    [Fact]
    public async Task Release_Exact_Duplicate_And_Never_Held_Are_Idempotent()
    {

        var repo = Repo();
        var driverId = await SeedAvailableDriverAsync(repo);
        var start = (await repo.LoadAsync(driverId))!;
        var t = Guid.NewGuid();
        var r = Guid.NewGuid();
        Assert.Equal(ReserveOutcome.Won, (await repo.TryReserveAsync(driverId, t, r, start.Version)).Outcome);

        var first = await repo.TryReleaseAsync(driverId, t, r);
        Assert.Equal(DriverMutationOutcome.Applied, first.Outcome);
        var afterFirst = (await repo.LoadAsync(driverId))!;

        var duplicate = await repo.TryReleaseAsync(driverId, t, r);
        Assert.Equal(DriverMutationOutcome.Unchanged, duplicate.Outcome);
        var neverHeld = await repo.TryReleaseAsync(driverId, Guid.NewGuid(), Guid.NewGuid());
        Assert.Equal(DriverMutationOutcome.Unchanged, neverHeld.Outcome);

        var final = (await repo.LoadAsync(driverId))!;
        Assert.Equal(DriverStatus.Available, final.Status);
        Assert.Equal(afterFirst.Version, final.Version);
    }

    [Fact]
    public async Task Release_Stale_On_Live_Reservation_Conflicts_Without_Mutation()
    {

        var repo = Repo();
        var driverId = await SeedAvailableDriverAsync(repo);
        var start = (await repo.LoadAsync(driverId))!;
        var t = Guid.NewGuid();
        var r = Guid.NewGuid();
        Assert.Equal(ReserveOutcome.Won, (await repo.TryReserveAsync(driverId, t, r, start.Version)).Outcome);

        var stale = await repo.TryReleaseAsync(driverId, Guid.NewGuid(), Guid.NewGuid());
        Assert.Equal(DriverMutationOutcome.Conflict, stale.Outcome);
        Assert.Equal(DriverErrors.StaleReservation, stale.ConflictCode);

        var final = (await repo.LoadAsync(driverId))!;
        Assert.Equal(DriverStatus.Reserved, final.Status);
        Assert.Equal(r, final.ReservationId);
        Assert.Equal(start.Version + 1, final.Version);
    }

    [Fact]
    public async Task State_Branches_Conflict_With_Current_Version()
    {

        // Deterministic complement to the race: with a CURRENT version, the
        // state legality branch (not the fence) decides, proving both codes.
        var repo = Repo();
        var driverId = await SeedAvailableDriverAsync(repo);
        var start = (await repo.LoadAsync(driverId))!;
        var t = Guid.NewGuid();
        var r = Guid.NewGuid();
        Assert.Equal(ReserveOutcome.Won, (await repo.TryReserveAsync(driverId, t, r, start.Version)).Outcome);

        var current = (await repo.LoadAsync(driverId))!;
        var offline = await repo.TryGoOfflineAsync(driverId, current.Version);
        Assert.Equal(DriverMutationOutcome.Conflict, offline.Outcome);
        Assert.Equal(DriverErrors.ReservationActive, offline.ConflictCode);

        var second = await SeedAvailableDriverAsync(repo);
        var off = await repo.TryGoOfflineAsync(second, (await repo.LoadAsync(second))!.Version);
        Assert.Equal(DriverMutationOutcome.Applied, off.Outcome);
        var reserve = await repo.TryReserveAsync(
            second, Guid.NewGuid(), Guid.NewGuid(), (await repo.LoadAsync(second))!.Version);
        Assert.Equal(ReserveOutcome.Conflict, reserve.Outcome);
        Assert.Equal(DriverErrors.DriverNotAvailable, reserve.ConflictCode);
    }

    [Fact]
    public async Task GoOffline_Stale_Version_Fenced_At_Persistence()
    {

        var repo = Repo();
        var driverId = await SeedAvailableDriverAsync(repo);
        var start = (await repo.LoadAsync(driverId))!;

        // Newer authority act first (any mutation bumps the version).
        var bump = await repo.TryReserveAsync(driverId, Guid.NewGuid(), Guid.NewGuid(), start.Version);
        Assert.Equal(ReserveOutcome.Won, bump.Outcome);
        await repo.TryReleaseAsync(driverId, bump.Driver!.ReservedTripId!.Value, bump.Driver.ReservationId!.Value);

        var stale = await repo.TryGoOfflineAsync(driverId, start.Version);
        Assert.Equal(DriverMutationOutcome.Conflict, stale.Outcome);
        Assert.Equal(DriverErrors.StaleVersion, stale.ConflictCode);

        var final = (await repo.LoadAsync(driverId))!;
        Assert.Equal(DriverStatus.Available, final.Status);
    }

    [Fact]
    public async Task GoOnline_Stale_Version_Cannot_Overwrite_Reservation()
    {

        // The lost-reservation shape via the online path: a stale online
        // object must not overwrite a newer RESERVED row.
        var repo = Repo();
        var driverId = await SeedAvailableDriverAsync(repo);
        var start = (await repo.LoadAsync(driverId))!;
        var t = Guid.NewGuid();
        var r = Guid.NewGuid();
        Assert.Equal(ReserveOutcome.Won, (await repo.TryReserveAsync(driverId, t, r, start.Version)).Outcome);

        var stale = await repo.TryGoOnlineAsync(driverId, start.Version);
        Assert.Equal(DriverMutationOutcome.Conflict, stale.Outcome);

        var final = (await repo.LoadAsync(driverId))!;
        Assert.Equal(DriverStatus.Reserved, final.Status);
        Assert.Equal(r, final.ReservationId);
    }

    [Theory]
    [InlineData(DriverStatus.Available, ReservationQueryOutcome.NotCommittedOrAvailable)]
    [InlineData(DriverStatus.Offline, ReservationQueryOutcome.Offline)]
    [InlineData(DriverStatus.OnTrip, ReservationQueryOutcome.OnTrip)]
    public async Task Query_Reservation_Reports_Current_Truth(
        DriverStatus setup, ReservationQueryOutcome expected)
    {

        var repo = Repo();
        var driver = Driver.Create(Guid.NewGuid(), Guid.NewGuid());
        Assert.True(await repo.TryInsertAsync(driver));
        var probe = Guid.NewGuid();
        if (setup == DriverStatus.Available)
        {
            var online = await repo.TryGoOnlineAsync(driver.DriverId, 1);
            Assert.Equal(DriverMutationOutcome.Applied, online.Outcome);
        }
        else if (setup == DriverStatus.OnTrip)
        {
            var online = await repo.TryGoOnlineAsync(driver.DriverId, 1);
            Assert.Equal(DriverMutationOutcome.Applied, online.Outcome);
            var loaded = (await repo.LoadAsync(driver.DriverId))!;
            var t = Guid.NewGuid();
            var r = Guid.NewGuid();
            Assert.Equal(ReserveOutcome.Won, (await repo.TryReserveAsync(driver.DriverId, t, r, loaded.Version)).Outcome);
            var reserved = (await repo.LoadAsync(driver.DriverId))!;
            reserved.MarkTripStarted(t, r);
            await repo.SaveAsync(reserved);
        }

        var query = await repo.QueryReservationAsync(driver.DriverId, probe);
        Assert.Equal(expected, query.Outcome);
        Assert.NotNull(query.Version);
    }

    [Fact]
    public async Task Query_Reservation_Owned_And_Foreign()
    {

        var repo = Repo();
        var driverId = await SeedAvailableDriverAsync(repo);
        var start = (await repo.LoadAsync(driverId))!;
        var t = Guid.NewGuid();
        var r = Guid.NewGuid();
        Assert.Equal(ReserveOutcome.Won, (await repo.TryReserveAsync(driverId, t, r, start.Version)).Outcome);

        var owned = await repo.QueryReservationAsync(driverId, r);
        Assert.Equal(ReservationQueryOutcome.OwnedByThisReservation, owned.Outcome);

        var foreign = await repo.QueryReservationAsync(driverId, Guid.NewGuid());
        Assert.Equal(ReservationQueryOutcome.ReservedByOther, foreign.Outcome);
    }

    [Fact]
    public async Task Query_Reservation_Unknown_Driver_Returns_NotFound()
    {

        var query = await Repo().QueryReservationAsync(Guid.NewGuid(), Guid.NewGuid());
        Assert.Equal(ReservationQueryOutcome.NotFound, query.Outcome);
        Assert.Null(query.Version);
    }
}
