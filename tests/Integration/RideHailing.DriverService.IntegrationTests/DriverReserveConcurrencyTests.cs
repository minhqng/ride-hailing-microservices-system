// B-W06-05/06/07/08/09: CON-01 contention proof on REAL PostgreSQL
// (Doc 03 §8, DSI-001/005). N independent contenders race one AVAILABLE
// driver through a barrier; final Driver DB state is the oracle (never HTTP
// counts): exactly one current reservation, winner among contenders, version
// advanced exactly once, N-1 domain conflicts, zero system errors.
// Per-contender timings are recorded as first E01 data (observation only).
using System.Diagnostics;
using Npgsql;
using RideHailing.DriverService.Domain.Drivers;
using RideHailing.DriverService.Infrastructure.Persistence;
using Xunit.Abstractions;

namespace RideHailing.DriverService.IntegrationTests;

[Collection(DriverPostgresCollection.Name)]
public class DriverReserveConcurrencyTests
{
    private readonly ITestOutputHelper _out;
    private readonly DriverPostgresDatabase _db;

    public DriverReserveConcurrencyTests(ITestOutputHelper output, DriverPostgresDatabase db)
    {
        _out = output;
        _db = db;
    }

    private DriverRepository Repo()
    {
        // Testcontainers PostgreSQL caps server connections (max_connections
        // = 100, minus superuser reserve). Keep the client pool well below
        // that so N=100 synchronized contenders queue client-side instead of
        // tripping 53300: queuing does not weaken the proof (the row lock
        // still serializes all contenders; exactly one commits first).
        // Pool Timeout stays at the Npgsql default (30 s), far above a tx.
        var cs = _db.ConnectionString;
        if (!cs.Contains("Maximum Pool Size", StringComparison.OrdinalIgnoreCase))
        {
            cs += ";Maximum Pool Size=40";
        }

        return new DriverRepository(cs);
    }

    private void RequireDb()
    {
        // Fixture is fail-loud: any infrastructure failure already fails every
        // test via InitializeAsync. No skip path remains.
        Assert.False(string.IsNullOrWhiteSpace(_db.ConnectionString));
    }

    private static async Task<Guid> SeedAvailableDriverAsync(DriverRepository repo)
    {
        var driver = Driver.Create(Guid.NewGuid(), Guid.NewGuid());
        driver.GoOnline(driver.Version);
        Assert.True(await repo.TryInsertAsync(driver));
        return driver.DriverId;
    }

    private sealed record AttemptResult(int Index, ReserveResult Result, long ElapsedMs, Exception? Error);

    [Theory]
    [InlineData(2)]
    [InlineData(10)]
    [InlineData(50)]
    [InlineData(100)]
    public async Task Concurrent_Distinct_Reservations_Exactly_One_Winner(int n)
    {
        RequireDb();
        // Barrier blocks one thread per contender; pre-grow the pool so the
        // overlap is real instead of threadpool-ramp fiction (no sleeps).
        ThreadPool.GetMinThreads(out var minW, out var minC);
        ThreadPool.SetMinThreads(Math.Max(minW, n + 50), Math.Max(minC, 50));
        try
        {
            await RunConcurrentDistinctReservationsAsync(n);
        }
        finally
        {
            ThreadPool.SetMinThreads(minW, minC);
        }
    }

