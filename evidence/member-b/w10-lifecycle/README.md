# Member B W10 Phase 1 evidence

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
