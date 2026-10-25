# Member B W9 Outbox closure

Verdict: **B-W09 CODE COMPLETE — EXTERNAL GATES REMAIN**.

Implementation revision: `d825920d51aaae39ecff31a72540bf45cd800b4a` (both original B commits preserved).
This pack records local verification on 2026-10-25, Asia/Saigon. No push, merge,
rebase, reset, force-push, W10 implementation or W11 implementation was performed.
See [manifest](manifest.md) for commands/results/artifact hashes and
[handoff](handoff.md) for operator gates and remaining limitations.

## Duplicate publication safety

The transport is at least once. Driver state mutations and Matching terminal
results persist intent locally; a relay restart can publish the same message
again. Stable envelope.messageId is required, not an exactly-once claim.

| Required invariant | VERIFIED evidence |
|---|---|
| Exact duplicate produces one effect | Trip `W9PublicationReplay_OneEffect_ConflictingPayloadCannotReuseReceipt` covers DriverReservedV1, DriverReservationReleasedV1 and MatchingFailedV1; projection duplicate test preserves version 10/RESERVED |
| Same ID, conflicting semantics fails | Trip theory rejects changed payload with MESSAGE_ID_CONTENT_CONFLICT; projection rejects payload, Saga and operation conflicts; Matching intent tests reject aggregate/version/Saga/operation collisions |
| Commit before ACK redelivery converges | Trip `CommitBeforeAck_ConnectionClose_RedeliveryDoesNotReapply` uses real broker connection close after DB commit; shared W9 dedup tests exercise the durable receipt/effect transaction |
| Identity/version do not change twice | Driver reserve/release replay tests preserve versions and one intent; Trip theory preserves state, assignment identity, version, cleanup count and one receipt |
| Stale facts cannot regress terminal states | Trip stale-generation, terminal reservation/exact cleanup tests; Redis state/version and location/session ordering suites |
| Receipt/effect atomicity | Trip rollback-before-commit tests and shared marker/effect rollback tests use PostgreSQL; projection receipt check, guarded update and receipt SET share one serialized Redis FCALL |

Driver producer: `DriverRepository` -> `DriverOutboxWriter` -> shared
`NpgsqlOutboxStore` on the same transaction. Driver effect identity derives from
(driverId, reservationId, version, status); the relay reuses stored identity and
envelope fields. `DriverEventPublishTests` covers actual PG/RabbitMQ replay.

Matching producer: `MatchingTerminalizer` persists NO_DRIVER, deterministic
result_message_id and MatchingFailedV1 intent together. Its replay/content/race
and relay suites prove stable publication identity and no unresolved terminal.

Trip consumer: the existing A-owned `TripMessageHandler` performs a unique receipt
claim, canonical semantic comparison, domain guard and effect in one DB
transaction. Only Trip test code was changed. No A/C production edits were made.

Matching input identity: `EnsureRoundAsync` preserves the first input message ID
under the (trip, round) primary key and rejects an input ID reused for a different
round through its unique index. Existing envelope-identity tests cover this.
This round identity is not a full semantic receipt for unused RideRequested
fields such as dropoff; the handler currently only creates/loads a round.

Projection receipts are derived Redis state and have no TTL. They detect
same-message content conflicts while retained. Loss/rebuild of Redis loses those
receipts. Version/session guards still protect projection order, and Driver
remains authority. FCALL isolation is not rollback for arbitrary runtime errors;
known wrong-key types are checked before writes, and dependency failures retry.

## REL-02 executable W9 base

Test: [CrashAfterReserveRecoveryTests.cs](../../../tests/Integration/RideHailing.MatchingService.IntegrationTests/CrashAfterReserveRecoveryTests.cs).

