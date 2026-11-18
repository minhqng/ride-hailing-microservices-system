# Final verification record

This page records the verification performed for the `v1.0.1` release. It
separates **what was executed on this source revision** from historical
measurements retained in evidence bundles. Numbers are copied from the run
outputs, not from an earlier branch. The `v1.0.0` counts remain in its evidence
bundle and are not rewritten here.

## Release revision and environment

- Release: `v1.0.1` (annotated tag; exact commit and tree are recorded in
  [evidence/release/v1.0.1/manifest.json](../evidence/release/v1.0.1/manifest.json)
  and on the GitHub Release page).
- Host: openSUSE Tumbleweed (Linux 7.3.4), Docker 29.9.2, Compose 5.7.1,
  Python 3.13.16; builds and tests run in the pinned .NET 10 SDK container.
- Providers: PostgreSQL 18.6, RabbitMQ 4.3.6 and Redis 8.10.2, pinned by
  digest in `compose.yaml` and the evidence manifests.
- Test transports are real processes, real PostgreSQL, real RabbitMQ and real
  Redis. No mock replaces an owning authority in the counts below.

## 1. Full .NET regression — PASS

Command (equivalent to `scripts/test/quick.sh` without the container mount):

```bash
dotnet restore RideHailing.Distributed.slnx
dotnet build RideHailing.Distributed.slnx -c Release --no-restore
dotnet test RideHailing.Distributed.slnx -c Release --no-build \
  --logger trx --results-directory TestResults/full-release
python scripts/test/assert-trx.py --results-dir TestResults/full-release \
  --min-passed 1200 --min-files 13 --max-skipped 1 \
  --allowed-skip RideHailing.IdentityService.IntegrationTests.AuthNegativeTests.RejectedRequestsDoNotMutateAuthoritativeState \
  --required-suites-file scripts/test/required-suites.txt
```

| Suite | Passed | Failed | Skipped |
|---|---:|---:|---:|
| RideHailing.NotificationWorker.UnitTests | 13 | 0 | 0 |
| RideHailing.NotificationService.IntegrationTests | 16 | 0 | 0 |
| RideHailing.InvariantAudit.Tests | 20 | 0 | 0 |
| RideHailing.PaymentService.UnitTests | 38 | 0 | 0 |
| RideHailing.PaymentService.IntegrationTests | 37 | 0 | 0 |
| RideHailing.Platform.IntegrationTests | 76 | 0 | 0 |
| RideHailing.MatchingService.UnitTests | 115 | 0 | 0 |
| RideHailing.DriverService.IntegrationTests | 120 | 0 | 0 |
| RideHailing.DriverService.UnitTests | 132 | 0 | 0 |
| RideHailing.MatchingService.IntegrationTests | 204 | 0 | 0 |
| RideHailing.TripService.IntegrationTests | 246 | 0 | 0 |
| RideHailing.TripService.UnitTests | 327 | 0 | 0 |
| RideHailing.IdentityService.IntegrationTests | 0 | 0 | 1 (documented opt-in) |
| **Total** | **1344** | **0** | **1** |

TRX gate result: `PASS: TRX files=13 (executed suites=13), notexecuted=1,
passed=1344, suite files=13`. The single skip is
`AuthNegativeTests.RejectedRequestsDoNotMutateAuthoritativeState`, a
deliberately opt-in theory; the dedicated Identity negative lane runs it
separately and reports 7/7 passing.

`scripts/test/required-suites.txt` also carries a per-assembly minimum
(roughly 90% of the counts above). The aggregate `--min-passed 1200` proves
the suite total, while the per-assembly floors catch a single suite that
silently stops discovering tests.

Build: `0 errors`, `0 warnings`. The final tree builds clean: the previously
reported analyzer warnings (three nullable-argument, one nullable-dereference,
five duplicate-type and nine xUnit style diagnostics) were resolved rather than
suppressed, and the unreachable Payment command handlers that produced them were
removed. No warning category is suppressed anywhere in the solution.

## 2. Static and repository gates — PASS

| Gate | Result |
|---|---|
| Secret scan (`scripts/security/scan-secrets.py`) | PASS — 576 tracked paths, no high-confidence secret |
| Hygiene self-test (`scripts/test/hygiene-check.sh --self-test`) | PASS — 24 pattern verdicts |
| Source-anchor fixtures (`verify-source-anchor.test.sh`) | PASS — 12 fixture cases |
| Compose render, development files | PASS (`docker compose config --quiet`) |
| Compose render, release overlay with all secrets | PASS |
| Release overlay without secrets | FAILS CLOSED with `must be set for the release overlay` |

