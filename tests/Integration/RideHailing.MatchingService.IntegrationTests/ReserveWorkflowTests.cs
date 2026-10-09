// B-W07-05..10: reserve-workflow component proof on REAL PostgreSQL ×2.
// The orchestrator talks to Driver exclusively through IDriverClient; the
// stub below injects deterministic transport faults, while BOTH databases
// stay real: attempt/round rows in matching_db, reservation truth in
// driver_db. Final durable state on both sides is the oracle throughout.
using RideHailing.DriverService.Domain.Drivers;
using RideHailing.DriverService.Infrastructure.Persistence;
using RideHailing.MatchingService.Application.Reserve;
using RideHailing.MatchingService.Infrastructure.DriverClient;
using RideHailing.MatchingService.Infrastructure.Persistence;
using Xunit.Abstractions;

namespace RideHailing.MatchingService.IntegrationTests;

[Collection(ReserveWorkflowCollection.Name)]
public class ReserveWorkflowTests
{
    private readonly ITestOutputHelper _out;
    private readonly MatchingPostgresDatabase _matching;
    private readonly DriverTestDatabase _drivers;

    public ReserveWorkflowTests(
        ITestOutputHelper output,
        MatchingPostgresDatabase matching,
        DriverTestDatabase drivers)
    {
        _out = output;
        _matching = matching;
        _drivers = drivers;
    }

    // Fixtures fail loud at collection init (no skip path on this base):
    // reaching a test body proves both databases are real.

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

    /// <summary>
    /// Scriptable IDriverClient: deterministic transport faults with a call
    /// log. Defaults succeed against scripted versions; tests override per
    /// scenario. A fault delegate may drive the REAL DriverRepository to
    /// prove a commit happened before the simulated response loss (REL-01).
    /// </summary>
    private sealed class StubDriverClient : IDriverClient
    {
        public readonly List<string> Calls = new();
        public Func<DriverReserveRequest, CancellationToken, Task<DriverReserveResult>>? OnReserve;
        public Func<Guid, Guid, CancellationToken, Task<ReservationReconcileOutcome>>? OnQuery;
        public Func<Guid, CancellationToken, Task<DriverVersionRead?>>? OnVersion;
        public int VersionToReturn = 3;
        public string StatusToReturn = "AVAILABLE";

        public Task<DriverVersionRead?> TryGetDriverVersionAsync(Guid driverId, CancellationToken ct = default)
        {
            Calls.Add($"version:{driverId:N}");
            if (OnVersion is not null)
            {
                return OnVersion(driverId, ct);
            }

            return Task.FromResult<DriverVersionRead?>(new DriverVersionRead(VersionToReturn, StatusToReturn));
        }

        public Task<DriverReserveResult> ReserveAsync(DriverReserveRequest request, CancellationToken ct = default)
        {
            Calls.Add($"reserve:{request.DriverId:N}:{request.ReservationId:N}");
            if (OnReserve is not null)
            {
                return OnReserve(request, ct);
            }

            return Task.FromResult(new DriverReserveResult(DriverReserveOutcomeKind.Reserved, null, VersionToReturn + 1));
        }

        public Task<ReservationReconcileOutcome> GetReservationAsync(Guid driverId, Guid reservationId, CancellationToken ct = default)
        {
            Calls.Add($"query:{driverId:N}:{reservationId:N}");
            if (OnQuery is not null)
            {
                return OnQuery(driverId, reservationId, ct);
            }

            return Task.FromResult(ReservationReconcileOutcome.NotCommittedOrAvailable);
        }
    }

    [Fact]
    public async Task Round_Serial_Success_Stops_At_First_Winner()
    {

        var trip = NewTrip();
        var d1 = Guid.NewGuid();
        var d2 = Guid.NewGuid();
        var d3 = Guid.NewGuid();
        var forwardedVersions = new List<int>();
        var stub = new StubDriverClient
        {
            OnVersion = (_, _) => Task.FromResult<DriverVersionRead?>(new DriverVersionRead(3, "AVAILABLE")),
            OnReserve = (req, _) =>
            {
                forwardedVersions.Add(req.ExpectedVersion);
                return Task.FromResult(
                    req.DriverId == d2
                        ? new DriverReserveResult(DriverReserveOutcomeKind.Reserved, null, 8)
                        : new DriverReserveResult(DriverReserveOutcomeKind.Rejected, "DRIVER_NOT_AVAILABLE", null));
            },
        };

        var result = await Orchestrator(stub).ExecuteRoundAsync(
            trip, 1, [d1, d2, d3], Guid.NewGuid());

        Assert.Equal(RoundOutcome.Reserved, result.Outcome);
        Assert.Equal(d2, result.DriverId);
        // The OCC dance is real: every reserve call forwarded the observed
        // version (a hardcoded or dropped version would fail here).
        Assert.Equal(new[] { 3, 3 }, forwardedVersions);
        var kinds = stub.Calls.Select(c =>
        {
            var parts = c.Split(':');
            return parts[0] + ":" + parts[1];
        }).ToList();
        Assert.Equal(
            new[] { $"version:{d1:N}", $"reserve:{d1:N}", $"version:{d2:N}", $"reserve:{d2:N}" },
            kinds);
        Assert.DoesNotContain(stub.Calls, c => c.StartsWith($"version:{d3:N}", StringComparison.Ordinal));

        var rows = await Attempts().LoadAttemptsAsync(trip, 1);
        Assert.Equal(2, rows.Count);
        Assert.Equal(MatchingAttemptStates.Rejected, rows[0].State);
        Assert.Equal(MatchingAttemptStates.Reserved, rows[1].State);
        var round = await Attempts().LoadRoundAsync(trip, 1);
        Assert.Equal(MatchingRoundStatuses.Reserved, round!.Status);
        Assert.Equal(d2, round.SelectedDriverId);
    }

