// Atomic dispatch-reconcile fence: an UNKNOWN attempt (or IN_FLIGHT with a
// durable dispatch fence) is NEVER dispatched; it is reconciled against Driver
// authority first.
//
// The historical race (a cached negative conclusion racing a newly authorized
// dispatch of the SAME ambiguous attempt) required replaying an attempt that
// may already have committed. That replay is now structurally prevented. These
// tests prove the replacement invariants with REAL PostgreSQL on both sides
// (matching_db attempt/round rows, driver_db reservation truth):
//   1. concurrent workers on an UNKNOWN attempt issue zero version reads and
//      zero reserve sends, and atomically converge on one conclusive result;
//   2. a terminal attempt denies late execution before any Driver call, while
//      the next candidate still advances normally.
// The stub IDriverClient only schedules the HTTP boundary; every
// commit/version oracle is a real DriverRepository read. No sleeps.
using RideHailing.DriverService.Domain.Drivers;
using RideHailing.DriverService.Infrastructure.Persistence;
using RideHailing.MatchingService.Application.Reserve;
using RideHailing.MatchingService.Infrastructure.DriverClient;
using RideHailing.MatchingService.Infrastructure.Persistence;
using Xunit.Abstractions;

namespace RideHailing.MatchingService.IntegrationTests;

[Collection(ReserveWorkflowCollection.Name)]
public sealed class AtomicDispatchFenceTests
{
    private readonly ITestOutputHelper _out;
    private readonly MatchingPostgresDatabase _matching;
    private readonly DriverTestDatabase _drivers;

    public AtomicDispatchFenceTests(
        ITestOutputHelper output,
        MatchingPostgresDatabase matching,
        DriverTestDatabase drivers)
    {
        _out = output;
        _matching = matching;
        _drivers = drivers;
    }

    private MatchingAttemptRepository Attempts() => new(_matching.ConnectionString);

    private MatchingReserveOrchestrator Orchestrator(StubDriverClient stub) =>
        new(Attempts(), stub, new MatchingTerminalizer(_matching.DataSource, _matching.OutboxStore));

    private static Guid NewTrip() => Guid.NewGuid();

    private async Task<Guid> SeedAvailableDriverAsync()
    {
        var repo = _drivers.CreateRepository();
        var driver = Driver.Create(Guid.NewGuid(), Guid.NewGuid());
        driver.GoOnline(driver.Version);
        Assert.True(await repo.TryInsertAsync(driver));
        return driver.DriverId;
    }

    private sealed class StubDriverClient : IDriverClient
    {
        public Func<Guid, CancellationToken, Task<DriverVersionRead?>>? OnVersion;
        public Func<DriverReserveRequest, CancellationToken, Task<DriverReserveResult>>? OnReserve;
        public Func<Guid, Guid, CancellationToken, Task<ReservationReconcileResult>>? OnQuery;

        public int ReserveSends;
        public List<int> ReserveVersions = new();

        public Task<DriverVersionRead?> TryGetDriverVersionAsync(Guid driverId, CancellationToken ct = default) =>
            OnVersion is not null
                ? OnVersion(driverId, ct)
                : Task.FromResult<DriverVersionRead?>(new DriverVersionRead(2, "AVAILABLE"));

        public Task<DriverReserveResult> ReserveAsync(DriverReserveRequest request, CancellationToken ct = default)
        {
            Interlocked.Increment(ref ReserveSends);
            lock (ReserveVersions)
            {
                ReserveVersions.Add(request.ExpectedVersion);
            }

            return OnReserve is not null
                ? OnReserve(request, ct)
                : Task.FromResult(new DriverReserveResult(DriverReserveOutcomeKind.Reserved, null, request.ExpectedVersion + 1));
        }

        public Task<ReservationReconcileResult> GetReservationAsync(Guid driverId, Guid reservationId, CancellationToken ct = default) =>
            OnQuery is not null
                ? OnQuery(driverId, reservationId, ct)
                : Task.FromResult(new ReservationReconcileResult(ReservationReconcileOutcome.NotCommittedOrAvailable, 2));
    }

    private static ReservationReconcileResult MapResult(ReservationQuery q) => new(
        q.Outcome switch
        {
            ReservationQueryOutcome.OwnedByThisReservation => ReservationReconcileOutcome.OwnedByThisReservation,
            ReservationQueryOutcome.NotCommittedOrAvailable => ReservationReconcileOutcome.NotCommittedOrAvailable,
            ReservationQueryOutcome.ReservedByOther => ReservationReconcileOutcome.ReservedByOther,
            ReservationQueryOutcome.Offline => ReservationReconcileOutcome.Offline,
            ReservationQueryOutcome.OnTrip => ReservationReconcileOutcome.OnTrip,
            _ => ReservationReconcileOutcome.NotFound,
        },
        q.Version);