## 3. Production release overlay smoke — PASS

`python scripts/test/release-smoke.py` starts the Production overlay in an
isolated Compose project, applies the real Trip EF migrations externally,
runs real authenticated journeys through the public HTTP surface, inspects
independent owning databases and tears down its own resources.

Local release run: 2026-11-18, Compose project `ride-release-v101`, source
commit recorded in
[evidence/release/v1.0.1/manifest.json](../evidence/release/v1.0.1/manifest.json).
This is the authoritative local run for the verified tree; the repository CI
workflow repeats the same gates for every exact-tree checkout.

- External migration: `dotnet ef database update`, 11 migrations applied.
- Broker topology observed: 12 exchanges and 19 queues.
- Checks executed: 19. All PASS:
  `RELEASE-SECRETS-*` (fail-closed), `RELEASE-RENDER`, `PRODUCTION-ENVIRONMENT`,
  `MESSAGING-ENABLED`, `PRODUCTION-RECONCILIATION`, `NO-PRODUCTION-SIMULATOR`,
  `RELEASE-SERVICES-READY`, `GATEWAY-AUTH`, `UNAUTHENTICATED-REJECTED`,
  `PUBLIC-DRIVER-LIFECYCLE`, `COMPLETED-JOURNEY`, `RECIPIENT-INBOXES`,
  `CANCELLED-VOID-JOURNEY`, `DRIVER-REJECT-JOURNEY`,
  `NOTIFICATION-OUTAGE-ISOLATION`, `REJECT-CRASH-REPLAY`, `AUTHORITY-AUDIT`,
  `TEARDOWN-ISOLATED`.
- Scenarios:
  - A — authenticated Driver rejects the exact assignment; Trip becomes
    `MATCH_FAILED` / `DRIVER_REJECTED`; the exact Driver release completes; no
    payment starts; the Rider can request another ride.
  - B — accept → authorize → start → complete → capture; both roles read their
    own inbox; four recipient notifications for the completed trip.
  - C — Notification worker is stopped during the ride; Trip/Driver/Payment
    still complete; after restart the queued notifications are delivered once,
    with no duplicates.
  - D — reject commit followed by a real Trip process stop/restart; the
    Outbox/reconciliation resumes; the replay is exact and idempotent.
- Final authority audit: 6 Trips (`COMPLETED`, `CANCELLED`, `MATCH_FAILED`),
  1 `AVAILABLE` Driver with no reservation, 6 matching rounds, unique Payment
  operation keys with matching provider effects, and 25 unique recipient
  notifications containing no token or payment-secret material.
- Teardown: PASS — the unrelated sentinel container was preserved and zero
  project containers remained.

The raw result of the release run is curated to
[evidence/release/v1.0.1/](../evidence/release/v1.0.1/) with the command,
environment, checks and source anchors.

## 3a. Live demonstration on a clean stack — PASS

The authoritative Vietnamese runbook `docs/demo.md` was executed end to end
on a freshly rebuilt development stack (`docker compose down -v`, then
`--profile obs up -d --build`) on 2026-11-18. Part C steps 1–13 and scenarios
D.1–D.8, D.11 and D.12, plus a two-candidate matching probe, all passed. The
final invariant audit read `cleanup_pending = 0`, `non_published_outbox = 0`,
`reserved_rounds_with_unresolved_attempt = 0` and
`duplicate_notifications = 0`.

D.11 cancelled while Payment was stopped: the saga reached `COMPENSATED`,
`AUTHORIZE` and `VOID` both succeeded with the same amount, and no `CAPTURE`
was produced. D.12 pre-seeded a terminal decline: `AUTHORIZE` ended
`DECLINED`, the trip became `CANCELLED`, `ReleaseDriverReservationV1` carried
the exact driver and reservation, and all three cleanup obligations were
confirmed. The two-candidate probe confirmed that a settled `RESERVED` round
closes the unselected attempt as `CANCELLED` (`NOT_SELECTED`), so
`CancelMatching` converges. A `DRIVER_RESERVED` cancellation now owns only
`CancelMatching` and `ReleaseReservation`: accept is the transition that
requests authorization, so no Payment effect exists to reconcile there.

Curated results:
[evidence/release/v1.0.1/final-state/demo-run-summary.json](../evidence/release/v1.0.1/final-state/demo-run-summary.json).

## 4. Course minimum test mapping

The course requires at least 8 functional, 2 concurrency/conflict, 2
fault/recovery, 2 security/invalid-data tests and 1 performance experiment.
The repository exceeds the minimum with executable, real-infrastructure
tests. Representative proofs:

| Category | Representative tests |
|---|---|
| Functional (≥8) | `CreateRideHttpTests`, `CreateRideComponentTests`, `TripLifecycleComponentTests`, `TripDriverLifecycleHttpTests`, `TripDriverAgreementTests`, `TripDriverRejectTests`, `PaymentPersistenceTests`, `NotificationInboxTests`, `AuthNegativeTests` |
| Concurrency / conflict (≥2) | `DriverReserveConcurrencyTests`, `CreateRideConcurrencyTests`, `TripConcurrencyComponentTests`, `PaymentConcurrencyTests`, `AtomicDispatchFenceTests`, `MatchingAttemptDriverUniquenessTests`, `ReconciliationRaceTests` |
| Fault / recovery (≥2) | `TripFaultProcessTests`, `CrashAfterReserveRecoveryTests`, `LostReserveResponseHttpTests`, `OutboxRelayUnitTests`, `PaymentRecoveryTests`, `PaymentReconciliationTests`, `RedisOutageRecoveryTests`, `BacklogRecoveryTests`, `ProductionMatchingLifecycleRecoveryTests` |
| Security / invalid data (≥2) | `AuthNegativeTests` (7 dedicated identity negatives), `DriverAuthorizationExposureTests`, `DriverDbOwnershipTests`, `MatchingDbOwnershipTests`, `MessageContractTests`, `TripApiBoundaryTests`, `TripHttpContractTests` |
| Performance / scalability (≥1) | E01–E04 campaigns and the E04 backlog-recovery run (see §6) |

## 5. DS03 requirement matrix

| Requirement | Implementation | Proof |
|---|---|---|
| ≥4 functioning business microservices | Identity, Driver, Trip, Matching, Payment (5) + Notification worker + Gateway | Separate projects, Dockerfiles and Compose services; `/info` per service |
| Independent service processes | Each has its own container, configuration, database and health endpoints | `compose.yaml`, `docker compose ps`, release smoke |
| REST/gRPC communication | HTTP minimal APIs; YARP gateway; typed HTTP clients (Matching→Driver, Trip→Driver directory) | Endpoint source; `ReserveTraceContinuityTests`; release smoke journeys |
| Asynchronous message queue | RabbitMQ quorum queues, exchanges, bindings, publisher confirms, manual ACK | `RideTopology`, `ConfirmedPublisher`, `ManualAckConsumer`, `topology-smoke.py` |
| Service-owned databases / isolated schemas | One logical database and role per service | `deploy/postgres/init/01-roles-dbs.sh`, `DbIsolationTests`, `DriverDbOwnershipTests`, `MatchingDbOwnershipTests` |
| No cross-service production DB access | Each service connects only to its owned connection string; no shared context | Ownership tests + role grants |
| Service identities and contracts | Per-service `ServiceInstanceId`, DNS names, versioned message envelopes, stable IDs | `MessageEnvelope`, `RideTopology`, docs 00–03 |
| Multiple concurrent requests | Async intake, OCC reservation, concurrency and contention tests | `DriverReserveConcurrencyTests`, `CreateRideConcurrencyTests`, E01/E02 |
| Registration, login and roles | Identity `/auth/register`, `/auth/login`, `/connect/token`; Rider/Driver/Service roles | `IdentityService` source; `AuthNegativeTests`; release smoke |
| Driver status and simulated location | Driver online/offline and location endpoints; Redis GEO projection | `DriverEndpoints`, `DriverLocationIngressTests`, release smoke |
| Passenger ride creation | `POST /trips` with durable idempotency key | `CreateRideEndpoint`, `CreateRideHttpTests`, release smoke |
| Driver search and selection | Matching candidate discovery, deterministic ranking | `MatchingPipelineTests`, `ProductionMatchingWorkflowTests`, release smoke |
| Driver acceptance or rejection | Authenticated accept/reject with exact `(driverId, reservationId)` fencing | `TripDriverDecisionTests`, `TripDriverRejectTests`, release smoke Scenario A |
| No simultaneous assignment to two trips | Driver PostgreSQL OCC reservation; stale release fencing | `DriverReserveConcurrencyTests`, `DriverRaceFencingTests`, E01 |
| Trip lifecycle through completion/cancellation | Trip FSM with terminal monotonicity, cancellation cleanup and Saga compensation | `TripLifecycleComponentTests`, `TripCancellationMatrixTests`, release smoke |
| Fare calculation and simulated payment | Deterministic fare snapshot; durable payment operations with `operationKey` | `DeterministicFareCalculatorTests`, `FareSnapshotTests`, `PaymentPersistenceTests`, release smoke |
| Passenger and driver notifications | Outbox intents → Notification worker → per-recipient inbox; JWT owner isolation | `NotificationInboxTests`, `TripNotificationOutboxTests`, release smoke |
| Event identity, idempotency and deduplication | `messageId`, `correlationId`, `processed_messages`, payment `operationKey` | `MessageContractTests`, `W9DedupTests`, `PaymentReplayTests` |
| Bounded retry and DLQ | Finite delivery attempts, `<queue>.dead`, quarantine queue | `FailurePolicyTests`, `TopologySmoke`, `BrokerConsumeTests` |
| Correlation IDs | W3C trace context propagation; correlation ID logging/echo | `TraceHeaderPropagation`, `ReserveTraceContinuityTests` |
| Real Saga or compensation | Persisted `RideSaga` with Authorize/Capture/Void and compensation | `RideSagaTransitionTests`, `TripSagaTests`, release smoke A/C |
| Concurrency control and final-state consistency | OCC, guarded transitions, invariant auditor | Concurrency suites, `TripInvariantAuditor`, invariant audit |
| Failure detection, recovery and restart | Fault points, process crash/restart, Outbox recovery, reconciliation workers | `TripFaultProcessTests`, `CrashAfterReserveRecoveryTests`, real process crash/restart runs, release smoke C/D |
| Authentication and access control | RSA JWT validation, role checks, service permissions, owner isolation | `AuthNegativeTests`, `DriverAuthorizationExposureTests`, security gate 55 checks |
| Input validation and message/input-size limits | Fluent validation at boundaries; Kestrel body limits; bounded payload parsing | `TripApiBoundaryTests`, `DriverLocationIngressTests`, `DriverIngressLimits` |
| Secure secret configuration, no hardcoded credentials | `.env.example` placeholders only; release overlay fails closed; secret scanner | `scan-secrets.py` PASS; release overlay negative render PASS |