    [Fact]
    public async Task Round_Stops_On_Unknown_Without_Touching_Next_Candidate()
    {

        // B-W07-10 negative assertion: D1 UNKNOWN ⇒ zero further Driver
        // calls, zero further rows (DSI-017/031).
        var trip = NewTrip();
        var d1 = Guid.NewGuid();
        var d2 = Guid.NewGuid();
        var stub = new StubDriverClient
        {
            OnReserve = (_, _) => throw new TimeoutException("injected response loss"),
        };

        var result = await Orchestrator(stub).ExecuteRoundAsync(
            trip, 1, [d1, d2], Guid.NewGuid());

        Assert.Equal(RoundOutcome.BlockedOnUnknown, result.Outcome);
        Assert.DoesNotContain(stub.Calls, c => c.Contains(d2.ToString("N"), StringComparison.Ordinal));

        var rows = await Attempts().LoadAttemptsAsync(trip, 1);
        var only = Assert.Single(rows);
        Assert.Equal(MatchingAttemptStates.Unknown, only.State);
        Assert.Equal(d1, only.DriverId);
    }

    [Fact]
    public async Task Round_All_Rejected_Exhausts_Without_Terminal_Marking()
    {

        // NO_DRIVER terminal needs W9 Outbox atomicity: all-rejected leaves
        // the round PROCESSING (deferred, not fabricated).
        var trip = NewTrip();
        var stub = new StubDriverClient
        {
            OnReserve = (_, _) => Task.FromResult(
                new DriverReserveResult(DriverReserveOutcomeKind.Rejected, "DRIVER_NOT_AVAILABLE", null)),
        };

        var result = await Orchestrator(stub).ExecuteRoundAsync(
            trip, 1, [Guid.NewGuid(), Guid.NewGuid()], Guid.NewGuid());

        Assert.Equal(RoundOutcome.Exhausted, result.Outcome);
        var round = await Attempts().LoadRoundAsync(trip, 1);
        Assert.Equal(MatchingRoundStatuses.Processing, round!.Status);
    }

