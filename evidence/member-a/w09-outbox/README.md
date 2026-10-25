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
- **Code commit under test: `6eb693a40bfb6b128cab22720034d05269b1234d`**
  (tree `eff9afa51d563f9b07cfb05f93e7dc44bc0ecfb2`)
- Initial implementation: `63f13a5544dd5b1caed357b62c3e60a188113ed4`,
  hardened by `d87c147` (outbox identity winner verification) and
  `20790f0` (relay publish-path gating), and extended by `6eb693a`
  (concurrent same-obligation discovery proof; test-only, no production
  change).

Verify the tree locally:

```bash
git rev-parse 6eb693a40bfb6b128cab22720034d05269b1234d^{tree}
# eff9afa51d563f9b07cfb05f93e7dc44bc0ecfb2
```

## Provenance model

The evidence commits only add or edit files under `evidence/` on top of the
verified code commit. `reproduce.sh` therefore accepts any checkout whose
source is *provably identical* to the verified revision: it compares the git
tree hash of every tracked top-level path (everything except `evidence/`)
between `HEAD` and the verified code commit, and refuses to run (exit 2) on
any drift or on a working tree that is dirty outside `evidence/`. Source
provenance verification is never skipped; the run header records both the
verified code commit and the actual `HEAD` it executed from.

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
7. **Readiness gating.** The relay loop re-checks migrations each cycle and
   gates publication on its own publish-path probe. Consumer connectivity is
   a different failure domain from publication safety: while the inbound
   consumers are disconnected, the worker independently opens a connection and
   re-declares the idempotent topology, so a consumer-only fault cannot stall
   outgoing delivery; a genuinely unreachable broker still blocks with rows
   `PENDING` and `attempt_count = 0` instead of burning the shared retry
   budget into `FAILED_VISIBLE`.
8. **Second concurrency fence with winner verification.** A deterministic
   `messageId` collision is translated to `TripConcurrencyConflictException`
   inside `TripDbContext.SaveChanges/SaveChangesAsync` only after the
   committed winner is read back and proven to carry the same message
   contract (type/exchange/routing key) as the staged intent. A foreign or
   corrupt row occupying a Trip intent identity surfaces as the original
   database fault instead of being replayed as a lost race.

## Results (local, Release)

Verified at code commit `6eb693a` (see Provenance model above; the run logs
live in `outputs/` and were produced by `reproduce.sh` at that source state).

