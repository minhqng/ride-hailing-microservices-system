// W07 reconciliation race — version-fenced repair proof.
// Proves: Matching must not treat current AVAILABLE as conclusive non-commit
// while an earlier R may still commit (pre-lock and pre-commit races).
// Uses REAL PostgreSQL on both sides; stub only coordinates transport faults.
// No sleeps; deterministic sequential scheduling (query before late commit).
using RideHailing.DriverService.Domain.Drivers;
using RideHailing.DriverService.Infrastructure.Persistence;
using RideHailing.MatchingService.Application.Reserve;
using RideHailing.MatchingService.Infrastructure.DriverClient;
using RideHailing.MatchingService.Infrastructure.Persistence;

namespace RideHailing.MatchingService.IntegrationTests;

[Collection(ReserveWorkflowCollection.Name)]
public sealed class ReconciliationRaceTests
{
    private readonly MatchingPostgresDatabase _matching;
    private readonly DriverTestDatabase _drivers;

    public ReconciliationRaceTests(
        MatchingPostgresDatabase matching,
        DriverTestDatabase drivers)
    {
        _matching = matching;
        _drivers = drivers;
    }

    private MatchingAttemptRepository Attempts() => new(_matching.ConnectionString);

    private MatchingReserveOrchestrator Orchestrator(
        StubDriverClient stub, int maxReserve = 3, int maxReconcile = 3) =>
        new(Attempts(), stub, maxReserve, maxReconcile);

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
        public readonly List<string> Calls = new();
        public Func<DriverReserveRequest, CancellationToken, Task<DriverReserveResult>>? OnReserve;
        public Func<Guid, Guid, CancellationToken, Task<ReservationReconcileResult>>? OnQuery;
        public Func<Guid, CancellationToken, Task<DriverVersionRead?>>? OnVersion;

        public Task<DriverVersionRead?> TryGetDriverVersionAsync(Guid driverId, CancellationToken ct = default)
        {
            Calls.Add($"version:{driverId:N}");
            if (OnVersion is not null)
            {
                return OnVersion(driverId, ct);
            }

            return Task.FromResult<DriverVersionRead?>(new DriverVersionRead(3, "AVAILABLE"));
        }

        public Task<DriverReserveResult> ReserveAsync(DriverReserveRequest request, CancellationToken ct = default)
        {
            Calls.Add($"reserve:{request.DriverId:N}:{request.ReservationId:N}");
            if (OnReserve is not null)
            {
                return OnReserve(request, ct);
            }

            return Task.FromResult(new DriverReserveResult(DriverReserveOutcomeKind.Reserved, null, 4));
        }

        public Task<ReservationReconcileResult> GetReservationAsync(Guid driverId, Guid reservationId, CancellationToken ct = default)
        {
            Calls.Add($"query:{driverId:N}:{reservationId:N}");
            if (OnQuery is not null)
            {
                return OnQuery(driverId, reservationId, ct);
            }

            return Task.FromResult(new ReservationReconcileResult(ReservationReconcileOutcome.NotCommittedOrAvailable, 3));
        }
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