    private async Task RunConcurrentDistinctReservationsAsync(int n)
    {
        RequireDb();
        var repo = Repo();
        var driverId = await SeedAvailableDriverAsync(repo);
        var start = (await repo.LoadAsync(driverId))!;
        Assert.Equal(DriverStatus.Available, start.Status);

        using var barrier = new Barrier(n);
        var total = Stopwatch.StartNew();
        // Task.Run per contender: SignalAndWait blocks, so each contender
        // needs its own pool thread BEFORE anyone waits. (Starting the
        // blocking body inline during Select/WhenAll enumeration would wedge
        // enumeration itself on the first contender.)
        var attempts = await Task.WhenAll(Enumerable.Range(0, n).Select(i => Task.Run(async () =>
        {
            var trip = Guid.NewGuid();
            var reservation = Guid.NewGuid();
            var sw = Stopwatch.StartNew();
            try
            {
                if (!barrier.SignalAndWait(TimeSpan.FromSeconds(60)))
                {
                    return new AttemptResult(i, ReserveResult.Conflict("BARRIER_TIMEOUT", "barrier did not release"), 0, new TimeoutException("barrier"));
                }

                var result = await repo.TryReserveAsync(driverId, trip, reservation, start.Version);
                sw.Stop();
                return new AttemptResult(i, result, sw.ElapsedMilliseconds, null);
            }
            catch (Exception ex)
            {
                sw.Stop();
                return new AttemptResult(i, ReserveResult.Conflict("SYSTEM_ERROR", ex.GetType().Name), sw.ElapsedMilliseconds, ex);
            }
        })));
        total.Stop();

        var won = attempts.Where(a => a.Result.Outcome == ReserveOutcome.Won).ToList();
        var conflicts = attempts.Where(a => a.Result.Outcome == ReserveOutcome.Conflict).ToList();
        var systemErrors = attempts.Where(a => a.Error is not null).ToList();
        var elapsed = attempts.Select(a => a.ElapsedMs).OrderBy(x => x).ToList();
        _out.WriteLine(
            $"E01-FIRST-DATA N={n} wallMs={total.ElapsedMilliseconds} won={won.Count} " +
            $"conflicts={conflicts.Count} errors={systemErrors.Count} " +
            $"attemptMs_min={elapsed.First()} p50={elapsed[elapsed.Count / 2]} max={elapsed.Last()} " +
            $"poolMax=40 (client-side queue; row lock still serializes all contenders)");
        foreach (var e in systemErrors)
        {
            _out.WriteLine($"SYSTEM_ERROR contender={e.Index} {e.Error}");
        }

        // Oracle on authoritative final state (Doc 03 §1.2): exactly one
        // current reservation, winner among contenders, single version step.
        Assert.Empty(systemErrors);
        Assert.Single(won);
        Assert.Equal(n - 1, conflicts.Count);
        Assert.All(conflicts, c => Assert.NotEqual("BARRIER_TIMEOUT", c.Result.ConflictCode));

        var final = (await repo.LoadAsync(driverId))!;
        Assert.Equal(DriverStatus.Reserved, final.Status);
        Assert.Equal(start.Version + 1, final.Version);
        Assert.Equal(won[0].Result.Driver!.ReservedTripId, final.ReservedTripId);
        Assert.Equal(won[0].Result.Driver!.ReservationId, final.ReservationId);

        await using var conn = new NpgsqlConnection(_db.ConnectionString);
        await conn.OpenAsync();
        await using var count = new NpgsqlCommand(
            "SELECT COUNT(*) FROM drivers WHERE driver_id = @id AND status = 'RESERVED'", conn);
        count.Parameters.AddWithValue("id", driverId);
        Assert.Equal(1L, (long)(await count.ExecuteScalarAsync())!);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(10)]
    public async Task Concurrent_Same_Reservation_Replay_Single_Effect(int n)
    {
        RequireDb();
        // Same prologue as the distinct-reservation theory: Barrier blocks one
        // thread per contender, and a timed-out barrier must fail labeled.
        ThreadPool.GetMinThreads(out var minW, out var minC);
        ThreadPool.SetMinThreads(Math.Max(minW, n + 50), Math.Max(minC, 50));
        try
        {
            await RunConcurrentSameReservationReplayAsync(n);
        }
        finally
        {
            ThreadPool.SetMinThreads(minW, minC);
        }
    }