| Lane | Command | Result | Log |
| --- | --- | --- | --- |
| Solution build | `dotnet build RideHailing.Distributed.slnx -c Release --no-incremental` | **0 errors**, 1 pre-existing warning (`xUnit2029` in Driver integration tests, file not touched by this change) | `outputs/solution-build-release.txt` |
| Full solution tests | `dotnet test RideHailing.Distributed.slnx -c Release --no-build` | **913 passed, 0 failed, 1 skipped** (exit 0) | `outputs/solution-tests-release.txt` |
| Trip unit | included above | **272 passed** | `outputs/solution-tests-release.txt` |
| Trip integration | included above | **143 passed** | `outputs/solution-tests-release.txt` |
| W09 Outbox proofs | `--filter FullyQualifiedName~TripW09` (outbox + relay-readiness classes) | **17 passed** | `outputs/w09-outbox-tests.txt` |
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
TripService.IntegrationTests        143 passed
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
| Concurrent deliveries discover the same obligation (inside the consumer's explicit transaction) | one committed obligation; the loser classifies as bounded Transient retry and its receipt claim rolls back | EF's automatic savepoint restores the open transaction so the winner readback verifies on a valid snapshot; redelivery converges as domain replay with no second intent | `ConcurrentLateReservations_DiscoveringSameReleaseObligation_AreTransientRedelivery_WinnerStaysIntact` |
| Broker unavailable at HTTP create | MATCHING + `PENDING` intent, HTTP 201 | broker restarted → relay publishes the same id; replay does not enqueue twice | `BrokerOutage_CommitsMatchingWithDurableIntent_ThenRelayPublishesSameIdAfterRecovery` |
| Replay / stale / terminal transition | no new intent | handler replay flag + domain generation guards | `ReplayPaths_NeverAppendSecondIntent`, `RejectedAndStaleTransitions_AppendNoIntent` |
| Foreign row under a deterministic intent identity | database fault, business state unchanged, no replay-as-race | winner readback compares type/exchange/routing before translating the duplicate key | `IdentityCollisionWithForeignContract_IsADataFault_NotAReplayableRace` |
| Consumers disconnected, broker healthy | relay still publishes; no consumer-domain stall | independent publish-path probe (connect + re-declare topology) | `ConsumersDisconnected_BrokerHealthy_RelayStillPublishes` |
| Broker unreachable, consumers disconnected | `PENDING`, `attempt_count = 0`, never claimed | probe fails → no relay cycle; recovery publishes afterwards | `BrokerUnreachable_WhileConsumersDisconnected_KeepsPendingWithoutAttemptOrClaim` |

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

From any checkout of the PR branch (no historical worktree or developer-home
path is required):

```bash
git clone https://github.com/minhqng/ride-hailing-microservices-system
cd ride-hailing-microservices-system
git checkout feature/A-W09-trip-outbox   # or any PR head with identical source

evidence/member-a/w09-outbox/reproduce.sh
```

Requirements: Docker (the integration fixtures start disposable
`postgres:18.6` and `rabbitmq:4.3.6-management` containers) and the .NET 10
SDK resolvable as `dotnet` on `PATH`, at `$HOME/.dotnet-sdk/dotnet`, or via
the `DOTNET` environment variable.

The script verifies source equivalence with the verified code commit first
(see Provenance model) and then runs, writing fresh logs to
`outputs/repro-<timestamp>/` (gitignored): solution Release build, full
solution tests, the `TripW09` integration proofs (outbox + relay readiness),
the W08 messaging regressions, and the W09 intent identity unit proofs.

The curated lane logs in `outputs/*.txt` were produced the same way at code
commit `6eb693a`; the earlier bundle at `20790f0` (912 passed) and the
historical first implementation run at `63f13a5` (909 passed) are superseded
by this bundle.

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
- **Accepted boundary — CreateRide → intake window.** `POST /trips` commits
  the authoritative `CREATED` row (W4 idempotency anchor) and then
  `TripRideRequestedIntake` commits `MATCHING + RideRequestedV1` in a second
  local transaction. A crash between the two leaves a stranded `CREATED` ride
  with no external obligation (no reservation, no driver, no message). This is
  the pre-W09 workflow shape, unchanged by W09: recovery is the idempotent
  HTTP replay (same `Idempotency-Key`), a durable local intake failure is
  surfaced as 503 `MATCHING_INTENT_PENDING` with `Retry-After`, and a client
  that received 2xx always has the intent committed (it is created before the
  response is written). Doc 05A §17 scopes W09 atomicity at the
  StartMatching+RideRequested pair; an automatic stranded-`CREATED` recovery
  scanner is not a W09 obligation (W10 Saga / W11 recovery territory).
- **Accepted boundary — cross-owner consumers.** `CancelMatchingRoundV1` and
  `ReleaseDriverReservationV1` are new W9 wire obligations; no Matching or
  Driver consumer for `matching.commands.q` / `driver.commands.q` exists yet
  (B-side W9/W10 work). REL-05's consumer-effect-once proof here covers
  Trip's own durable-receipt path (a Trip-consumed `DriverReservedV1`
  delivered twice); downstream consumer idempotency for the two command types
  must be proven by their owners and is not claimed here.
- **Upgrade boundary.** Cleanup intents committed before W09 (pre-upgrade
  trips) are intentionally not backfilled into the outbox; W08 never
  published these commands, so no obligation regresses. Only obligations
  added by transitions after the upgrade are enqueued.
- The Trip service's existing W8 broker-outage HTTP test was rewritten to the
  W9 contract (commit-then-publish). Its old expectation (HTTP 503 while the
  broker is down) is intentionally obsolete: the create path must now succeed
  with a durable pending intent.
- No W10 Saga/Payment execution, no generic Outbox framework, no CDC, no
  distributed transaction, and no changes to `plans/` or other members'
  branches are part of this change.
