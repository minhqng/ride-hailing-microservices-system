# Member B W10 Phase 1

Outcome: durable exact-identity Driver Start/Complete, safe shared command ingress,
real PostgreSQL/RabbitMQ proofs, independent review and a regular stacked PR.
Constraints: base d825920; preserve W7/W9 behavior and A/C production code; normal
push only; no merge. Non-goals: Payment-failure Saga integration and W11 campaign.
Acceptance: the request's deterministic proof matrix, affected full suites and
Release build pass; evidence and review have no unresolved B-owned blockers.

## Preflight

Both existing worktrees clean. C 7a0575a -> B 0b1611c -> 03c0438 ->
d825920 verified. W9 remote/local head 66d7d1c adds docs/evidence only.
W9 regular PR #37 targets MemC-W9-Distributed; duplicate #38 targets main and
is left untouched. W10 worktree D:/rh-w10b branched from exact d825920, then
fast-forwarded to the verified W9 docs/evidence-only head 66d7d1c so the
stacked PR preserves those files and has an ancestor base. No code history changed.
Native gh exists under the temporary CLI installation but is unauthenticated;
connected GitHub supplies live PR access. Push dry-run of W9: up to date.

## Execution

- [x] Inspect authorities, Driver/Trip/Matching contracts and shared runtime.
- [x] Implement append-only lifecycle effects and semantic receipts.
- [x] Implement guarded Start/Complete and exact Release command transaction.
- [x] Verify deterministic cases on disposable infrastructure and full suites.
- [x] Independent review, security and simplification; repair findings.
- [ ] Evidence/docs, focused commit, safe normal push, Ready-for-Review PR and CI.

Implementation b1435f79d1fd5b29696de68eeeaedcbd989e7a96; seven selected
unique suites: 913 PASS, 0 FAIL/SKIP, Release build PASS. Two P1 review findings
(disabled host and repeated DDL on reconnect) repaired and re-reviewed clean;
final real-host regression and full Driver suites passed. See
[evidence](../evidence/member-b/w10-lifecycle/README.md) for the proof matrix,
scope boundaries and Phase 2 handoff. Publication/CI gate is tracked on the PR.

## Design / risk

Driver row lock serializes reserve/start/complete/release. Lifecycle effects
retain exact driver/trip/reservation, phase, version, timestamp and message ID.
Separate STARTED/COMPLETED rows preserve historical replay without reactivating
old trips; historical identities cannot be newly reserved. No guessed provenance
for legacy ON_TRIP rows. Semantic receipts retain both successful and rejected
dispositions; a transaction-scoped message lock serializes reused message IDs
before taking a Driver lock. Receipt, effect and outgoing intent share one commit.
Consumer ACK follows commit; failures use C's bounded visible retry/error policy.

Scenario dimensions: input (missing/contradictory IDs); timing (duplicates and
Start/Release races); state (old trip vs newer work); environment (fresh process,
upgraded database); errors (outbox/receipt failure); integrity (identity reuse);
integration (Trip intake/Redis fences/relay retry); authorization (exact token).
Internal broker actors are service identities, not end-user roles. UI/compliance
and throughput experiments are outside this Phase 1 request.

Rollback: disable Driver command consumption; deploy prior application version.
Keep additive ledger/receipt tables and provenance. Never drop history to make
replay appear new. Migration is repeatable and leaves legacy rows intact.

## Research, 2026-10-25 Asia/Saigon

Executable RideTopology and TripMessageHandler own wire details. No A W10 producer
exists on this base. All command fixtures carry exact three IDs and canonical V1
envelopes; A must validate its eventual producer against this surface.
PostgreSQL row locks wait and return the updated row under READ COMMITTED:
https://www.postgresql.org/docs/18/explicit-locking.html . RabbitMQ confirms and
consumer ACKs are separate; uncertain handoff permits duplicates:
https://www.rabbitmq.com/docs/confirms and
https://www.rabbitmq.com/docs/reliability . Use durable receipts and stable
outbox IDs rather than infer exactly-once delivery.
