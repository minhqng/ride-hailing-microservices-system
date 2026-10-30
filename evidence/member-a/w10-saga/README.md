# A-W10 — Persisted Ride Saga, Payment Orchestration, Compensation & Forward Recovery

Member A Week 10 local verification (Doc 05A §18, A-W10-01..12). Scope:
Trip-owned persisted Ride Saga (`ride_sagas`), the DriverAccepted →
AuthorizePayment → PaymentAuthorized → READY_TO_START flow, terminal
authorization-failure compensation with exact release identity (REL-09),
start/complete with durable Driver commands, and post-completion capture
forward recovery that never regresses a COMPLETED Trip (REL-10, DSI-028).

## Provenance model

Identical to W09: every result below was produced from exactly one code
revision; `reproduce.sh` delegates to the shared
`scripts/test/verify-source-anchor.sh`, which proves the checkout's source
(every tracked path outside `evidence/` — added, modified, deleted,
renamed and mode-changed paths are all drift) is identical to that revision
before running anything, and refuses otherwise.

- Branch: `feature/A-W10-trip-saga` (stacked on the updated
  `feature/A-W09-trip-outbox` head `f5fbe26`, PR #36, incorporated by a
  normal forward merge `d1c99b1` — no history rewritten, no force push; see
  `manifest.json`).
- Verified code commit: `0ba68b0` (tree `134cde3`) — the W10 implementation
  (`dbbe625`), the CI-trigger commit (`368dec8`), the merged W09 hygiene
  hardening (`0185c37`/`f5fbe26`), plus the W10-owned test cleanup fixing
  the 13 xUnit2029/xUnit2031 analyzer warnings in `TripW10SagaTests.cs`
  (predicate overloads; semantically identical assertions, none weakened).
- Final-state proof records for REL-09/REL-10 live in `final-state/`
  (assertion records against disposable real PostgreSQL probes,
  cross-referenced to the test names proven in `test-output.txt` §3/§4).

## Environment

| Item | Value |
|---|---|
| OS | Linux 7.3.0-1-default |
| .NET SDK | 10.0.401 |
| Configuration | Release |
| Docker | 29.7.2-ce |
| Test database | postgres:18.6 (Testcontainers, disposable probe DB per test) |
| Test broker | rabbitmq:4.3.6-management (Testcontainers, per-class fixture) |

## Results

| Lane | Command | Result |
|---|---|---|
| solution-build-release | `dotnet build RideHailing.Distributed.slnx -c Release --no-incremental` | PASS (exit 0, 0 errors, **1 pre-existing warning**: xUnit2029 in B-owned `DriverReserveConcurrencyTests.cs`, not touched here) |
| solution-tests-release | `dotnet test RideHailing.Distributed.slnx -c Release --no-build` | PASS **954 passed, 0 failed, 1 skipped** (pre-existing Identity opt-in skip; the SystemTests project has no discoverable tests — explicitly not counted as a skip) |
| w10-saga-integration | `dotnet test …TripService.IntegrationTests -c Release --no-build --filter FullyQualifiedName~TripW10Saga` | PASS 14/14 |
| w10-saga-unit | `dotnet test …TripService.UnitTests -c Release --no-build --filter "FullyQualifiedName~RideSagaTransitionTests|FullyQualifiedName~TripSagaIntentTests"` | PASS 27/27 |
| w09-outbox-regression | `dotnet test …TripService.IntegrationTests -c Release --no-build --filter FullyQualifiedName~TripW09` | PASS 17/17 |
| w08-messaging-regression | `dotnet test …TripService.IntegrationTests -c Release --no-build --filter FullyQualifiedName~TripW08MessagingTests` | PASS 18/18 |

Curated commands, exit codes, counts, per-project totals (including the
Identity skip and the SystemTests observation) and lane verdicts live in
**`test-output.txt`** (Doc 03 §25 bundle shape). Raw lane transcripts are
not tracked: re-running `reproduce.sh` regenerates them under
`outputs/repro-<stamp>/` (gitignored, local-only).

Corrections against the superseded bundle (commit `e469fda` lineage): it
reported `954/954 (0 failed, 0 skipped)` while its own log showed the
Identity skip, and `1 warning` while its own build log showed 14 (13 of
them xUnit2029/2031 in `TripW10SagaTests.cs`, W10-owned and fixed at this
commit).

## What each proof detects (A-W10-09..11)

- `TripW10SagaTests.SagaSchema_…` — migration drift: missing
  lineage/step/status CHECK constraints or the one-saga-per-trip unique
  index; W09-era databases upgrade without touching existing rows.
