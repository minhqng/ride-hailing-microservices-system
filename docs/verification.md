# Final verification record

This page records the verification performed for the `v1.0.0` release. It
separates **what was executed on this source revision** from historical
measurements retained in evidence bundles. Numbers are copied from the run
outputs, not from an earlier branch.

## Release revision and environment

- Release: `v1.0.0` (annotated tag; exact commit and tree are recorded in
  [evidence/release/v1.0.0/manifest.json](../evidence/release/v1.0.0/manifest.json)
  and on the GitHub Release page).
- Host: Windows 11, Docker Desktop 29.8.2 (Linux engine), .NET SDK 10.0.403,
  Python 3.15.2, Git for Windows.
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
| RideHailing.B12Audit.Tests | 20 | 0 | 0 |
| RideHailing.PaymentService.UnitTests | 38 | 0 | 0 |
| RideHailing.PaymentService.IntegrationTests | 37 | 0 | 0 |
| RideHailing.Platform.IntegrationTests | 76 | 0 | 0 |
| RideHailing.MatchingService.UnitTests | 115 | 0 | 0 |
| RideHailing.DriverService.IntegrationTests | 120 | 0 | 0 |
| RideHailing.DriverService.UnitTests | 132 | 0 | 0 |
| RideHailing.MatchingService.IntegrationTests | 202 | 0 | 0 |
| RideHailing.TripService.IntegrationTests | 246 | 0 | 0 |
| RideHailing.TripService.UnitTests | 326 | 0 | 0 |
| RideHailing.IdentityService.IntegrationTests | 0 | 0 | 1 (documented opt-in) |
| **Total** | **1341** | **0** | **1** |

TRX gate result: `PASS: TRX files=13 (executed suites=13), notexecuted=1,
passed=1341, suite files=13`. The single skip is
`AuthNegativeTests.RejectedRequestsDoNotMutateAuthoritativeState`, a
deliberately opt-in theory; the dedicated Identity negative lane runs it
separately and reports 7/7 passing.

Build: `0 errors`, 19 analyzer warnings (pre-existing xUnit/analyzer style
warnings; no correctness warning category is suppressed).

## 2. Static and repository gates — PASS

| Gate | Result |
|---|---|
| Secret scan (`scripts/security/scan-secrets.py`) | PASS — 1340 tracked paths, no high-confidence secret |
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

Local release run: 2026-11-06, Compose project `ride-release-final`, source
`da85ca3` plus the release metadata/cleanup tree. The exact-tree CI run on the
release pull request is the authoritative run for the published commit; its
run id is recorded in the GitHub Release notes.

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
[evidence/release/v1.0.0/](../evidence/release/v1.0.0/) with the command,
environment, checks and source anchors.

## 4. Course minimum test mapping

The course requires at least 8 functional, 2 concurrency/conflict, 2
fault/recovery, 2 security/invalid-data tests and 1 performance experiment.
The repository exceeds the minimum with executable, real-infrastructure
tests. Representative proofs:

| Category | Representative tests |
|---|---|
| Functional (≥8) | `CreateRideHttpTests`, `CreateRideComponentTests`, `TripLifecycleComponentTests`, `TripDriverLifecycleHttpTests`, `TripDriverAgreementTests`, `TripDriverRejectTests`, `PaymentPersistenceTests`, `NotificationInboxTests`, `AuthNegativeTests` |
| Concurrency / conflict (≥2) | `DriverReserveConcurrencyTests`, `CreateRideConcurrencyTests`, `TripConcurrencyComponentTests`, `PaymentConcurrencyTests`, `AtomicDispatchFenceTests`, `MatchingAttemptDriverUniquenessTests`, `ReconciliationRaceTests` |
| Fault / recovery (≥2) | `TripW11FaultProcessTests`, `CrashAfterReserveRecoveryTests`, `LostReserveResponseHttpTests`, `OutboxRelayUnitTests`, `PaymentRecoveryTests`, `PaymentReconciliationTests`, `RedisOutageRecoveryTests`, `BacklogRecoveryTests`, `ProductionMatchingLifecycleRecoveryTests` |
| Security / invalid data (≥2) | `AuthNegativeTests` (7 dedicated identity negatives), `DriverAuthorizationExposureTests`, `DriverDbOwnershipTests`, `MatchingDbOwnershipTests`, `MessageContractTests`, `TripApiBoundaryTests`, `TripHttpContractTests` |
| Performance / scalability (≥1) | W13 E01–E04 campaigns and W14 E04 backlog recovery (see §6) |

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
| Multiple concurrent requests | Async intake, OCC reservation, concurrency and contention tests | `DriverReserveConcurrencyTests`, `CreateRideConcurrencyTests`, W13 E01/E02 |
| Registration, login and roles | Identity `/auth/register`, `/auth/login`, `/connect/token`; Rider/Driver/Service roles | `IdentityService` source; `AuthNegativeTests`; release smoke |
| Driver status and simulated location | Driver online/offline and location endpoints; Redis GEO projection | `DriverEndpoints`, `DriverLocationIngressTests`, release smoke |
| Passenger ride creation | `POST /trips` with durable idempotency key | `CreateRideEndpoint`, `CreateRideHttpTests`, release smoke |
| Driver search and selection | Matching candidate discovery, deterministic ranking | `MatchingPipelineTests`, `ProductionMatchingWorkflowTests`, release smoke |
| Driver acceptance or rejection | Authenticated accept/reject with exact `(driverId, reservationId)` fencing | `TripDriverDecisionTests`, `TripDriverRejectTests`, release smoke Scenario A |
| No simultaneous assignment to two trips | Driver PostgreSQL OCC reservation; stale release fencing | `DriverReserveConcurrencyTests`, `DriverRaceFencingTests`, W13 E01 |
| Trip lifecycle through completion/cancellation | Trip FSM with terminal monotonicity, cancellation cleanup and Saga compensation | `TripLifecycleComponentTests`, `TripCancellationMatrixTests`, release smoke |
| Fare calculation and simulated payment | Deterministic fare snapshot; durable payment operations with `operationKey` | `DeterministicFareCalculatorTests`, `FareSnapshotTests`, `PaymentPersistenceTests`, release smoke |
| Passenger and driver notifications | Outbox intents → Notification worker → per-recipient inbox; JWT owner isolation | `NotificationInboxTests`, `TripW18NotificationOutboxTests`, release smoke |
| Event identity, idempotency and deduplication | `messageId`, `correlationId`, `processed_messages`, payment `operationKey` | `MessageContractTests`, `W9DedupTests`, `PaymentReplayTests` |
| Bounded retry and DLQ | Finite delivery attempts, `<queue>.dead`, quarantine queue | `FailurePolicyTests`, `TopologySmoke`, `BrokerConsumeTests` |
| Correlation IDs | W3C trace context propagation; correlation ID logging/echo | `TraceHeaderPropagation`, `ReserveTraceContinuityTests` |
| Real Saga or compensation | Persisted `RideSaga` with Authorize/Capture/Void and compensation | `RideSagaTransitionTests`, `TripW10SagaTests`, release smoke A/C |
| Concurrency control and final-state consistency | OCC, guarded transitions, invariant auditor | Concurrency suites, `TripInvariantAuditor`, B12 audit |
| Failure detection, recovery and restart | Fault points, process crash/restart, Outbox recovery, reconciliation workers | `TripW11FaultProcessTests`, `CrashAfterReserveRecoveryTests`, W11/W14 evidence, release smoke C/D |
| Authentication and access control | RSA JWT validation, role checks, service permissions, owner isolation | `AuthNegativeTests`, `DriverAuthorizationExposureTests`, security gate 55 checks |
| Input validation and message/input-size limits | Fluent validation at boundaries; Kestrel body limits; bounded payload parsing | `TripApiBoundaryTests`, `DriverLocationIngressTests`, `DriverIngressLimits` |
| Secure secret configuration, no hardcoded credentials | `.env.example` placeholders only; release overlay fails closed; secret scanner | `scan-secrets.py` PASS; release overlay negative render PASS |

## 6. Performance and high-availability evidence

The measured campaigns are retained with their source identity, environment,
raw-sample hashes and acceptance analysis under
[evidence/team/w13-integration](../evidence/team/w13-integration/README.md) and
[evidence/team/w14-integration](../evidence/team/w14-integration/README.md).

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
- The attribution rewrite is recorded in
  [docs/08-credit-balance-correction-2026-11-06.md](../docs/08-credit-balance-correction-2026-11-06.md)
  and its machine-readable old→new map.
- No raw secrets, bearer tokens, database volumes or full engine streams are
  committed.

## 8. Known verification gaps

- Human team rehearsal, oral defense and peer sign-off are course-process
  activities performed by the team, not automated by this repository.
- Payment is a simulated provider; the provider boundary is real PostgreSQL
  and durable operations, not a card network.
- Notification delivery is in-app only.
- CI billing availability is external; the exact-tree GitHub Actions runs that
  did execute are linked in the W15/W16 evidence, and the local Release build,
  real-infrastructure suites, release smoke and clean-clone checks are the
  release-gate substitutes when a CI run is unavailable.