    [Fact]
    public async Task PreCommit_Available_Must_Not_Advance_To_D2()
    {
        // T0 Matching persists R1 IN_FLIGHT. T1 Reserve(R1) outstanding
        // (transport loss, no commit yet). T5 query returns AVAILABLE before
        // original is final. T7 Matching must NOT advance to D2. Late R1
        // commit must NOT create a second durable reservation for same Trip.
        var trip = NewTrip();
        var d1 = await SeedAvailableDriverAsync();
        var d2 = await SeedAvailableDriverAsync();
        var driverRepo = new DriverRepository(_drivers.ConnectionString);

        // Capture the fencing version observed for R1 (real Driver version).
        int observedV = 0;
        var stub = new StubDriverClient
        {
            OnVersion = async (id, ct) =>
            {
                var d = await driverRepo.LoadAsync(id, ct);
                Assert.NotNull(d);
                if (id == d1)
                {
                    observedV = d!.Version;
                }

                return new DriverVersionRead(d!.Version, d.Status.ToString().ToUpperInvariant());
            },
            OnReserve = (req, _) =>
            {
                if (req.DriverId == d1)
                {
                    // Outstanding: response lost before any Driver effect.
                    throw new TimeoutException("injected response loss before commit");
                }

                // D2 path, if ever (unsafely) reached, performs a REAL commit
                // so the final driver_db oracle proves double reservation.
                return RealReserveAsync(driverRepo, req);
            },
            OnQuery = async (id, rid, ct) =>
            {
                var q = await driverRepo.QueryReservationAsync(id, rid, ct);
                return MapResult(q);
            },
        };
        var orch = Orchestrator(stub);

        var blocked = await orch.ExecuteRoundAsync(trip, 1, [d1, d2], Guid.NewGuid().ToString("D"));
        Assert.Equal(RoundOutcome.BlockedOnUnknown, blocked.Outcome);
        var r1 = Assert.Single(await Attempts().LoadAttemptsAsync(trip, 1)).ReservationId;
        Assert.NotEqual(Guid.Empty, r1);
        Assert.True(observedV >= 1);

        // Query sees AVAILABLE with Vx == max (no version advance): must NOT
        // conclude REJECTED. All polls stay inconclusive, so bounded exhaustion
        // becomes visible review with the row staying UNKNOWN (DSL-001).
        var reconciled = await orch.ReconcileAsync(trip, 1, 0);
        // Safety invariant: must NOT conclude REJECTED/RESERVED while R1 may still commit.
        Assert.True(
            reconciled.Outcome is AttemptOutcome.Unknown or AttemptOutcome.NeedsReview,
            $"must stay unresolved, got {reconciled.Outcome}");
        var rowAfter = Assert.Single(await Attempts().LoadAttemptsAsync(trip, 1));
        Assert.Equal(MatchingAttemptStates.Unknown, rowAfter.State);

        // Attempt D2 explicitly: must be blocked while R1 unresolved.
        var d2Attempt = await orch.ExecuteAttemptAsync(trip, 1, 1, d2);
        Assert.Equal(AttemptOutcome.Blocked, d2Attempt.Outcome);

        // Late original commits (pre-lock race: it never held the lock at query time).
        var late = await driverRepo.TryReserveAsync(d1, trip, r1, observedV);
        Assert.True(
            late.Outcome == ReserveOutcome.Won || late.Outcome == ReserveOutcome.Replayed,
            $"late R1 must still be committable to prove the race (got {late.Outcome})");

        // Second reconcile must converge to RESERVED for R1, never D2.
        var stub2 = new StubDriverClient
        {
            OnVersion = async (id, ct) =>
            {
                var d = await driverRepo.LoadAsync(id, ct);
                return d is null ? null : new DriverVersionRead(d.Version, d.Status.ToString().ToUpperInvariant());
            },
            OnReserve = (req, _) => RealReserveAsync(driverRepo, req),
            OnQuery = async (id, rid, ct) =>
            {
                var q = await driverRepo.QueryReservationAsync(id, rid, ct);
                return MapResult(q);
            },
        };
        var orch2 = Orchestrator(stub2);
        var converged = await orch2.ReconcileAsync(trip, 1, 0);
        Assert.Equal(AttemptOutcome.Reserved, converged.Outcome);
        Assert.Equal(r1, converged.ReservationId);

        // Authoritative final-state oracles.
        var finalD1 = await driverRepo.LoadAsync(d1);
        Assert.Equal(DriverStatus.Reserved, finalD1!.Status);
        Assert.Equal(r1, finalD1.ReservationId);
        var finalD2 = await driverRepo.LoadAsync(d2);
        Assert.Equal(DriverStatus.Available, finalD2!.Status);
        Assert.Null(finalD2.ReservationId);

        var attempts = await Attempts().LoadAttemptsAsync(trip, 1);
        Assert.DoesNotContain(attempts, a => a.Ordinal == 1 && a.State == MatchingAttemptStates.Reserved);
        var round = await Attempts().LoadRoundAsync(trip, 1);
        Assert.NotEqual(d2, round!.SelectedDriverId);
    }