    private static async Task<DriverReserveResult> RealReserveAsync(
        DriverRepository repo, DriverReserveRequest req, CancellationToken ct = default)
    {
        var outcome = await repo.TryReserveAsync(req.DriverId, req.TripId, req.ReservationId, req.ExpectedVersion, req.MatchingRound, ct);
        return outcome.Outcome switch
        {
            ReserveOutcome.Won => new DriverReserveResult(DriverReserveOutcomeKind.Reserved, null, outcome.Driver?.Version),
            ReserveOutcome.Replayed => new DriverReserveResult(DriverReserveOutcomeKind.Reserved, null, outcome.Driver?.Version),
            _ => new DriverReserveResult(
                DriverReserveOutcomeKind.Rejected, outcome.ConflictCode ?? "DRIVER_NOT_AVAILABLE", null),
        };
    }

    /// <summary>
    /// Puts R1 into UNKNOWN with a durable fence at the then-current Driver
    /// version, then advances the Driver version twice (Offline/Online, still
    /// AVAILABLE) so the old fence is provably stale. Returns (trip, d1, d2,
    /// r1, v1, v2).
    /// </summary>
    private async Task<(Guid Trip, Guid D1, Guid D2, Guid R1, int V1, int V2)> SetupStaleFenceAsync()
    {
        var trip = NewTrip();
        var d1 = await SeedAvailableDriverAsync();
        var d2 = await SeedAvailableDriverAsync();
        var driverRepo = _drivers.CreateRepository();

        var seed = await driverRepo.LoadAsync(d1);
        var v1 = seed!.Version;

        var lossy = new StubDriverClient
        {
            OnVersion = async (id, ct) =>
            {
                var d = await driverRepo.LoadAsync(id, ct);
                return d is null ? null : new DriverVersionRead(d.Version, d.Status.ToString().ToUpperInvariant());
            },
            OnReserve = (_, _) => throw new TimeoutException("injected response loss before commit"),
            OnQuery = async (id, rid, ct) => MapResult(await driverRepo.QueryReservationAsync(id, rid, ct)),
        };

        var blocked = await Orchestrator(lossy).ExecuteRoundAsync(trip, 1, [d1, d2], Guid.NewGuid().ToString("D"));
        Assert.Equal(RoundOutcome.BlockedOnUnknown, blocked.Outcome);
        var attempt = Assert.Single(await Attempts().LoadAttemptsAsync(trip, 1));
        Assert.Equal(MatchingAttemptStates.Unknown, attempt.State);
        var r1 = attempt.ReservationId;
        Assert.Equal(v1, attempt.MaxDispatchedVersion);

        // Unrelated version bump, driver stays AVAILABLE: old R(V1) is stale.
        Assert.Equal(DriverMutationOutcome.Applied, (await driverRepo.TryGoOfflineAsync(d1, v1)).Outcome);
        var mid = await driverRepo.LoadAsync(d1);
        Assert.Equal(DriverMutationOutcome.Applied, (await driverRepo.TryGoOnlineAsync(d1, mid!.Version)).Outcome);
        var bumped = await driverRepo.LoadAsync(d1);
        Assert.Equal(DriverStatus.Available, bumped!.Status);
        var v2 = bumped.Version;
        Assert.True(v2 > v1);

        var staleLate = await driverRepo.TryReserveAsync(d1, Guid.NewGuid(), Guid.NewGuid(), v1);
        Assert.Equal(ReserveOutcome.Conflict, staleLate.Outcome);

        _out.WriteLine($"setup: trip={trip:N} d1={d1:N} d2={d2:N} r1={r1:N} v1={v1} v2={v2}");
        return (trip, d1, d2, r1, v1, v2);
    }

