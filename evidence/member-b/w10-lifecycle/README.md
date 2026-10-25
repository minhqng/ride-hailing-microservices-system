# Member B W10 lifecycle and exact compensation evidence

## Phase 2 — 2026-10-25 Asia/Saigon

**B-owned compensation implementation and proof added. Full REL-09:
EXTERNAL GATE — NOT RUN.** Final counts, review and publication state are in
[manifest](manifest.md); owner actions are in [handoff](handoff.md).
Phase 1 evidence below is retained as a historical verification record.

### Protocol and production agreement

A PR #36 head `5f61cff4fc7a6dd1c178c7ffac3d6709369f7bf3` produces
`ReleaseDriverReservationV1` with exactly `driverId`, `tripId`, `reservationId`
and positive integer `matchingRound`. Message identity is
`trip:{tripId:N}:release:{reservationId:N}`, also its operation key.
The actual builder and DTO compile byte-for-byte into the B integration tests
from a pinned [test-only snapshot](../../../tests/Integration/RideHailing.DriverService.IntegrationTests/TripProducerSnapshot/README.md).
Their Git blob IDs match upstream (`13ea9f78…` and `ce4363c8…`). No A production
files or payloads were changed. A W10 PR [#40](https://github.com/minhqng/ride-hailing-microservices-system/pull/40)
head `e469fda1e36e6072d887b028f0726f7365aaeafd` retains that Release builder;
its added Start/Complete DTOs do not change Release. A's W10 Saga/intake was
source-reviewed, not executed as a combined persisted-Saga proof here.

Broker Release reuses the existing transaction:
message lock → semantic receipt → new-execution round validation → locked Driver → exact trip/reservation and
stored matching-generation guard → Driver AVAILABLE + canonical release Outbox
intent + receipt → commit → ACK. Round is compared before clearing reservation
fields. Wrong round and missing legacy provenance produce durable Rejected
receipts; no generation is fabricated. W7 HTTP Release behavior is preserved.
Exact message redelivery returns the original receipt and event ID even after
another reservation. A fresh message with no current exact reservation is not
historical proof; it requires reconciliation with the original event/receipt.
ON_TRIP, OFFLINE and AVAILABLE return `RELEASE_PROVENANCE_REQUIRED`, missing
Driver returns `NotFound:DRIVER_NOT_FOUND`, and mismatched IDs cannot release.
Committed Phase1 three-field messages still replay their identical stored receipt;
only new execution requires four fields. This preserves old semantics without
backfilling provenance or allowing new roundless effects.

**ACKNOWLEDGED:** Driver persisted a disposition. **APPLIED:** Driver committed
the release and required event. **CONFIRMED BY TRIP:** Trip consumed that exact
authoritative release and completed the owned cleanup. The integration proof
asserts Trip cleanup is still pending after the successful command ACK and before
event relay. Rejected/NotFound receipts never emit a success event.

### REL-09 scenario matrix

Tests below are in [DriverCompensationTests](../../../tests/Integration/RideHailing.DriverService.IntegrationTests/DriverCompensationTests.cs)
unless otherwise stated. Each uses real disposable PostgreSQL/RabbitMQ.

| Scenario | Status | Actual authority assertions / executable proof |
|---|---|---|
| Correct exact Release, canonical payment-compensation command shape | VERIFIED B LEG | ProductionRelease: Driver v3 RESERVED R → v4 AVAILABLE/unbound; one release intent with original trip/R and command causation/operation key; one Applied receipt |
| Duplicate Release | VERIFIED | Same command from fresh repository returns identical result/event; v4 and two total intents (reserve + release) stay unchanged |
| Phase1 receipt after upgrade | VERIFIED | PersistedThreeFieldReceipt: historical three-field receipt returns original Applied/event after newer R; unseen roundless command rejected, new R/v5 and three intents unchanged |
| Same messageId, altered round | VERIFIED | ProductionRelease: poison DRIVER_COMMAND_CONTENT_CONFLICT; no extra effect/intent |
| Stale Release after newer R | VERIFIED | LateReleaseAndReplay: original receipt stable; fresh-ID stale command Rejected; new trip/R/round2 survive at v5; three intents only |
| Release vs Start | VERIFIED | Phase1 ConcurrentStartAndRelease (updated valid round fixture): one Applied/one Rejected, v4, exactly one transition event; AVAILABLE or ON_TRIP |
| Release after ON_TRIP / AVAILABLE / OFFLINE | VERIFIED | UnprovenCompensation theory: durable Rejected receipt, null event identity; same version/state/active binding/intent count |
| Missing Driver / wrong trip or token | VERIFIED | Same theory: durable NotFound or Rejected, no success event; existing Driver unchanged |
| Wrong matchingRound / absent historical round | VERIFIED | Same theory: RESERVATION_ROUND_MISMATCH / RESERVATION_ROUND_REQUIRED; RESERVED R and version unchanged |
| Missing/null/noninteger/nonpositive/overflow round | VERIFIED | DriverCommandTests.ReleaseRequiresPositiveIntegerRound rejects before persistence; shared poison handling remains covered by Phase1 |
| Outbox insertion failure | VERIFIED | RequiredInsertFailure: real PostgreSQL trigger aborts; Driver remains RESERVED R/round1/v3, reserve intent only, no command receipt; retry applies after trigger removal |
| Receipt insertion failure | VERIFIED | Same proof and complete rollback assertions |
| Commit before ACK | VERIFIED | CommitBeforeAck: close real consumer after commit and before ACK; broker redelivers; original receipt/event, AVAILABLE v4, two intents only |
| Relay publish/mark loss | VERIFIED | ReleaseRelayAndTripIntake: confirmed publish then IOException; restarted relay publishes byte-identical envelope/messageId, row reaches PUBLISHED; one Trip receipt |
| Trip exact cleanup intake | VERIFIED B/BASE A CONTRACT | Same proof: wrong R produces Trip NeedsReview and leaves cleanup pending; command ACK still leaves pending; exact real Driver event completes only owned cleanup; Trip remains CANCELLED |
| Broker / database restart | VERIFIED | BrokerAndDatabaseRestart: durable queued command survives broker stop/start; PG stop/start before consume and after commit preserves Applied receipt, AVAILABLE v4 and pending release intent; relay then reaches PUBLISHED |
| Late DriverReserved | VERIFIED B/BASE A CONTRACT | Same real relay/intake proof: late actual Driver reservation creates new pending cleanup; earlier R stays complete, terminal Trip never reactivates; current Driver retains late R |
| Genuine terminal Payment failure → persisted A Saga → B → Saga confirmation, duplicates and restart | EXTERNAL GATE — NOT RUN | C Payment command consumer/outcome producer unavailable on all current remote branches. No PaymentFailed injection is counted as an end-to-end proof |

The Trip intake test runs the existing production Trip handler against a separate
real Trip database. It seeds a cancelled cleanup obligation through Trip domain
methods, not a terminal Payment outcome or Saga. It proves exact B/A intake
compatibility; it does not prove A W10 Saga completion or no duplicate money effect.
Redis projection and W7/W9/Phase1 regressions run in the selected full suites.

### Final issue checklist

| Item | Final boundary |
|---|---|
| B-W10-01 | VERIFIED: exact Start and atomic event (Phase1) |
| B-W10-02 | VERIFIED: exact Complete and original reservation history (Phase1) |
| B-W10-03 | VERIFIED: restart/replay and semantic receipts (Phase1) |
| B-W10-04 | VERIFIED B-owned exact compensation; full A/C integration EXTERNAL DEPENDENCY |
| B-W10-05 | VERIFIED: stale/newer reservation, active trip and race fencing |
| B-W10-06 | VERIFIED B Start/Trip intake; combined A Saga happy ride PARTIAL |
| B-W10-07 | VERIFIED B Complete/Trip intake; combined A Saga happy ride PARTIAL |
| B-W10-08 | VERIFIED B REL-09 support and executable matrix; full REL-09 NOT RUN |
| B-W10-09 | Independent code/contract review plus handoff; A/C human sign-off EXTERNAL DEPENDENCY |

## Retained Phase 1 evidence

Date: 2026-10-25, Asia/Saigon. Implementation:
`b1435f79d1fd5b29696de68eeeaedcbd989e7a96`.
Local gates: **PASS**, 913 unique selected tests, 0 failed, 0 skipped;
Release solution build PASS. Independent final review: no P0/P1 findings.
Publication and exact-head CI are recorded in the stacked PR and delivery report.

W9 dependency: [PR #37](https://github.com/minhqng/ride-hailing-microservices-system/pull/37),
head `66d7d1c1f05a87fe3156b43b94616ae3201006bb`, base C
`7a0575a51a89ba32bd3017afd779355ed2da9576`.
W10 branches from the specified W9 code closure `d825920`, then fast-forwards
through W9's docs-only closure so the published W9 head remains an ancestor.
Duplicate W9 PR #38 is left untouched. No force push or merge is authorized.

## Delivered architecture

Migration [005](../../../src/Services/Driver/RideHailing.DriverService/Infrastructure/Persistence/Migrations/005_driver_lifecycle.sql)
adds application-append-only lifecycle effects keyed by exact Driver, Trip,
reservation and phase. Original version, event ID and timestamp persist after
live RESERVED fields clear and after later trips. Completed identities cannot
be reserved again on the same Driver. Legacy ON_TRIP without provenance fails
safely; no identity is backfilled or guessed.

Each command acquires its message identity lock, checks a canonical semantic
fingerprint, locks the Driver row, validates the transition, persists state,
canonical Outbox intent, lifecycle effect and final command disposition, then
commits. Exact replay returns the original result; a changed semantic payload
under the same message ID is poison. The B-owned receipt also records rejected
commands for owner review. HTTP Release and broker Release share the existing
exact-token operation; the broker receipt and required Release intent share
its transaction. No remote call runs inside the transaction.

C's manual consumer ACKs after commit. Transient DB failures use bounded retry;
malformed/conflicting commands enter visible platform dead-letter handling.
The host consumes all three commands, defaults enabled, waits for required
schemas, and does not repeat successful DDL on broker reconnect. C's store and
relay publish stored event IDs and envelopes; correlation, causation, trace and
Saga/operation metadata are preserved. A/C production code is unchanged.

## Mandatory proof matrix

All listed tests use disposable real infrastructure. Names below are methods
in DriverLifecycleTests, DriverLifecycleBrokerTests or DriverLifecycleMigrationTests
unless the owning suite is explicitly stated. Theory cases expand the assertions.

| Requested case | Proof / final assertion |
|---|---|
| Correct Start | StartComplete_RestartAndHistoricalReplay_PreserveCanonicalIdentity: ON_TRIP, exact trip, cleared live refs, version 4 |
| Wrong reservation Start | WrongIdentity_DoesNotChangeStateVersionOrIntent: no state/version/intent change |
| Correct Complete | StartComplete_RestartAndHistoricalReplay_PreserveCanonicalIdentity: AVAILABLE, original reservation in event, version 5 |
| Wrong-trip Complete | WrongIdentity_DoesNotChangeStateVersionOrIntent: rejected without change |
| Duplicate Start / Complete | ConcurrentDuplicates_CommitOneEffectAndStableReceipt: one effect/intent; historical replay returns original event/version |
| Restart after Start / Complete | Fresh repository instances in StartComplete_RestartAndHistoricalReplay_PreserveCanonicalIdentity recover DB provenance and original replay |
| Stale Start after newer Reserve | OldUnstartedReservation_CannotHijackNewReservation plus historical replay against newer reservation |
| Stale Complete after newer Start | Historical replay leaves newer trip ON_TRIP at version 7 |
| Concurrent Start vs Release | ConcurrentStartAndRelease_HaveOneLegalWinner: exactly one Applied/one Rejected, coherent version 4 |
| Concurrent duplicate commands | ConcurrentDuplicates_CommitOneEffectAndStableReceipt: four barrier-released commands, same canonical receipt |
| Outbox insert failure | RequiredInsertFailure_RollsBackStateLedgerReceiptAndOutbox: START/COMPLETE both roll back |
| Receipt insert failure | Same theory: START/COMPLETE both roll back; retry after trigger removal applies |
| Commit before ACK | CommitBeforeAck_RealBrokerRedeliveryConverges: real broker connection disposed after commit before ACK, new consumer converges, both phases |
| Same message ID altered payload | SameMessageIdWithAlteredContent_IsPoisonWithoutMutation: actual Trip payload and command type variants |
| Relay publish/mark loss | LifecycleRelay_MarkLossRepublishesIdenticalEnvelope: identical stored message ID and body on restarted relay |
| Trip event intake | TripW08MessagingTests.DriverLifecycleProducerEvents_AreAcceptedAndReplaySafelyOverBroker: both real mapper payloads, duplicate deliveries, Trip COMPLETED and exact assignment |
| Matching projection | ProjectionDuplicateSafetyTests.DriverLifecycleProducerEvents_RespectProjectionVersionFence: ON_TRIP/AVAILABLE, delayed Start cannot regress version 5 |
| Migration fresh + upgrade | Fresh full Driver fixtures; UpgradePreservesLegacyRowsAndCoherence_AndIsRepeatable uses separate database, preserves legacy ON_TRIP, creates no guessed ledger, retains coherence CHECK |
| Runtime enablement/reconnect | DefaultConfiguration_StartsProductionHostAndConsumesCommand: real host consumes Start, reconnects while PG ACCESS SHARE blocks DDL, then commits Complete |
| Shared Release / poison | SharedQueue_HandlesReleaseAndLifecycle_AndQuarantinesPoison: Release reaches AVAILABLE; malformed command enters .dead |

## Scope and dependencies

| Item | Status / owner |
|---|---|
| B-W10-01 | DONE: exact Start guard and atomic event |
| B-W10-02 | DONE: Complete guard with durable original reservation |
| B-W10-03 | DONE: restart-safe lifecycle replay and semantic command receipts |
| B-W10-04 | PHASE 2: B/A Payment-failure compensation integration; exact Release ingress is supported here |
| B-W10-05 | DONE: stale/later-trip and concurrent fencing proofs |
| B-W10-06 | DONE for B independent fixtures and Trip event intake; real A producer is an external A gate |
| B-W10-07 | DONE for B independent fixtures and Trip event intake; real A producer is an external A gate |
| B-W10-08 | PHASE 2: B/A/C REL-09 integrated authorization-failure compensation |
| B-W10-09 | PENDING human A review; fresh independent agent review passed |

Full W10 completion is not claimed. See [manifest](manifest.md) for commands,
counts, hashes and retained failures; [handoff](handoff.md) for rollout and Phase 2.

## Independent review and security

Fresh-context read-only reviewer inspected the complete Phase 1 production,
migration, tests, CI and docs diff. Two P1 findings were fixed: disabled default
command hosting, then repeated locking DDL on reconnect introduced by the
readiness fix. A real host/PG-lock regression test covers both. Final re-review
found no P0/P1. Review did not independently rerun the controller's suites.

Read-only simplifier found one useful dead member removal (`DriverCommand.Envelope`),
which was applied. The explicit transaction flow and fresh-instance replay
proofs were retained; additional helper/enum abstractions were unnecessary.

Security review protects authoritative assignment state and command/result
integrity across a service-to-service broker boundary. New SQL uses parameters;
exact IDs and semantic receipts reject tampering; C bounds message size/retries
and emits sanitized dead-letter errors. No new user HTTP/auth surface or
dependencies were introduced. New-source/staged secret scans found no credential;
only .env.example is tracked. Driver transitive NuGet vulnerability audit found
no vulnerable packages in the current source. This is a scoped audit, not a
claim about all repository or deployment security. Existing platform broker
access controls remain the service-authentication boundary.