    [Fact]
    public async Task Version_Advanced_Available_May_Advance_Safely()
    {
        // Liveness companion: when the Driver version has advanced beyond
        // every dispatched fence (unrelated Offline->Online bump), the old
        // R1 provably cannot commit (future reads see >= Vx > V0 -> Stale),
        // so advancing is safe. Guards against a fix that blocks every
        // AVAILABLE forever (bounded review only when truly inconclusive).
        var trip = NewTrip();
        var d1 = await SeedAvailableDriverAsync();
        var d2 = await SeedAvailableDriverAsync();
        var driverRepo = new DriverRepository(_drivers.ConnectionString);

        int v0 = 0;
        var stub = new StubDriverClient
        {
            OnVersion = async (id, ct) =>
            {
                var d = await driverRepo.LoadAsync(id, ct);
                if (id == d1)
                {
                    v0 = d!.Version;
                }

                return new DriverVersionRead(d!.Version, d.Status.ToString().ToUpperInvariant());
            },
            OnReserve = (req, _) =>
            {
                if (req.DriverId == d1)
                {
                    throw new TimeoutException("injected loss");
                }

                return RealReserveAsync(driverRepo, req);
            },
            OnQuery = async (id, rid, ct) =>
            {
                var q = await driverRepo.QueryReservationAsync(id, rid, ct);
                return MapResult(q);
            },
        };
        var orch = Orchestrator(stub);

        var blocked = await orch.ExecuteRoundAsync(trip, 1, [d1, d2], Guid.NewGuid().ToString("D"));
        Assert.Equal(RoundOutcome.BlockedOnUnknown, blocked.Outcome);
        Assert.True(v0 >= 1);

        // Unrelated version bump: Offline then Online (AVAILABLE, version+2).
        // Proves R1(V0) stale: any future R1 read sees version != V0.
        var off = await driverRepo.TryGoOfflineAsync(d1, v0);
        Assert.Equal(DriverMutationOutcome.Applied, off.Outcome);
        var mid = await driverRepo.LoadAsync(d1);
        var on = await driverRepo.TryGoOnlineAsync(d1, mid!.Version);
        Assert.Equal(DriverMutationOutcome.Applied, on.Outcome);
        var bumped = await driverRepo.LoadAsync(d1);
        Assert.Equal(DriverStatus.Available, bumped!.Status);
        Assert.True(bumped.Version > v0);

        // Old R1 can no longer commit (fenced stale).
        var staleLate = await driverRepo.TryReserveAsync(d1, Guid.NewGuid(), Guid.NewGuid(), v0);
        Assert.Equal(ReserveOutcome.Conflict, staleLate.Outcome);

        // Reconcile now sees AVAILABLE with advanced version -> may advance.
        var reconciled = await orch.ReconcileAsync(trip, 1, 0);
        Assert.Equal(AttemptOutcome.Rejected, reconciled.Outcome);

        // D2 may now be reserved safely; D1 stays free.
        var d2Attempt = await orch.ExecuteAttemptAsync(trip, 1, 1, d2);
        Assert.Equal(AttemptOutcome.Reserved, d2Attempt.Outcome);
        var finalD1 = await driverRepo.LoadAsync(d1);
        Assert.Equal(DriverStatus.Available, finalD1!.Status);
        var finalD2 = await driverRepo.LoadAsync(d2);
        Assert.Equal(DriverStatus.Reserved, finalD2!.Status);
    }

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
}