    [Fact]
    public async Task Round_Replay_Returns_Canonical_Without_New_Effects()
    {

        var trip = NewTrip();
        var d1 = Guid.NewGuid();
        var calls = 0;
        var stub = new StubDriverClient
        {
            OnReserve = (_, _) =>
            {
                calls++;
                return Task.FromResult(new DriverReserveResult(DriverReserveOutcomeKind.Reserved, null, 8));
            },
        };
        var orch = Orchestrator(stub);

        var first = await orch.ExecuteRoundAsync(trip, 1, [d1], Guid.NewGuid());
        var second = await orch.ExecuteRoundAsync(trip, 1, [d1], Guid.NewGuid());

        Assert.Equal(RoundOutcome.Reserved, first.Outcome);
        Assert.Equal(RoundOutcome.Reserved, second.Outcome);
        Assert.Equal(first.ReservationId, second.ReservationId);
        var replayed = await Attempts().LoadAttemptsAsync(trip, 1);
        var replayRow = Assert.Single(replayed);
        Assert.Equal(first.ReservationId, replayRow.ReservationId);
        _out.WriteLine($"reserve calls across replay: {calls} (second run must add none)");
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Response_Loss_Converges_Via_Same_Reservation_Reconcile()
    {

        // REL-01 (Doc 03 §9): the stub performs a REAL reserve commit in
        // driver_db, then suppresses the response (faultpoint equivalent).
        // Caller sees timeout ⇒ UNKNOWN ⇒ no D2 ⇒ reconcile same R ⇒ round
        // RESERVED. Oracles on both databases.
        var trip = NewTrip();
        var driverId = await SeedAvailableDriverAsync();
        var driverRepo = new DriverRepository(_drivers.ConnectionString);
        var stub = new StubDriverClient
        {
            OnVersion = async (id, ct) =>
            {
                var d = await driverRepo.LoadAsync(id, ct);
                return d is null ? null : new DriverVersionRead(d.Version, d.Status.ToString().ToUpperInvariant());
            },
            OnReserve = async (req, ct) =>
            {
                var loaded = await driverRepo.LoadAsync(req.DriverId, ct);
                var outcome = await driverRepo.TryReserveAsync(
                    req.DriverId, req.TripId, req.ReservationId, req.ExpectedVersion, ct);
                if (outcome.Outcome == RideHailing.DriverService.Infrastructure.Persistence.ReserveOutcome.Won
                    || outcome.Outcome == RideHailing.DriverService.Infrastructure.Persistence.ReserveOutcome.Replayed)
                {
                    throw new TimeoutException("faultpoint: response suppressed after commit");
                }

                return new DriverReserveResult(
                    DriverReserveOutcomeKind.Rejected,
                    outcome.ConflictCode ?? "DRIVER_NOT_AVAILABLE",
                    null);
            },
            OnQuery = async (id, rid, ct) =>
            {
                var q = await driverRepo.QueryReservationAsync(id, rid, ct);
                return q.Outcome switch
                {
                    RideHailing.DriverService.Infrastructure.Persistence.ReservationQueryOutcome.OwnedByThisReservation
                        => ReservationReconcileOutcome.OwnedByThisReservation,
                    RideHailing.DriverService.Infrastructure.Persistence.ReservationQueryOutcome.NotCommittedOrAvailable
                        => ReservationReconcileOutcome.NotCommittedOrAvailable,
                    RideHailing.DriverService.Infrastructure.Persistence.ReservationQueryOutcome.ReservedByOther
                        => ReservationReconcileOutcome.ReservedByOther,
                    RideHailing.DriverService.Infrastructure.Persistence.ReservationQueryOutcome.Offline
                        => ReservationReconcileOutcome.Offline,
                    RideHailing.DriverService.Infrastructure.Persistence.ReservationQueryOutcome.OnTrip
                        => ReservationReconcileOutcome.OnTrip,
                    _ => ReservationReconcileOutcome.NotFound,
                };
            },
        };
        var orch = Orchestrator(stub);
        var d2 = Guid.NewGuid();

        var blocked = await orch.ExecuteRoundAsync(trip, 1, [driverId, d2], Guid.NewGuid());
        Assert.Equal(RoundOutcome.BlockedOnUnknown, blocked.Outcome);

        var attempt = Assert.Single(await Attempts().LoadAttemptsAsync(trip, 1));
        Assert.Equal(MatchingAttemptStates.Unknown, attempt.State);
        var committedR = attempt.ReservationId;
        Assert.Single(stub.Calls, c => c.StartsWith("reserve:", StringComparison.Ordinal));

        var reconciled = await orch.ReconcileAsync(trip, 1, 0);
        Assert.Equal(AttemptOutcome.Reserved, reconciled.Outcome);
        Assert.Equal(committedR, reconciled.ReservationId);

        var driver = await driverRepo.LoadAsync(driverId);
        Assert.Equal(DriverStatus.Reserved, driver!.Status);
        Assert.Equal(committedR, driver.ReservationId);
        var round = await Attempts().LoadRoundAsync(trip, 1);
        Assert.Equal(MatchingRoundStatuses.Reserved, round!.Status);
        Assert.Equal(driverId, round.SelectedDriverId);
        Assert.DoesNotContain(stub.Calls, c => c.Contains(d2.ToString("N"), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Reconcile_Exhaustion_Is_Visible_Terminal_Not_Silent()
    {

        // Truth permanently unavailable: bounded polling then NeedsReview;
        // the row stays UNKNOWN (durable truth), the outcome is visible.
        var trip = NewTrip();
        var d1 = Guid.NewGuid();
        var queries = 0;
        var stub = new StubDriverClient
        {
            OnReserve = (_, _) => throw new TimeoutException("injected loss"),
            OnQuery = (_, _, _) =>
            {
                queries++;
                throw new TimeoutException("truth unavailable");
            },
        };
        var orch = Orchestrator(stub, maxReconcile: 3);

        var blocked = await orch.ExecuteRoundAsync(trip, 1, [d1], Guid.NewGuid());
        Assert.Equal(RoundOutcome.BlockedOnUnknown, blocked.Outcome);

        var terminal = await orch.ReconcileAsync(trip, 1, 0);
        Assert.Equal(AttemptOutcome.NeedsReview, terminal.Outcome);
        Assert.Equal(3, queries);

        var row = Assert.Single(await Attempts().LoadAttemptsAsync(trip, 1));
        Assert.Equal(MatchingAttemptStates.Unknown, row.State);
        Assert.Equal("RECONCILE_EXHAUSTED", row.LastErrorCode);
        var round = await Attempts().LoadRoundAsync(trip, 1);
        Assert.Equal(MatchingRoundStatuses.Processing, round!.Status);
    }

    [Fact]
    public async Task Reconcile_Conclusive_Outcomes_Resolve_Durably()
    {

        var trip = NewTrip();
        foreach (var (wire, code) in new[]
                 {
                     (ReservationReconcileOutcome.NotCommittedOrAvailable, "NOT_COMMITTED_OR_AVAILABLE"),
                     (ReservationReconcileOutcome.ReservedByOther, "RESERVED_BY_OTHER"),
                     (ReservationReconcileOutcome.Offline, "OFFLINE"),
                     (ReservationReconcileOutcome.OnTrip, "ON_TRIP"),
                     (ReservationReconcileOutcome.NotFound, "NOT_FOUND"),
                 })
        {
            var d = Guid.NewGuid();
            var stub = new StubDriverClient
            {
                OnReserve = (_, _) => throw new TimeoutException("injected loss"),
                OnQuery = (_, _, _) => Task.FromResult(wire),
            };
            var orch = Orchestrator(stub);
            var roundNo = 10 + Array.IndexOf(new[]
            {
                ReservationReconcileOutcome.NotCommittedOrAvailable,
                ReservationReconcileOutcome.ReservedByOther,
                ReservationReconcileOutcome.Offline,
                ReservationReconcileOutcome.OnTrip,
                ReservationReconcileOutcome.NotFound,
            }, wire);

            var blocked = await orch.ExecuteRoundAsync(trip, roundNo, [d], Guid.NewGuid());
            Assert.Equal(RoundOutcome.BlockedOnUnknown, blocked.Outcome);
            var resolved = await orch.ReconcileAsync(trip, roundNo, 0);
            Assert.Equal(AttemptOutcome.Rejected, resolved.Outcome);
            Assert.Equal(code, resolved.Code);
            var row = Assert.Single(await Attempts().LoadAttemptsAsync(trip, roundNo));
            Assert.Equal(MatchingAttemptStates.Rejected, row.State);
            Assert.Equal(code, row.LastErrorCode);
        }
    }

    [Fact]
    public async Task Duplicate_Workers_With_Divergent_Lists_Converge_To_First_View()
    {
        // Same ordinal, different drivers: whichever interleaving occurs, the
        // schedule-invariant properties hold — one durable row, every Reserved
        // agrees with it, any Blocked is UNRESOLVED_ATTEMPT (fail-safe), the
        // round never marks a non-row winner. (A barrier cannot force true
        // overlap, so no specific interleaving is asserted.)
        var trip = NewTrip();
        var d1 = Guid.NewGuid();
        var other = Guid.NewGuid();
        var stub = new StubDriverClient
        {
            OnReserve = (_, _) => Task.FromResult(
                new DriverReserveResult(DriverReserveOutcomeKind.Reserved, null, 8)),
        };
        var orch = Orchestrator(stub);

        using var barrier = new Barrier(2);
        var runs = await Task.WhenAll(
            Task.Run(async () =>
            {
                barrier.SignalAndWait(TimeSpan.FromSeconds(60));
                return await orch.ExecuteRoundAsync(trip, 1, [d1], Guid.NewGuid());
            }),
            Task.Run(async () =>
            {
                barrier.SignalAndWait(TimeSpan.FromSeconds(60));
                return await orch.ExecuteRoundAsync(trip, 1, [other], Guid.NewGuid());
            }));

        var row = Assert.Single(await Attempts().LoadAttemptsAsync(trip, 1));
        foreach (var r in runs)
        {
            Assert.True(
                r.Outcome is RoundOutcome.Reserved or RoundOutcome.BlockedOnUnknown,
                $"unexpected {r.Outcome}");
            if (r.Outcome == RoundOutcome.Reserved)
            {
                Assert.Equal(row.ReservationId, r.ReservationId);
                Assert.Equal(row.DriverId, r.DriverId);
            }
            else
            {
                Assert.Equal("UNRESOLVED_ATTEMPT", r.Code);
            }
        }

        foreach (var c in stub.Calls.Where(c => c.StartsWith("reserve:", StringComparison.Ordinal)))
        {
            Assert.Contains(row.ReservationId.ToString("N"), c, StringComparison.Ordinal);
        }

        var round = await Attempts().LoadRoundAsync(trip, 1);
        if (runs.Any(r => r.Outcome == RoundOutcome.Reserved))
        {
            Assert.Equal(MatchingRoundStatuses.Reserved, round!.Status);
            Assert.Equal(row.DriverId, round.SelectedDriverId);
            Assert.Equal(row.ReservationId, round.SelectedReservationId);
        }
        else
        {
            Assert.Equal(MatchingRoundStatuses.Processing, round!.Status);
        }
    }

    [Fact]
    public async Task Reservation_Reuse_Across_Ordinals_Fails_Loud()
    {

        // DSI-018: one R = one logical attempt. Reusing an R on a different
        // ordinal trips the reservation UNIQUE (23505) — loud
        // PostgresException, never silent convergence to a wrong row.
        var trip = NewTrip();
        var repo = Attempts();
        await repo.EnsureRoundAsync(trip, 1, Guid.NewGuid());
        var r = Guid.NewGuid();
        var first = await repo.EnsureInflightAttemptAsync(trip, 1, 0, Guid.NewGuid(), r);
        Assert.True(first.Inserted);

        var ex = await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
            repo.EnsureInflightAttemptAsync(trip, 1, 1, Guid.NewGuid(), r));
        Assert.Equal("23505", ex.SqlState);
    }

    [Fact]
    public async Task Duplicate_Workers_Converge_To_One_Identity()
    {

        // Same candidate list on two workers: PK convergence on
        // (trip,round,ordinal) forces one reservation identity; both observe
        // canonical success; exactly one row exists.
        var trip = NewTrip();
        var d1 = Guid.NewGuid();
        var stub = new StubDriverClient
        {
            OnReserve = (_, _) => Task.FromResult(
                new DriverReserveResult(DriverReserveOutcomeKind.Reserved, null, 8)),
        };
        var orch = Orchestrator(stub);

        using var barrier = new Barrier(2);
        var runs = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
        {
            barrier.SignalAndWait(TimeSpan.FromSeconds(60));
            return await orch.ExecuteRoundAsync(trip, 1, [d1], Guid.NewGuid());
        })));

        Assert.All(runs, r => Assert.Equal(RoundOutcome.Reserved, r.Outcome));
        Assert.Equal(runs[0].ReservationId, runs[1].ReservationId);
        var rows = await Attempts().LoadAttemptsAsync(trip, 1);
        var row = Assert.Single(rows);
        Assert.Equal(runs[0].ReservationId, row.ReservationId);
    }