## 6. Performance and high-availability evidence

The measured campaigns were executed on a single host with synthetic load. The
raw harness and samples are not part of the final deliverable; the results and
their validity limits are summarized here.

- **E01 — Driver contention.** Three repetitions at each of 1/10/25/50/100
  contenders; 558 real authenticated HTTP requests, 15 winners, 543 expected
  conflicts. Every point has exactly one durable winner and one reserve intent,
  followed by an exact authenticated release.
- **E02 — Authenticated public intake.** 12 runs of 30 seconds, 16,661
  classified requests, zero dropped iterations, system errors or authentication
  failures; every success maps to one Trip/idempotency resource.
- **E03 — Asynchronous intake replicas.** 9 runs, 3,600 unique durable
  intakes, 3,960 deliveries/ACKs including 360 deliberate duplicate deliveries
  with no duplicate durable rounds.
- **E04 — Controlled backlog recovery.** 3 runs, 104 unique confirmed
  intakes + 3 duplicates, all 107 ACKed, all 104 unique rows reach `NO_DRIVER`
  with exactly 104 result Outbox effects and no fabricated reservation.
- **HA.** Quorum queues with three members; a primary-loss run SIGKILLs the
  leading broker while `matching.commands.q` is active. All 19 critical quorum
  queues retain two online members and live leaders across three journeys,
  returning to 3/3 after restoration. A broker-full-stop run holds new
  authorization intents `PENDING` with null `published_at`, then resumes
  completion and exact cancellation compensation after restoration.

These are single-host measurements. They do not establish production capacity,
multi-region HA or linear scaling; the evidence bundles state their own
validity conditions and retain failed/rejected attempts.

## 7. Evidence integrity

- Every retained bundle has a manifest with the source commit, tree, command,
  environment and artifact hashes.
- The final release record maps the release commit/tree to the evidence used
  for this verification. Historical bundles keep their original source SHA,
  including bundles produced before the tree-identical attribution rewrites.
- No raw secrets, bearer tokens, database volumes or full engine streams are
  committed.

## 8. Known verification gaps

- Human team rehearsal, oral defense and peer sign-off are course-process
  activities performed by the team, not automated by this repository.
- Payment is a simulated provider; the provider boundary is real PostgreSQL
  and durable operations, not a card network.
- Notification delivery is in-app only.
- CI billing availability is external; the local Release build,
  real-infrastructure suites, release smoke and clean-clone checks are the
  release-gate substitutes when a hosted CI run is unavailable.