    private async Task RunConcurrentSameReservationReplayAsync(int n)
    {
        RequireDb();
        var repo = Repo();
        var driverId = await SeedAvailableDriverAsync(repo);
        var start = (await repo.LoadAsync(driverId))!;
        var trip = Guid.NewGuid();
        var reservation = Guid.NewGuid();

        using var barrier = new Barrier(n);
        // Same Task.Run rationale as the distinct-reservation theory: the
        // blocking SignalAndWait must run on pool threads, never inline
        // during Select/WhenAll enumeration on the test thread.
        var attempts = await Task.WhenAll(Enumerable.Range(0, n).Select(_ => Task.Run(async () =>
        {
            try
            {
                if (!barrier.SignalAndWait(TimeSpan.FromSeconds(60)))
                {
                    return ReserveResult.Conflict("BARRIER_TIMEOUT", "barrier did not release");
                }

                return await repo.TryReserveAsync(driverId, trip, reservation, start.Version);
            }
            catch (Exception ex)
            {
                return ReserveResult.Conflict("SYSTEM_ERROR", ex.GetType().Name);
            }
        })));

        // Same logical effect retried: every caller observes canonical success
        // (DSR-002), but the database holds exactly one effect (DSI-005).
        Assert.All(attempts, a => Assert.True(
            a.Outcome is ReserveOutcome.Won or ReserveOutcome.Replayed,
            $"unexpected {a.Outcome} ({a.ConflictCode})"));
        Assert.Single(attempts, a => a.Outcome == ReserveOutcome.Won);

        var final = (await repo.LoadAsync(driverId))!;
        Assert.Equal(DriverStatus.Reserved, final.Status);
        Assert.Equal(trip, final.ReservedTripId);
        Assert.Equal(reservation, final.ReservationId);
        Assert.Equal(start.Version + 1, final.Version);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(11)]
    public async Task Mixed_Same_And_Distinct_Reservations_Single_Winner(int sameCount)
    {
        RequireDb();
        // Interleaving the two pure theories omit: N same-(T,R) replays plus
        // one distinct contender, one shared version, one barrier. Exactly one
        // logical effect may win; every same-R caller either shares the win
        // (replay) or loses to the distinct winner (conflict) — never a
        // second effect, never a resurrect.
        const int distinctCount = 1;
        var n = sameCount + distinctCount;
        var repo = Repo();
        var driverId = await SeedAvailableDriverAsync(repo);
        var start = (await repo.LoadAsync(driverId))!;
        var trip = Guid.NewGuid();
        var reservation = Guid.NewGuid();
        var otherTrip = Guid.NewGuid();
        var otherR = Guid.NewGuid();

        ThreadPool.GetMinThreads(out var minW, out var minC);
        ThreadPool.SetMinThreads(Math.Max(minW, n + 50), Math.Max(minC, 50));
        ReserveResult[] attempts;
        try
        {
            using var barrier = new Barrier(n);
            attempts = await Task.WhenAll(Enumerable.Range(0, n).Select(i => Task.Run(async () =>
            {
                try
                {
                    if (!barrier.SignalAndWait(TimeSpan.FromSeconds(60)))
                    {
                        return ReserveResult.Conflict("BARRIER_TIMEOUT", "barrier did not release");
                    }

                    return i < sameCount
                        ? await repo.TryReserveAsync(driverId, trip, reservation, start.Version)
                        : await repo.TryReserveAsync(driverId, otherTrip, otherR, start.Version);
                }
                catch (Exception ex)
                {
                    return ReserveResult.Conflict("SYSTEM_ERROR", ex.GetType().Name);
                }
            })));
        }
        finally
        {
            ThreadPool.SetMinThreads(minW, minC);
        }

        var won = attempts.Where(a => a.Outcome == ReserveOutcome.Won).ToList();
        Assert.Single(won);
        Assert.DoesNotContain(attempts, a => a.ConflictCode == "BARRIER_TIMEOUT");
        Assert.DoesNotContain(attempts, a => a.ConflictCode == "SYSTEM_ERROR");

        var final = (await repo.LoadAsync(driverId))!;
        Assert.Equal(DriverStatus.Reserved, final.Status);
        Assert.Equal(start.Version + 1, final.Version);
        if (final.ReservationId == reservation)
        {
            // Same-R won: every same-R caller replays it, the distinct loses.
            Assert.Equal(sameCount - 1, attempts.Count(a => a.Outcome == ReserveOutcome.Replayed));
            Assert.Equal(trip, final.ReservedTripId);
        }
        else
        {
            // Distinct won: no replay exists; every same-R caller conflicts.
            Assert.Equal(otherR, final.ReservationId);
            Assert.Empty(attempts.Where(a => a.Outcome == ReserveOutcome.Replayed));
            Assert.Equal(n - 1, attempts.Count(a => a.Outcome == ReserveOutcome.Conflict));
        }
    }

    [Fact]
    public async Task Stale_Version_Loses_To_Committed_Winner()
    {
        RequireDb();
        var repo = Repo();
        var driverId = await SeedAvailableDriverAsync(repo);
        var start = (await repo.LoadAsync(driverId))!;

        var winnerTrip = Guid.NewGuid();
        var winnerR = Guid.NewGuid();
        var won = await repo.TryReserveAsync(driverId, winnerTrip, winnerR, start.Version);
        Assert.Equal(ReserveOutcome.Won, won.Outcome);

        // Same stale version, different reservation: fenced, row untouched.
        var stale = await repo.TryReserveAsync(driverId, Guid.NewGuid(), Guid.NewGuid(), start.Version);
        Assert.Equal(ReserveOutcome.Conflict, stale.Outcome);
        Assert.Equal(DriverErrors.StaleVersion, stale.ConflictCode);

        var final = (await repo.LoadAsync(driverId))!;
        Assert.Equal(winnerTrip, final.ReservedTripId);
        Assert.Equal(winnerR, final.ReservationId);
        Assert.Equal(start.Version + 1, final.Version);
    }