    [Fact]
    public async Task Restart_Resumes_Same_Reservation_Identity()
    {

        // Crash simulation: first orchestrator instance persists IN_FLIGHT
        // then "dies" (dropped); a fresh instance resumes the SAME attempt
        // with the SAME reservation identity — never a fresh R.
        var trip = NewTrip();
        var d1 = Guid.NewGuid();
        var stub = new StubDriverClient
        {
            OnReserve = (_, _) => throw new TimeoutException("injected loss"),
        };

        var first = Orchestrator(stub);
        var blocked = await first.ExecuteRoundAsync(trip, 1, [d1], Guid.NewGuid());
        Assert.Equal(RoundOutcome.BlockedOnUnknown, blocked.Outcome);
        var persistedR = Assert.Single(await Attempts().LoadAttemptsAsync(trip, 1)).ReservationId;

        // Round-level resume converges the same way: the guard sees the
        // unresolved attempt and blocks without new effects.
        var second = Orchestrator(stub);
        var roundResume = await second.ExecuteRoundAsync(trip, 1, [d1], Guid.NewGuid());
        Assert.Equal(RoundOutcome.BlockedOnUnknown, roundResume.Outcome);
        Assert.Single(await Attempts().LoadAttemptsAsync(trip, 1));

        var resumed = await second.ReconcileAsync(trip, 1, 0);
        Assert.Equal(persistedR, resumed.ReservationId);
    }