- `DriverAccepted_CommitsTripSagaAndAuthorizeAtomically_…` — A-W10-02:
  business state without its outgoing intent, or a duplicate acceptance
  minting a second logical authorize (DSI-034).
- `PaymentAuthorized_UnderExactLineage_…` — A-W10-03: a tripId alone, a
  foreign operationKey, or a duplicate success advancing/driving state.
- `DeclinedAuthorization_CompensatesWithExactRelease_…` — REL-09/A-W10-04:
  compensation missing the exact (tripId, driverId, reservationId,
  matchingRound) tuple, or an UNCERTAIN outcome compensating (DECLINED ≠
  UNKNOWN), or the reconcile obligation left dangling after conclusive
  no-effect evidence.
- `DeclinedAfterCancel_…`, `AuthorizedAfterCancel_…`,
  `CancelRace_ReviewSignalNeverOwnsCompensation_…`,
  `CancelRace_ReleaseConfirmedFirst_…` — the cancel-race window: stale
  success reviving CANCELLED, a review signal clobbering COMPENSATING, a
  release confirmation dead-lettering, or compensation terminating before
  BOTH release and void resolved (DSI-033/DSL-002).
- `StartRequiresAuthorization_…` — A-W10-05/06/07: start before
  authoritative authorization, capture before the completion pivot
  (DSI-027), or a late Driver completion fact creating a second capture.
- `CaptureFailure_AfterCompletion_…` — REL-10/A-W10-08/DSI-028: any capture
  failure shape regressing COMPLETED, re-minting the capture key, or hiding
  the review state.
- `RestartRecoversFromPersistedState_…` / every stage's fresh DbContext —
  Doc 02 §14.7: resume requires nothing in-memory; late facts classify
  stale without mutation.
- `ConflictingConcurrentOutcomes_…` — deterministic (TwoPartySaveBarrier)
  concurrent conflicting outcomes: exactly one commits, the OCC loser
  surfaces as a transient conflict and its receipt rolls back with the
  lost effect.
- `HappyRide_ALeg_…` — A-W10-11 (A-side): the full local chain to
  saga COMPLETED with exactly one of each logical command, all PENDING for
  the relay.
- `TripW10SagaRelayTests.AuthorizeIntent_…` — DSL-006: the shared relay
  publishes the authorize intent through the real broker onto
  `payment.commands.q` with the frozen operationKey/sagaId lineage intact.

## A-W10 task matrix

| Task | Status | Primary proof |
|---|---|---|
| A-W10-01 RideSaga persistence | DONE | schema/migration test; unit transition table |
| A-W10-02 DriverAccepted→Authorize | DONE | atomicity test; relay delivery test |
| A-W10-03 PaymentAuthorized→READY_TO_START | DONE | lineage test |
| A-W10-04 terminal-failure compensation | DONE | REL-09 test; cancel-race suite |
| A-W10-05 StartTrip | DONE | saga-phase guard test |
| A-W10-06 CompleteTrip | DONE | progress test |
| A-W10-07 CapturePayment emission | DONE | completion-pivot test |
| A-W10-08 capture pending/review | DONE | REL-10 test |
| A-W10-09 REL-09 proof | DONE | real PostgreSQL; assertion record in `final-state/rel-09-compensation.md`; broker release-intent payload exactness |
| A-W10-10 REL-10 proof | DONE | real PostgreSQL; assertion record in `final-state/rel-10-capture-recovery.md`; authoritative outcome envelopes |
| A-W10-11 happy ride | A-SIDE DONE / 3-service INTEGRATION_PENDING | HappyRide_ALeg + relay proof |
| A-W10-12 evidence + B/C review | evidence DONE; peer review pending | this bundle |

## Caveats / cross-owner boundaries

- **INTEGRATION_PENDING (not claimed):** the three-service happy ride
  requires C-W10's `payment.commands.q` consumer + Payment outcome producer
  and B-W10's `driver.commands.q` consumer; neither exists yet. No fake
  production consumers were created in Trip to make the end-to-end pass.
  Payment outcome events in these proofs are authoritative-shaped envelopes
  delivered through the real consumer transaction — the C-side provider
  effect execution is not claimed.
- `MarkPaymentAuthorizedHandler`/`MarkPaymentFailedHandler` remain trusted
  system intakes (caller attests the authority fact; the broker path is the
  lineage-verified one).
- Pre-W10 DRIVER_ACCEPTED trips keep the frozen W8
  `PAYMENT_LINEAGE_UNRESOLVED` parking (no saga is minted post-hoc from a
  duplicate acceptance).
- W11 owns deterministic fault-injection (crash hooks, repeated controlled
  passes); W10 proofs use real PostgreSQL/RabbitMQ and deterministic race
  barriers only.
