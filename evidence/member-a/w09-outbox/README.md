# Member A W09 — Trip Transactional Outbox (local evidence)

## Scope

Implements A-W09-01..10 for the Trip service: a committed Trip business
transition never loses the outgoing message intent that transition requires,
even if the process dies or RabbitMQ is unavailable after the local commit.

This directory contains **local verification output only**. It is not CI
output, and it is not a peer signoff. See [Caveats](#caveats).

- Owner: A (Trip lifecycle / Outbox producer semantics)
- Shared platform: C's `RideHailing.Messaging.Outbox` relay (claim →
  publish-confirm → mark → retry → backlog metrics)
- Branch: `feature/A-W09-trip-outbox`
- Base: `7a0575a51a89ba32bd3017afd779355ed2da9576`
  (`origin/MemC-W9-Distributed`, PR #35 head)
- Main lineage base: `ba5bb40615564ab022ddb0ff5e933bde93ee15a1`
- **Code commit under test: `63f13a5544dd5b1caed357b62c3e60a188113ed4`**
- Code tree: `6e4369c1d314d55821f9e00ae455b1e1ad64b1bc`

Verify the tree locally:

```bash
git rev-parse 63f13a5544dd5b1caed357b62c3e60a188113ed4^{tree}
# 6e4369c1d314d55821f9e00ae455b1e1ad64b1bc
```

All commands below were run in the worktree at that commit with no source
changes relative to it. The evidence directory itself is the only addition
after the runs.

## Environment

| Item | Value |
| --- | --- |
| OS | Linux 7.3.0-1-default |
| .NET SDK | 10.0.401 |
| Test DB | `postgres:18.6` (Testcontainers, disposable per run) |
| Broker | `rabbitmq:4.3.6-management` (Testcontainers) |
| Docker | 29.7.2-ce |
| Configuration | Release (`-c Release`), test projects run with `--no-build` after the Release build |

## What the change does

1. **Atomic intent commit.** `StartMatchingHandler`, `CancelRideHandler`,
   `RecordDriverReservationHandler` and `TripMessageHandler` stage a pending
   `outbox_messages` row on the same `TripDbContext` and commit it in the same
   `SaveChangesAsync` as the Trip state change.
2. **Broker-free create path.** `POST /trips` no longer publishes directly.
   `TripRideRequestedIntake` opens round 1 and commits MATCHING +
   `RideRequestedV1` locally; the response never depends on RabbitMQ.
3. **Stable identity.** `messageId` is derived from the committed business
   fact, not from time or retry:
   - `trip:{tripId:N}:ride-requested:{round}` (W8-frozen)
   - `trip:{tripId:N}:cancel-round:{round}`
   - `trip:{tripId:N}:release:{reservationId:N}`
   `CorrelationId` stays `trip.TripId.ToString("D")`.
4. **Cancellation wire commands.** `CancelMatchingRoundV1` →
   `matching.cancel-round.v1` and `ReleaseDriverReservationV1` →
   `driver.release-reservation.v1`, both on `ride.commands`, derived from the
   persisted `TripCleanupIntent`. `ReconcilePayment` stays Trip-durable and is
   **not** published (W10 owns Payment commands).
5. **Shared relay, Trip-owned database.** The Trip host resolves C's
   `NpgsqlOutboxStore` / `RabbitOutboxPublisher` / `OutboxRelay` against the
   same `NpgsqlDataSource` as EF. Claim/publish/confirm/mark/retry and the
   `outbox.pending`, `outbox.oldest_pending.age`, `outbox.failed_visible`
   instruments stay C-owned.
6. **Schema ownership.** EF migration `20261025031423_W09_TripOutbox` creates
   `outbox_messages` with exactly C's column set, PK name, check-constraint
   names and partial indexes. The relay never calls `EnsureSchemaAsync` in the
   Trip host.
7. **Readiness gating.** The relay loop re-checks migrations and broker/
   consumer connectivity each cycle; while the broker is down rows stay
   `PENDING` with `attempt_count = 0` instead of burning the shared retry
   budget into `FAILED_VISIBLE`.
8. **Second concurrency fence.** A deterministic `messageId` collision is
   translated to `TripConcurrencyConflictException` inside
   `TripDbContext.SaveChanges/SaveChangesAsync`, so a concurrent same-
   transition writer reloads and observes the winner rather than returning a
   500 or committing orphan state.

## Results (local, Release)

| Lane | Command | Result | Log |
| --- | --- | --- | --- |
| Solution build | `dotnet build RideHailing.Distributed.slnx -c Release --no-incremental` | **0 errors**, 1 pre-existing warning (`xUnit2029` in Driver integration tests, file not touched by this change) | `outputs/solution-build-release.txt` |
| Full solution tests | `dotnet test RideHailing.Distributed.slnx -c Release --no-build` | **909 passed, 0 failed, 1 skipped** (exit 0) | `outputs/solution-tests-release.txt` |
| Trip unit | included above | **272 passed** | `outputs/solution-tests-release.txt` |
| Trip integration | included above | **139 passed** | `outputs/solution-tests-release.txt` |
| W09 Outbox proofs | `--filter FullyQualifiedName~TripW09OutboxTests` | **13 passed** | `outputs/w09-outbox-tests.txt` |
| W08 messaging regressions | `--filter FullyQualifiedName~TripW08MessagingTests` | **18 passed** | `outputs/w08-messaging-tests.txt` |
| W09 intent identity unit proofs | `--filter FullyQualifiedName~TripOutboxIntentTests` | **5 passed** | `outputs/w09-intent-unit-tests.txt` |

Per-project totals from `outputs/solution-tests-release.txt`:

```text
MatchingService.UnitTests            98 passed
DriverService.UnitTests             117 passed
TripService.UnitTests               272 passed
PaymentService.UnitTests             25 passed
IdentityService.IntegrationTests      0 passed, 1 skipped (pre-existing opt-in skip)
DriverService.IntegrationTests       60 passed
MatchingService.IntegrationTests    112 passed
PaymentService.IntegrationTests      26 passed
Platform.IntegrationTests            60 passed
TripService.IntegrationTests        139 passed
SystemTests                         no tests discovered (solution-wide "no test available"; unchanged)
```

The one skipped test is
`IdentityService.IntegrationTests.AuthNegativeTests.RejectedRequestsDoNotMutateAuthoritativeState`,
which was already opt-in/skipped before this change. The SystemTests project
contains no discoverable tests in the current tree; `dotnet test` reports that
and continues with exit 0.

## Dual-write crash matrix (A-W09-08)

| Faultpoint | Expected durable state after fault | Recovery proof | Test |
| --- | --- | --- | --- |
| Before local COMMIT | no business transition, no Outbox row (safe retry) | injected `SaveChanges` failure; later retry commits both exactly once | `FailedSave_RollsBackBusinessTransitionAndIntent` |
| After local COMMIT, before publish | business transition + `PENDING` row (claim may remain until lease expiry) | new relay instance reclaims after lease, publishes the **same** `messageId`; no duplicate row | `CrashBeforePublish_LeavesDurablePendingRow_ThenRestartPublishesSameIdentity` |
| Publish accepted, local mark lost | `PENDING` row, no `published_at` | new relay republishes same identity; Trip's durable receipt applies the downstream effect once | `LostMarkAfterAcceptedPublish_RepublishesSameIdentity_ThenSettles` |
| Publish repeatedly fails | `FAILED_VISIBLE` after `MaxAttempts`, `last_error_code` set | backlog metric/visibility; identity and payload unchanged | `FailedPublication_BecomesFailedVisible_AndRelayMetricsAreEmitted` |
| Concurrent same transition (OCC) | one winner with exactly one intent; loser observes committed state | Version fence + deterministic PK fence; replay appends nothing | `ConcurrentStartMatching_OneWinner_OneIntent_NoOrphan`, `DuplicateLogicalIntent_BlocksStateCommit_ThenLeavesOneRow` |
| Broker unavailable at HTTP create | MATCHING + `PENDING` intent, HTTP 201 | broker restarted → relay publishes the same id; replay does not enqueue twice | `BrokerOutage_CommitsMatchingWithDurableIntent_ThenRelayPublishesSameIdAfterRecovery` |
| Replay / stale / terminal transition | no new intent | handler replay flag + domain generation guards | `ReplayPaths_NeverAppendSecondIntent`, `RejectedAndStaleTransitions_AppendNoIntent` |

## Observability

The Trip-created rows are visible through C's shared instruments without
adding Trip-specific metrics:

- `outbox.pending` (gauge) — pending backlog size.
- `outbox.oldest_pending.age` (gauge, seconds) — age of the oldest pending row.
- `outbox.failed_visible` (gauge) — rows requiring operator action.
- `outbox.claim`, `outbox.publish`, `outbox.mark`, `outbox.confirm.duration` —
  counters/histogram emitted by the shared relay.

`TripIntent_RoundTripsThroughSharedStore_AndStatsExposeBacklog` reads the
backlog through C's `NpgsqlOutboxStore.GetStatsAsync()` after a Trip EF insert
and verifies the counters fall back to zero after a shared-store mark.
`FailedPublication_BecomesFailedVisible_AndRelayMetricsAreEmitted` captures the
instruments with a `MeterListener` and asserts no message identity is used as a
metric dimension.

## Reproduce

```bash
cd /tmp/opencode/w09-trip-outbox
git checkout 63f13a5544dd5b1caed357b62c3e60a188113ed4
# this environment keeps the SDK outside PATH:
export PATH="$HOME/.dotnet-sdk:$PATH"

dotnet build RideHailing.Distributed.slnx -c Release --no-incremental
dotnet test RideHailing.Distributed.slnx -c Release --no-build

dotnet test tests/Integration/RideHailing.TripService.IntegrationTests/RideHailing.TripService.IntegrationTests.csproj \
  -c Release --no-build --filter "FullyQualifiedName~TripW09OutboxTests"
dotnet test tests/Integration/RideHailing.TripService.IntegrationTests/RideHailing.TripService.IntegrationTests.csproj \
  -c Release --no-build --filter "FullyQualifiedName~TripW08MessagingTests"
dotnet test tests/Unit/RideHailing.TripService.UnitTests/RideHailing.TripService.UnitTests.csproj \
  -c Release --no-build --filter "FullyQualifiedName~TripOutboxIntentTests"
```

Docker must be available: the integration fixtures start disposable
`postgres:18.6` and `rabbitmq:4.3.6-management` containers.

`./reproduce.sh` runs the same lane and writes fresh logs to
`outputs/repro-<timestamp>/`.

## Caveats

- **Local evidence, not CI.** No CI run is claimed. These logs were produced by
  the author on the commit above.
- **REL-04 fault injection.** The crash is executed through C's documented
  `OutboxRelayOptions.BeforePublish` seam, which throws after claim and before
  publish; the row remains claimed/PENDING and a **new relay instance** with an
  expired lease reclaims and publishes it. This is the shared platform's
  deterministic REL-04 faultpoint, not an OS-level `SIGKILL` automation.
  Full process-kill automation is A-W11-02 and is not claimed here.
- **Peer rerun (A-W09-09) is not performed in this evidence.** C/B rerun must
  be recorded separately; this directory must not be read as peer signoff.
- The Trip service's existing W8 broker-outage HTTP test was rewritten to the
  W9 contract (commit-then-publish). Its old expectation (HTTP 503 while the
  broker is down) is intentionally obsolete: the create path must now succeed
  with a durable pending intent.
- No W10 Saga/Payment execution, no generic Outbox framework, no CDC, no
  distributed transaction, and no changes to `plans/` or other members'
  branches are part of this change.