    [Fact]
    public async Task UnknownAttempt_IsNeverDispatched_ConcurrentWorkersConcludeOnce()
    {
        // Setup: R1 is UNKNOWN with a durable fence at V1, and the Driver has
        // since advanced to AVAILABLE@V2. Two workers execute the SAME
        // attempt concurrently. Neither may read a version or send a
        // Reserve; both must converge on the single authoritative negative.
        var (trip, d1, d2, _, v1, v2) = await SetupStaleFenceAsync();
        var driverRepo = _drivers.CreateRepository();
        var versionReads = 0;
        var reserveSends = 0;
        var queries = 0;

        var stub = new StubDriverClient
        {
            OnVersion = (_, _) =>
            {
                Interlocked.Increment(ref versionReads);
                return Task.FromResult<DriverVersionRead?>(new DriverVersionRead(v2, "AVAILABLE"));
            },
            OnReserve = (_, _) =>
            {
                Interlocked.Increment(ref reserveSends);
                throw new InvalidOperationException("an ambiguous attempt must never be dispatched");
            },
            OnQuery = async (id, rid, ct) =>
            {
                Interlocked.Increment(ref queries);
                return MapResult(await driverRepo.QueryReservationAsync(id, rid, ct));
            },
        };

        using var barrier = new Barrier(2);
        var runs = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
        {
            barrier.SignalAndWait(TimeSpan.FromSeconds(30));
            return await Orchestrator(stub).ExecuteAttemptAsync(trip, 1, 0, d1);
        })));

        Assert.All(runs, r =>
        {
            Assert.Equal(AttemptOutcome.Rejected, r.Outcome);
            Assert.Equal("NOT_COMMITTED_OR_AVAILABLE", r.Code);
        });
        Assert.Equal(0, versionReads);
        Assert.Equal(0, reserveSends);
        Assert.True(queries >= 1);

        var row = Assert.Single(await Attempts().LoadAttemptsAsync(trip, 1));
        Assert.Equal(MatchingAttemptStates.Rejected, row.State);
        Assert.Equal("NOT_COMMITTED_OR_AVAILABLE", row.LastErrorCode);
        Assert.Equal(v1, row.MaxDispatchedVersion);

        // No authoritative Driver effect anywhere: D1 is free at V2, D2 is
        // untouched, and the stale fence proves R1 cannot commit later.
        var d1After = await driverRepo.LoadAsync(d1);
        Assert.Equal(DriverStatus.Available, d1After!.Status);
        Assert.Equal(v2, d1After.Version);
        Assert.Null(d1After.ReservationId);
        var d2After = await driverRepo.LoadAsync(d2);
        Assert.Equal(DriverStatus.Available, d2After!.Status);
        Assert.Null(d2After.ReservationId);
    }

    [Fact]
    public async Task RejectedAttempt_LateExecutionDeniedBeforeAnyDriverCall_NextCandidateAdvances()
    {
        // R1 is terminally REJECTED by valid fenced proof. A late worker that
        // resumes the same attempt must not read a version, query, or send;
        // it reports the durable terminal. D2 then advances normally, so the
        // repair does not conservatively block every legitimate rejection.
        var (trip, d1, d2, r1, _, v2) = await SetupStaleFenceAsync();
        var driverRepo = _drivers.CreateRepository();

        var live = new StubDriverClient
        {
            OnVersion = async (id, ct) =>
            {
                var d = await driverRepo.LoadAsync(id, ct);
                return d is null ? null : new DriverVersionRead(d.Version, d.Status.ToString().ToUpperInvariant());
            },
            OnReserve = (req, ct) => RealReserveAsync(driverRepo, req, ct),
            OnQuery = async (id, rid, ct) => MapResult(await driverRepo.QueryReservationAsync(id, rid, ct)),
        };
        var aResult = await Orchestrator(live).ReconcileAsync(trip, 1, 0);
        Assert.Equal(AttemptOutcome.Rejected, aResult.Outcome);
        Assert.Equal(r1, aResult.ReservationId);
        var rejected = await Attempts().LoadAttemptAsync(trip, 1, 0);
        Assert.Equal(MatchingAttemptStates.Rejected, rejected!.State);

        var lateVersionReads = 0;
        var lateSends = 0;
        var late = new StubDriverClient
        {
            OnVersion = (_, _) =>
            {
                Interlocked.Increment(ref lateVersionReads);
                throw new InvalidOperationException("terminal attempts must not read a version");
            },
            OnReserve = (_, _) =>
            {
                Interlocked.Increment(ref lateSends);
                throw new InvalidOperationException("terminal attempts must not dispatch");
            },
            OnQuery = (_, _, _) => throw new InvalidOperationException("terminal attempts must not query"),
        };
        var bResult = await Orchestrator(late).ExecuteAttemptAsync(trip, 1, 0, d1);
        Assert.Equal(AttemptOutcome.Rejected, bResult.Outcome);
        Assert.Equal(r1, bResult.ReservationId);
        Assert.Equal(0, lateVersionReads);
        Assert.Equal(0, lateSends);

        var d1After = await driverRepo.LoadAsync(d1);
        Assert.Equal(DriverStatus.Available, d1After!.Status);
        Assert.Equal(v2, d1After.Version);
        Assert.Null(d1After.ReservationId);

        // R1 stays terminal; D2 advances normally (single winner).
        var d2Attempt = await Orchestrator(live).ExecuteAttemptAsync(trip, 1, 1, d2);
        Assert.Equal(AttemptOutcome.Reserved, d2Attempt.Outcome);
        var finalD2 = await driverRepo.LoadAsync(d2);
        Assert.Equal(DriverStatus.Reserved, finalD2!.Status);
        Assert.Equal(d2Attempt.ReservationId, finalD2.ReservationId);
        var finalD1 = await driverRepo.LoadAsync(d1);
        Assert.Equal(DriverStatus.Available, finalD1!.Status);
    }
}