The test runs the production Matching orchestrator and real Driver repository
against separate real PostgreSQL databases. It lets Matching persist IN_FLIGHT
and the dispatch fence, lets Driver commit Reserve(R) plus its intent, then
injects an `InterruptedAfterCommit` exception before Matching receives success.
It verifies persisted IN_FLIGHT, a real Driver reservation R and terminalizer
refusal; fresh repository/client/orchestrator instances recover the same durable
attempt via `ReconcileAsync`. The existing IN_FLIGHT reconciliation path performs
idempotent same-R Reserve replay. This is not a query-only recovery claim.

Assertions: same R, one durable attempt/ordinal, one DriverReservedV1 intent,
unchanged winner version after replay, second real Driver remains AVAILABLE at
its original version, recovered attempt and round are RESERVED with the same
identity, no MatchingFailed intent, no incorrect NO_DRIVER. A 30-second deadline
bounds execution; the fault boundary is synchronous and has no sleep-based oracle.

**NOT RUN:** OS process kill, host restart, HTTP transport, unattended recovery
worker or complete REL-02 fault campaign. The exception/recreated objects are an
executable recovery base, not a real process crash. External process/faultpoint
orchestration remains W11 work and was not started.

## Independent review

A separate read-only reviewer inspected both full original commit diffs,
surrounding production paths and tests, then reviewed the fixes independently.
The reviewer did not execute tests; runtime results in this pack were executed by
the controller in Docker.

| Finding | Ownership | Resolution |
|---|---|---|
| Legacy NO_DRIVER rows retained by 006 could not replay with NULL result ID | B | Fixed: under round/attempt locks, validate safety, persist deterministic ID and intent in one transaction; migration upgrade/replay proof passes |
| Matching intent conflicts ignored aggregate/Saga/operation metadata | B | Fixed: load and compare every consumer-semantic metadata field; four targeted rollback cases pass |
| Projection accepted reused ID with changed semantics | B | Fixed: canonical semantic fingerprint and receipt check in Redis FCALL, including Saga/operation and duplicate-property rejection |
| Historical pre-W9 Driver reserve replay does not repair missing intent | B/lead rollout decision | REPORTED limitation; current W9 writes are atomic, but old rows need coordinated backfill/rebuild. No speculative history was synthesized |

Reviewed lock order is round -> attempt: terminalizer FOR UPDATE, creation and
dispatch FOR SHARE, then attempt insert/update. Reconcile-only transactions
release attempt locks before round updates. Existing deterministic lock-wait
and W7/W8 fencing tests pass. This is scoped evidence, not proof against every
possible deadlock schedule. Migration 006 remains append-only and NOT VALID for
old rows; safe replay repair does not rewrite 001–006.

Final independent follow-up found no remaining production blocker in the changed
scope. A missing duplicate-property regression assertion was then added and the
final projection test passed.

## B-W09-01…10 checklist

| Task | Local status | Evidence / boundary |
|---|---|---|
| 01 Driver Outbox events | VERIFIED | Available/Offline/Reserved/Released for existing production mutations; no production OnTrip/TripCompleted path exists to wire in W9 |
| 02 Driver state + intent atomicity | VERIFIED | rollback/replay/real-broker Driver suite |
| 03 Matching NO_DRIVER + intent atomicity | VERIFIED | terminal rollback, guards, race tests |
| 04 Matching result identity | VERIFIED | deterministic persisted identity, exact replay and metadata conflict tests |
| 05 Shared relay integration | VERIFIED locally | Driver/Matching relay adapters and shared 60-test platform suite; C PR35 approval external |
| 06 Terminal durability | VERIFIED | Matching terminal/relay/migration integration tests |
| 07 Duplicate consumer safety | VERIFIED within documented boundaries | six-invariant matrix above; A/C production unchanged |
| 08 Resume after restart | VERIFIED component base | fresh repository/orchestrator reads persisted attempt; no unattended process-host recovery claim |
| 09 REL-02 automation base | VERIFIED | real PG/Driver authority, injected exception boundary; real process kill NOT RUN |
| 10 Evidence and independent review | VERIFIED locally | this pack, separate reviewer, local commits; human approval/CI external |
