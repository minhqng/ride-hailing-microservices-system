// W07 atomic dispatch-reconcile fence — two-worker race proof.
//
// Hypothesis under test: ReconcileAsync concludes UNKNOWN -> REJECTED from a
// CACHED max_dispatched_version while TryTransitionAsync only checks attempt
// state, and RecordDispatchAsync authorizes unconditionally (void). Two
// workers on the SAME attempt row can therefore produce the forbidden
// combination: conclusive REJECTED + an outstanding newly-authorized
// ReserveDriver(R) that commits afterwards.
//
// Both tests use REAL PostgreSQL on both sides (matching_db attempt/round
// rows, driver_db reservation truth). The stub IDriverClient only schedules
// the HTTP boundary via deterministic gates; every commit/version oracle is
// a real DriverRepository read. No sleeps.
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
        new(Attempts(), stub);

    private static Guid NewTrip() => Guid.NewGuid();

    private async Task<Guid> SeedAvailableDriverAsync()
    {
        var repo = new DriverRepository(_drivers.ConnectionString);
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
        var driverRepo = new DriverRepository(_drivers.ConnectionString);

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
    public async Task DispatchFence_Wins_StaleReconcileMustNotReject()
    {
        // Order under test: B records fence V2 (authorized, pre-send pause),
        // then A attempts a negative conclusion from its STALE cached fence
        // V1 while observing the same AVAILABLE@V2.
        //
        // Safe outcome: A cannot conclude (V2 > V1 is stale proof, V2 > V2 is
        // not); R1 stays unresolved; B's authorized dispatch commits; a
        // follow-up reconcile converges R1 to RESERVED. Exactly one durable
        // reservation for the trip (D1/R1); D2 untouched.
        var (trip, d1, d2, r1, v1, v2) = await SetupStaleFenceAsync();
        var driverRepo = new DriverRepository(_drivers.ConnectionString);

        var enteredA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var enteredB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stubAQueryCalls = 0;

        var stubA = new StubDriverClient
        {
            OnVersion = async (id, ct) =>
            {
                var d = await driverRepo.LoadAsync(id, ct);
                return d is null ? null : new DriverVersionRead(d.Version, d.Status.ToString().ToUpperInvariant());
            },
            OnReserve = (_, _) => throw new InvalidOperationException("reconcile path must not dispatch"),
            OnQuery = async (id, rid, ct) =>
            {
                if (Interlocked.Increment(ref stubAQueryCalls) == 1)
                {
                    enteredA.SetResult();
                    await gateA.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
                }

                return MapResult(await driverRepo.QueryReservationAsync(id, rid, ct));
            },
        };
        var stubB = new StubDriverClient
        {
            OnVersion = async (id, ct) =>
            {
                var d = await driverRepo.LoadAsync(id, ct);
                return d is null ? null : new DriverVersionRead(d.Version, d.Status.ToString().ToUpperInvariant());
            },
            OnReserve = async (req, ct) =>
            {
                enteredB.SetResult();
                await gateB.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
                return await RealReserveAsync(driverRepo, req, ct);
            },
            OnQuery = async (id, rid, ct) => MapResult(await driverRepo.QueryReservationAsync(id, rid, ct)),
        };

        // A loads R1 (cached max = V1) and parks inside the truth query.
        var reconciling = Task.Run(() => Orchestrator(stubA).ReconcileAsync(trip, 1, 0));
        await enteredA.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // B resumes the same attempt: fresh version V2, records fence V2,
        // then parks BEFORE sending ReserveDriver.
        var dispatching = Task.Run(() => Orchestrator(stubB).ExecuteAttemptAsync(trip, 1, 0, d1));
        await enteredB.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var fenced = await Attempts().LoadAttemptAsync(trip, 1, 0);
        Assert.Equal(v2, fenced!.MaxDispatchedVersion);

        // A observes AVAILABLE@V2 but proves only V2 > V1(stale): must NOT conclude.
        gateA.SetResult();
        var aResult = await reconciling;
        _out.WriteLine($"A reconcile outcome: {aResult.Outcome} ({aResult.Code})");
        Assert.True(
            aResult.Outcome is AttemptOutcome.Unknown or AttemptOutcome.NeedsReview,
            $"stale fence V1={v1} must not conclusively reject against durable fence V2={v2}, got {aResult.Outcome}");

        // B's authorized dispatch commits for real.
        gateB.SetResult();
        var bResult = await dispatching;
        Assert.Equal(AttemptOutcome.Reserved, bResult.Outcome);
        Assert.Equal(r1, bResult.ReservationId);

        var d1After = await driverRepo.LoadAsync(d1);
        Assert.Equal(DriverStatus.Reserved, d1After!.Status);
        Assert.Equal(r1, d1After.ReservationId);

        // Follow-up reconcile converges the committed R1; nothing else exists.
        var converged = await Orchestrator(new StubDriverClient
        {
            OnVersion = async (id, ct) =>
            {
                var d = await driverRepo.LoadAsync(id, ct);
                return d is null ? null : new DriverVersionRead(d.Version, d.Status.ToString().ToUpperInvariant());
            },
            OnReserve = (req, ct) => RealReserveAsync(driverRepo, req, ct),
            OnQuery = async (id, rid, ct) => MapResult(await driverRepo.QueryReservationAsync(id, rid, ct)),
        }).ReconcileAsync(trip, 1, 0);
        Assert.Equal(AttemptOutcome.Reserved, converged.Outcome);
        Assert.Equal(r1, converged.ReservationId);

        var attempts = await Attempts().LoadAttemptsAsync(trip, 1);
        var row = Assert.Single(attempts);
        Assert.Equal(MatchingAttemptStates.Reserved, row.State);
        Assert.Equal(r1, row.ReservationId);

        var finalD1 = await driverRepo.LoadAsync(d1);
        Assert.Equal(DriverStatus.Reserved, finalD1!.Status);
        Assert.Equal(r1, finalD1.ReservationId);
        var finalD2 = await driverRepo.LoadAsync(d2);
        Assert.Equal(DriverStatus.Available, finalD2!.Status);
        Assert.Null(finalD2.ReservationId);
        var round = await Attempts().LoadRoundAsync(trip, 1);
        Assert.Equal(d1, round!.SelectedDriverId);
        Assert.Equal(r1, round.SelectedReservationId);
    }

    [Fact]
    public async Task NegativeConclusion_Wins_LateDispatchDeniedBeforeSend()
    {
        // Mirror order: A conclusively rejects R1 first (no newer fence
        // exists, AVAILABLE@V2 > V1 is valid proof), then a stale worker B
        // that loaded R1 while UNKNOWN attempts RecordDispatch.
        //
        // Safe outcome: B is denied BEFORE any remote send (zero Reserve
        // calls, no Driver effect); R1 stays properly terminal; the next
        // candidate D2 may advance normally.
        var (trip, d1, d2, r1, v1, v2) = await SetupStaleFenceAsync();
        var driverRepo = new DriverRepository(_drivers.ConnectionString);

        var enteredB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var stubB = new StubDriverClient
        {
            OnVersion = async (id, ct) =>
            {
                enteredB.SetResult();
                await gateB.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
                var d = await driverRepo.LoadAsync(id, ct);
                return d is null ? null : new DriverVersionRead(d.Version, d.Status.ToString().ToUpperInvariant());
            },
            OnReserve = (req, ct) => RealReserveAsync(driverRepo, req, ct),
            OnQuery = async (id, rid, ct) => MapResult(await driverRepo.QueryReservationAsync(id, rid, ct)),
        };

        // B loads R1 while UNKNOWN and parks inside the version read.
        var dispatching = Task.Run(() => Orchestrator(stubB).ExecuteAttemptAsync(trip, 1, 0, d1));
        await enteredB.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // A conclusively rejects with valid proof (durable fence still V1).
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
        var rejected = await Attempts().LoadAttemptAsync(trip, 1, 0);
        Assert.Equal(MatchingAttemptStates.Rejected, rejected!.State);

        // B resumes: its dispatch authorization must fail before any send.
        gateB.SetResult();
        var bResult = await dispatching;
        _out.WriteLine($"B resume outcome: {bResult.Outcome} ({bResult.Code}) sends={stubB.ReserveSends}");
        Assert.Equal(0, stubB.ReserveSends);
        Assert.Equal(AttemptOutcome.Rejected, bResult.Outcome);
        Assert.Equal(r1, bResult.ReservationId);

        // No new Driver effect from the denied dispatch.
        var d1After = await driverRepo.LoadAsync(d1);
        Assert.Equal(DriverStatus.Available, d1After!.Status);
        Assert.Equal(v2, d1After.Version);
        Assert.Null(d1After.ReservationId);

        // R1 stays properly terminal; D2 advances normally (single winner).
        var d2Attempt = await Orchestrator(live).ExecuteAttemptAsync(trip, 1, 1, d2);
        Assert.Equal(AttemptOutcome.Reserved, d2Attempt.Outcome);
        var finalD2 = await driverRepo.LoadAsync(d2);
        Assert.Equal(DriverStatus.Reserved, finalD2!.Status);
        Assert.Equal(d2Attempt.ReservationId, finalD2.ReservationId);
        var finalD1 = await driverRepo.LoadAsync(d1);
        Assert.Equal(DriverStatus.Available, finalD1!.Status);
    }
}