    [Fact]
    public async Task Stale_Version_Retries_Same_Reservation_With_Fresh_Version()
    {

        // Read-then-attempt OCC dance: first call fenced STALE, orchestrator
        // re-reads and retries the SAME R (never a fresh identity).
        var trip = NewTrip();
        var d1 = Guid.NewGuid();
        var versions = new Queue<int>(new[] { 3, 9 });
        var seen = new List<(Guid R, int V)>();
        var stub = new StubDriverClient
        {
            OnVersion = (_, _) => Task.FromResult<DriverVersionRead?>(new DriverVersionRead(versions.Dequeue(), "AVAILABLE")),
            OnReserve = (req, _) =>
            {
                seen.Add((req.ReservationId, req.ExpectedVersion));
                return Task.FromResult(seen.Count == 1
                    ? new DriverReserveResult(DriverReserveOutcomeKind.Rejected, "STALE_VERSION", null)
                    : new DriverReserveResult(DriverReserveOutcomeKind.Reserved, null, 10));
            },
        };

        var result = await Orchestrator(stub).ExecuteRoundAsync(trip, 1, [d1], Guid.NewGuid());

        Assert.Equal(RoundOutcome.Reserved, result.Outcome);
        Assert.Equal(2, seen.Count);
        Assert.Equal(seen[0].R, seen[1].R);
        Assert.Equal(3, seen[0].V);
        Assert.Equal(9, seen[1].V);
        var row = Assert.Single(await Attempts().LoadAttemptsAsync(trip, 1));
        Assert.Equal(MatchingAttemptStates.Reserved, row.State);
        Assert.Equal(seen[0].R, row.ReservationId);
    }