    [Fact]
    public async Task Different_Reservation_On_Reserved_Conflicts_Without_Mutation()
    {
        RequireDb();
        var repo = Repo();
        var driverId = await SeedAvailableDriverAsync(repo);
        var start = (await repo.LoadAsync(driverId))!;
        var t = Guid.NewGuid();
        var r = Guid.NewGuid();
        Assert.Equal(ReserveOutcome.Won, (await repo.TryReserveAsync(driverId, t, r, start.Version)).Outcome);

        var current = (await repo.LoadAsync(driverId))!;
        var conflict = await repo.TryReserveAsync(driverId, Guid.NewGuid(), Guid.NewGuid(), current.Version);
        Assert.Equal(ReserveOutcome.Conflict, conflict.Outcome);
        Assert.Equal(DriverErrors.DriverNotAvailable, conflict.ConflictCode);

        var final = (await repo.LoadAsync(driverId))!;
        Assert.Equal(t, final.ReservedTripId);
        Assert.Equal(r, final.ReservationId);
        Assert.Equal(current.Version, final.Version);
    }

    [Fact]
    public async Task Reserve_Identity_Held_By_Another_Driver_Conflicts_Without_Mutation()
    {
        RequireDb();
        var repo = Repo();

        // Driver A holds R1.
        var driverA = await SeedAvailableDriverAsync(repo);
        var startA = (await repo.LoadAsync(driverA))!;
        var tripA = Guid.NewGuid();
        var sharedR = Guid.NewGuid();
        Assert.Equal(ReserveOutcome.Won, (await repo.TryReserveAsync(driverA, tripA, sharedR, startA.Version)).Outcome);

        // Same reservation identity presented for a DIFFERENT driver: the
        // partial unique index backstops the row lock; B is untouched.
        var driverB = await SeedAvailableDriverAsync(repo);
        var startB = (await repo.LoadAsync(driverB))!;
        var crossed = await repo.TryReserveAsync(driverB, Guid.NewGuid(), sharedR, startB.Version);
        Assert.Equal(ReserveOutcome.Conflict, crossed.Outcome);
        Assert.Equal(DriverErrors.DriverNotAvailable, crossed.ConflictCode);

        var finalB = (await repo.LoadAsync(driverB))!;
        Assert.Equal(DriverStatus.Available, finalB.Status);
        Assert.Equal(startB.Version, finalB.Version);
        Assert.Null(finalB.ReservationId);
    }

    [Fact]
    public async Task Reserve_Unknown_Driver_Returns_NotFound()
    {
        RequireDb();
        var result = await Repo().TryReserveAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1);
        Assert.Equal(ReserveOutcome.DriverNotFound, result.Outcome);
    }

    [Fact]
    public async Task Reserve_Empty_Identity_Rejected_Before_Database()
    {
        RequireDb();
        var repo = Repo();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            repo.TryReserveAsync(Guid.Empty, Guid.NewGuid(), Guid.NewGuid(), 1));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            repo.TryReserveAsync(Guid.NewGuid(), Guid.Empty, Guid.NewGuid(), 1));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            repo.TryReserveAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.Empty, 1));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            repo.TryReserveAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 0));
    }

    [Fact]
    public async Task Reserve_Identity_Check_Rejects_Empty_Guid_At_Boundary()
    {
        RequireDb();
        // Persistence-boundary twin of the domain/API empty-identity rule:
        // a bypassed-domain RESERVED row with Guid.Empty fails loud (23514).
        await using var conn = new NpgsqlConnection(_db.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO drivers (driver_id, user_id, status, version, reserved_trip_id, reservation_id) " +
            "VALUES (@id, @u, 'RESERVED', 2, @t, '00000000-0000-0000-0000-000000000000')", conn);
        cmd.Parameters.AddWithValue("id", Guid.NewGuid());
        cmd.Parameters.AddWithValue("u", Guid.NewGuid());
        cmd.Parameters.AddWithValue("t", Guid.NewGuid());

        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Equal("23514", ex.SqlState);
    }

    [Fact]
    public async Task Migration_Reapply_Succeeds_On_Realistic_PreW6_Data()
    {
        RequireDb();
        // Upgrade shape: realistic rows (coherent RESERVED with true UUIDs,
        // plain AVAILABLE) exist, then the full ordered baseline incl. 003
        // re-applies cleanly and the rows survive unchanged.
        var repo = Repo();
        var reservedId = await SeedAvailableDriverAsync(repo);
        var before = (await repo.LoadAsync(reservedId))!;
        var t = Guid.NewGuid();
        var r = Guid.NewGuid();
        Assert.Equal(ReserveOutcome.Won, (await repo.TryReserveAsync(reservedId, t, r, before.Version)).Outcome);
        var plainId = await SeedAvailableDriverAsync(repo);

        await _db.ReapplyScriptsAsync();

        var reserved = (await repo.LoadAsync(reservedId))!;
        Assert.Equal(DriverStatus.Reserved, reserved.Status);
        Assert.Equal(t, reserved.ReservedTripId);
        Assert.Equal(r, reserved.ReservationId);
        Assert.Equal(DriverStatus.Available, (await repo.LoadAsync(plainId))!.Status);
    }
}