    [Fact]
    public async Task Repeated_Stale_Contention_Stays_Unknown_Never_Advances()
    {

        // The load-bearing contention-exhaustion path: every fence rejects
        // our R (never committed), so no conclusive outcome exists — the
        // attempt stays UNKNOWN and the round stops (fail safe).
        var trip = NewTrip();
        var d1 = Guid.NewGuid();
        var d2 = Guid.NewGuid();
        var stub = new StubDriverClient
        {
            OnReserve = (_, _) => Task.FromResult(
                new DriverReserveResult(DriverReserveOutcomeKind.Rejected, "STALE_VERSION", null)),
        };

        var result = await Orchestrator(stub).ExecuteRoundAsync(
            trip, 1, [d1, d2], Guid.NewGuid());

        Assert.Equal(RoundOutcome.BlockedOnUnknown, result.Outcome);
        Assert.Equal("RESERVE_CONTENTION", result.Code);
        var row = Assert.Single(await Attempts().LoadAttemptsAsync(trip, 1));
        Assert.Equal(MatchingAttemptStates.Unknown, row.State);
        Assert.Equal("RESERVE_CONTENTION", row.LastErrorCode);
        Assert.DoesNotContain(stub.Calls, c => c.Contains(d2.ToString("N"), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cancelled_Round_Refuses_New_Work()
    {

        var trip = NewTrip();
        var stub = new StubDriverClient();
        await using var conn = new Npgsql.NpgsqlConnection(_matching.ConnectionString);
        await conn.OpenAsync();
        await using (var cmd = new Npgsql.NpgsqlCommand(
            "INSERT INTO matching_rounds (trip_id, matching_round, input_message_id, status) " +
            "VALUES (@trip, 1, @msg, 'CANCELLED')", conn))
        {
            cmd.Parameters.AddWithValue("trip", trip);
            cmd.Parameters.AddWithValue("msg", Guid.NewGuid());
            await cmd.ExecuteNonQueryAsync();
        }

        var result = await Orchestrator(stub).ExecuteRoundAsync(
            trip, 1, [Guid.NewGuid()], Guid.NewGuid());

        Assert.Equal(RoundOutcome.Refused, result.Outcome);
        Assert.Empty(stub.Calls);
        Assert.Empty(await Attempts().LoadAttemptsAsync(trip, 1));
    }

    [Fact]
    public async Task Missing_Driver_Is_Conclusive_Without_Attempt_Effect()
    {

        // Version read 404: an unknown driver cannot hold an effect —
        // conclusive rejection, round advances past it.
        var trip = NewTrip();
        var missing = Guid.NewGuid();
        var next = Guid.NewGuid();
        var stub = new StubDriverClient
        {
            OnVersion = (id, _) => Task.FromResult<DriverVersionRead?>(
                id == missing ? null : new DriverVersionRead(4, "AVAILABLE")),
            OnReserve = (_, _) => Task.FromResult(
                new DriverReserveResult(DriverReserveOutcomeKind.Reserved, null, 5)),
        };

        var result = await Orchestrator(stub).ExecuteRoundAsync(
            trip, 1, [missing, next], Guid.NewGuid());

        Assert.Equal(RoundOutcome.Reserved, result.Outcome);
        Assert.Equal(next, result.DriverId);
        var rows = await Attempts().LoadAttemptsAsync(trip, 1);
        Assert.Equal(
            new[] { MatchingAttemptStates.Rejected, MatchingAttemptStates.Reserved },
            rows.Select(r => r.State));
        Assert.Equal(MatchingAttemptStates.Rejected, rows[0].State);
        Assert.Equal("DRIVER_NOT_FOUND", rows[0].LastErrorCode);
    }

    [Fact]
    public async Task Ambiguous_Attempt_Bare_404_Stays_Unresolved_Without_Advance()
    {

        // F1 CASE 2: timeout makes R ambiguous (row UNKNOWN); a later bare
        // 404 (proxy/wrong-route/deleted-row — indistinguishable on the wire)
        // must NOT become conclusive rejection. Contrast with the fresh-404
        // path (Missing_Driver_Is_Conclusive), which may conclude.
        var trip = NewTrip();
        var d1 = Guid.NewGuid();
        var d2 = Guid.NewGuid();
        var phase = 0;
        var stub = new StubDriverClient
        {
            OnVersion = (_, _) => Task.FromResult<DriverVersionRead?>(
                phase == 0 ? new DriverVersionRead(3, "AVAILABLE") : null),
            OnReserve = (_, _) =>
            {
                phase++;
                throw new TimeoutException("injected response loss");
            },
        };

        var first = await Orchestrator(stub).ExecuteRoundAsync(trip, 1, [d1, d2], Guid.NewGuid());
        Assert.Equal(RoundOutcome.BlockedOnUnknown, first.Outcome);
        var persistedR = Assert.Single(await Attempts().LoadAttemptsAsync(trip, 1)).ReservationId;

        phase = 1;
        var callsBefore = stub.Calls.Count;
        var second = await Orchestrator(stub).ExecuteRoundAsync(trip, 1, [d1, d2], Guid.NewGuid());
        Assert.Equal(RoundOutcome.BlockedOnUnknown, second.Outcome);
        Assert.Equal("PRIOR_AMBIGUITY", second.Code);

        var row = Assert.Single(await Attempts().LoadAttemptsAsync(trip, 1));
        Assert.Equal(MatchingAttemptStates.Unknown, row.State);
        Assert.Equal(persistedR, row.ReservationId);
        Assert.DoesNotContain(stub.Calls.Skip(callsBefore), c => c.Contains(d2.ToString("N"), StringComparison.Ordinal));
        var round = await Attempts().LoadRoundAsync(trip, 1);
        Assert.Equal(MatchingRoundStatuses.Processing, round!.Status);
    }

    [Fact]
    public async Task Restart_After_Ambiguity_Bare_404_Keeps_Same_R_Unresolved()
    {

        // F1 CASE 3: ambiguity context is durable, not process memory. A
        // fresh orchestrator instance after restart must reach the same
        // verdict: same R retained, single row, no D2 call.
        var trip = NewTrip();
        var d1 = Guid.NewGuid();
        var d2 = Guid.NewGuid();
        var stub = new StubDriverClient
        {
            OnVersion = (_, _) => Task.FromResult<DriverVersionRead?>(new DriverVersionRead(3, "AVAILABLE")),
            OnReserve = (_, _) => throw new TimeoutException("injected response loss"),
        };

        var blocked = await Orchestrator(stub).ExecuteRoundAsync(trip, 1, [d1, d2], Guid.NewGuid());
        Assert.Equal(RoundOutcome.BlockedOnUnknown, blocked.Outcome);
        var persistedR = Assert.Single(await Attempts().LoadAttemptsAsync(trip, 1)).ReservationId;

        var restarted = new StubDriverClient
        {
            OnVersion = (_, _) => Task.FromResult<DriverVersionRead?>(null),
            OnReserve = (_, _) => throw new InvalidOperationException("must not be called after ambiguity"),
        };
        var result = await Orchestrator(restarted).ExecuteRoundAsync(trip, 1, [d1, d2], Guid.NewGuid());

        Assert.Equal(RoundOutcome.BlockedOnUnknown, result.Outcome);
        var row = Assert.Single(await Attempts().LoadAttemptsAsync(trip, 1));
        Assert.Equal(MatchingAttemptStates.Unknown, row.State);
        Assert.Equal(persistedR, row.ReservationId);
        Assert.DoesNotContain(restarted.Calls, c => c.Contains(d2.ToString("N"), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Reconcile_Bare_404_Throw_Stays_Unknown_Bounded()
    {

        // F1 CASE 5: the reconcile transport itself 404s bare (client throws
        // per the DSI-031 rule) → stays UNKNOWN, bounded polls, visible end.
        var trip = NewTrip();
        var d1 = Guid.NewGuid();
        var queries = 0;
        var stub = new StubDriverClient
        {
            OnReserve = (_, _) => throw new TimeoutException("injected loss"),
            OnQuery = (_, _, _) =>
            {
                queries++;
                throw new HttpRequestException("bare 404 throw");
            },
        };
        var orch = Orchestrator(stub, maxReconcile: 2);

        var blocked = await orch.ExecuteRoundAsync(trip, 1, [d1], Guid.NewGuid());
        Assert.Equal(RoundOutcome.BlockedOnUnknown, blocked.Outcome);
        var terminal = await orch.ReconcileAsync(trip, 1, 0);
        Assert.Equal(AttemptOutcome.NeedsReview, terminal.Outcome);
        Assert.Equal(2, queries);
        Assert.Equal(MatchingAttemptStates.Unknown, Assert.Single(await Attempts().LoadAttemptsAsync(trip, 1)).State);
    }

    [Fact]
    public async Task Reserved_Attempt_Heals_Unmarked_Round_Without_New_Call()
    {

        // F2 CASE 1: crash between attempt commit and round mark, simulated
        // by flipping a durably RESERVED round back to PROCESSING. Replay
        // must converge the round, issue zero Driver calls, keep identity.
        var trip = NewTrip();
        var d1 = Guid.NewGuid();
        var stub = new StubDriverClient
        {
            OnReserve = (_, _) => Task.FromResult(
                new DriverReserveResult(DriverReserveOutcomeKind.Reserved, null, 8)),
        };
        var orch = Orchestrator(stub);

        var won = await orch.ExecuteRoundAsync(trip, 1, [d1], Guid.NewGuid());
        Assert.Equal(RoundOutcome.Reserved, won.Outcome);
        var winnerR = won.ReservationId;
        Assert.NotNull(winnerR);

        await using (var conn = new Npgsql.NpgsqlConnection(_matching.ConnectionString))
        {
            await conn.OpenAsync();
            await using var flip = new Npgsql.NpgsqlCommand(
                "UPDATE matching_rounds SET status = 'PROCESSING', " +
                "selected_driver_id = NULL, selected_reservation_id = NULL " +
                "WHERE trip_id = @trip AND matching_round = 1", conn);
            flip.Parameters.AddWithValue("trip", trip);
            Assert.Equal(1, await flip.ExecuteNonQueryAsync());
        }

        var silent = new StubDriverClient
        {
            OnVersion = (_, _) => throw new InvalidOperationException("must not call Driver"),
            OnReserve = (_, _) => throw new InvalidOperationException("must not call Driver"),
            OnQuery = (_, _, _) => throw new InvalidOperationException("must not call Driver"),
        };
        var healed = await Orchestrator(silent).ExecuteRoundAsync(trip, 1, [d1], Guid.NewGuid());

        Assert.Equal(RoundOutcome.Reserved, healed.Outcome);
        Assert.Equal(winnerR, healed.ReservationId);
        Assert.Equal(d1, healed.DriverId);
        var round = await Attempts().LoadRoundAsync(trip, 1);
        Assert.Equal(MatchingRoundStatuses.Reserved, round!.Status);
        Assert.Equal(d1, round.SelectedDriverId);
        Assert.Equal(winnerR, round.SelectedReservationId);
    }

    [Fact]
    public async Task Round_Repair_Is_Idempotent_Under_Duplicate_Workers()
    {

        // F2 CASES 2+3: repeated and concurrent replays converge without
        // duplicate effects; both workers report the durable winner.
        var trip = NewTrip();
        var d1 = Guid.NewGuid();
        var stub = new StubDriverClient
        {
            OnReserve = (_, _) => Task.FromResult(
                new DriverReserveResult(DriverReserveOutcomeKind.Reserved, null, 8)),
        };
        var orch = Orchestrator(stub);
        var won = await orch.ExecuteRoundAsync(trip, 1, [d1], Guid.NewGuid());
        Assert.Equal(RoundOutcome.Reserved, won.Outcome);

        await using (var conn = new Npgsql.NpgsqlConnection(_matching.ConnectionString))
        {
            await conn.OpenAsync();
            await using var flip = new Npgsql.NpgsqlCommand(
                "UPDATE matching_rounds SET status = 'PROCESSING', " +
                "selected_driver_id = NULL, selected_reservation_id = NULL " +
                "WHERE trip_id = @trip AND matching_round = 1", conn);
            flip.Parameters.AddWithValue("trip", trip);
            await flip.ExecuteNonQueryAsync();
        }

        var silent = new StubDriverClient
        {
            OnVersion = (_, _) => throw new InvalidOperationException("must not call Driver"),
            OnReserve = (_, _) => throw new InvalidOperationException("must not call Driver"),
            OnQuery = (_, _, _) => throw new InvalidOperationException("must not call Driver"),
        };
        var repairer = Orchestrator(silent);
        using var barrier = new Barrier(2);
        var runs = await Task.WhenAll(
            Task.Run(async () =>
            {
                barrier.SignalAndWait(TimeSpan.FromSeconds(60));
                return await repairer.ExecuteRoundAsync(trip, 1, [d1], Guid.NewGuid());
            }),
            Task.Run(async () =>
            {
                barrier.SignalAndWait(TimeSpan.FromSeconds(60));
                return await repairer.ExecuteRoundAsync(trip, 1, [d1], Guid.NewGuid());
            }));

        Assert.All(runs, r => Assert.Equal(RoundOutcome.Reserved, r.Outcome));
        Assert.Equal(runs[0].ReservationId, runs[1].ReservationId);
        var round = await Attempts().LoadRoundAsync(trip, 1);
        Assert.Equal(MatchingRoundStatuses.Reserved, round!.Status);
        Assert.Single(await Attempts().LoadAttemptsAsync(trip, 1));
    }

    [Fact]
    public async Task Unresolved_Attempt_Never_Marks_Round_Reserved()
    {

        // F2 CASES 4+5: UNKNOWN and REJECTED attempts must not mark the round.
        var trip = NewTrip();
        var d1 = Guid.NewGuid();
        var stub = new StubDriverClient
        {
            OnReserve = (_, _) => throw new TimeoutException("injected loss"),
        };
        var orch = Orchestrator(stub);

        var blocked = await orch.ExecuteRoundAsync(trip, 7, [d1], Guid.NewGuid());
        Assert.Equal(RoundOutcome.BlockedOnUnknown, blocked.Outcome);
        Assert.Equal(
            MatchingRoundStatuses.Processing,
            (await Attempts().LoadRoundAsync(trip, 7))!.Status);

        var rejecting = new StubDriverClient
        {
            OnReserve = (_, _) => Task.FromResult(
                new DriverReserveResult(DriverReserveOutcomeKind.Rejected, "DRIVER_NOT_AVAILABLE", null)),
        };
        var exhausted = await Orchestrator(rejecting).ExecuteRoundAsync(trip, 8, [d1], Guid.NewGuid());
        Assert.Equal(RoundOutcome.Exhausted, exhausted.Outcome);
        Assert.Equal(
            MatchingRoundStatuses.Processing,
            (await Attempts().LoadRoundAsync(trip, 8))!.Status);
    }

    [Fact]
    public async Task Lost_Reconcile_Race_Does_Not_Mark_Round_Reserved()
    {
        // Lost-transition race, gate-ordered (no sleep): this worker polls
        // OWNED truth while a duplicate worker concurrently resolves the
        // attempt as REJECTED. The loser must report durable truth and must
        // NOT mark the round RESERVED for an unowned reservation.
        var trip = NewTrip();
        var d1 = Guid.NewGuid();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stub = new StubDriverClient
        {
            OnReserve = (_, _) => throw new TimeoutException("injected loss"),
            OnQuery = async (_, _, _) =>
            {
                entered.SetResult();
                await gate.Task;
                return ReservationReconcileOutcome.OwnedByThisReservation;
            },
        };
        var orch = Orchestrator(stub);

        var blocked = await orch.ExecuteRoundAsync(trip, 1, [d1], Guid.NewGuid());
        Assert.Equal(RoundOutcome.BlockedOnUnknown, blocked.Outcome);

        var reconciling = Task.Run(() => orch.ReconcileAsync(trip, 1, 0));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(60));
        var moved = await Attempts().TryTransitionAsync(
            trip, 1, 0,
            [MatchingAttemptStates.Unknown], MatchingAttemptStates.Rejected, "RESERVED_BY_OTHER");
        Assert.True(moved);
        gate.SetResult();
        var result = await reconciling;

        Assert.Equal(AttemptOutcome.Rejected, result.Outcome);
        var round = await Attempts().LoadRoundAsync(trip, 1);
        Assert.Equal(MatchingRoundStatuses.Processing, round!.Status);
        Assert.Null(round.SelectedDriverId);
    }

    [Fact]
    public async Task Lost_Reserve_Race_Does_Not_Mark_Round_Reserved()
    {
        // Same race on the reserve-win path: our reserve observes success
        // but the guarded transition loses to a concurrent conclusive
        // resolution. Durable truth wins; no round mark follows.
        var trip = NewTrip();
        var d1 = Guid.NewGuid();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ok = new DriverReserveResult(DriverReserveOutcomeKind.Reserved, null, 4);
        var stub = new StubDriverClient
        {
            OnVersion = (_, _) => Task.FromResult<DriverVersionRead?>(new DriverVersionRead(3, "AVAILABLE")),
            OnReserve = (_, _) => Task.FromResult(ok),
        };
        var orch = Orchestrator(stub);

        var first = await orch.ExecuteAttemptAsync(trip, 1, 0, d1);
        Assert.Equal(AttemptOutcome.Reserved, first.Outcome);
        var r = first.ReservationId;

        await using (var conn = new Npgsql.NpgsqlConnection(_matching.ConnectionString))
        {
            await conn.OpenAsync();
            await using var reset = new Npgsql.NpgsqlCommand(
                "UPDATE matching_attempts SET state = 'IN_FLIGHT', last_error_code = NULL " +
                "WHERE trip_id = @trip AND matching_round = 1 AND ordinal = 0", conn);
            reset.Parameters.AddWithValue("trip", trip);
            await reset.ExecuteNonQueryAsync();
            await using var unmark = new Npgsql.NpgsqlCommand(
                "UPDATE matching_rounds SET status = 'PROCESSING', " +
                "selected_driver_id = NULL, selected_reservation_id = NULL " +
                "WHERE trip_id = @trip AND matching_round = 1", conn);
            unmark.Parameters.AddWithValue("trip", trip);
            await unmark.ExecuteNonQueryAsync();
        }

        stub.OnReserve = async (_, _) =>
        {
            entered.SetResult();
            await gate.Task;
            return ok;
        };
        var racing = Task.Run(() => orch.ExecuteAttemptAsync(trip, 1, 0, d1));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(60));
        var moved = await Attempts().TryTransitionAsync(
            trip, 1, 0,
            [MatchingAttemptStates.InFlight], MatchingAttemptStates.Rejected, "RESERVED_BY_OTHER");
        Assert.True(moved);
        gate.SetResult();
        var result = await racing;

        Assert.Equal(AttemptOutcome.Rejected, result.Outcome);
        Assert.Equal(r, result.ReservationId);
        var round = await Attempts().LoadRoundAsync(trip, 1);
        Assert.Equal(MatchingRoundStatuses.Processing, round!.Status);
    }

    [Fact]
    public async Task Terminal_Round_Refuses_New_Work()
    {

        var trip = NewTrip();
        var stub = new StubDriverClient();
        var orch = Orchestrator(stub);

        var reserved = await orch.ExecuteRoundAsync(trip, 1, [Guid.NewGuid()], Guid.NewGuid());
        Assert.Equal(RoundOutcome.Reserved, reserved.Outcome);
        var callsAfterFirst = stub.Calls.Count;

        var replay = await orch.ExecuteRoundAsync(trip, 1, [Guid.NewGuid()], Guid.NewGuid());
        Assert.Equal(RoundOutcome.Reserved, replay.Outcome);
        Assert.Equal(reserved.ReservationId, replay.ReservationId);
        Assert.Equal(callsAfterFirst, stub.Calls.Count);
    }
}
