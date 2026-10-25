# W10 lifecycle and compensation handoff

## Phase 2 final handoff — 2026-10-25 Asia/Saigon

B-owned exact compensation is implemented and independently reviewed. The
implementation commit is `87b639f5c471861129f05811968e92a3090a5ce7`. The
existing [PR #39](https://github.com/minhqng/ride-hailing-microservices-system/pull/39)
remains stacked on W9 #37; no merge/auto-merge or history rewrite is authorized.
See [README](README.md) for the final B-W10-01..09 checklist and scenario matrix,
and [manifest](manifest.md) for final commands/counts and retained failures.

### Owner gates

| Owner | Existing surface / required evidence |
|---|---|
| A / `minhqng` | PR #40 at e469fda1e36e6072d887b028f0726f7365aaeafd already implements persisted Saga, payment compensation and lifecycle commands. Review B's four-field Release compatibility, exact event cleanup, roundless legacy receipt replay and visible rejection policy; provide human Driver–Trip agreement sign-off (B-W10-09) |
| C / `Nerduckk` | Implement/wire real `payment.commands.q` handling, authoritative terminal authorization outcomes and stable Saga/operation-key retry semantics. Existing Payment only exposes the durable simulator/HTTP execution and topology bootstrap; no broker command consumer/outcome producer exists on inspected remote branches |
| A/B/C | Run real Payment terminal failure → persisted Saga COMPENSATING + Release Outbox → B exact release → Driver event → A cleanup/Saga confirmation. Assert durable Payment no-effect/decline, exact R/round, Driver AVAILABLE/unbound, Trip terminal and correct Saga compensation. Repeat duplicate/restart windows with no duplicate money/business effect |
| Lead | Integrate C #35, B W9 #37, A W9 #36, A W10 #40 and B W10 #39 in coordinated order, replace pinned test-only A snapshots with integrated production types, rerun combined tests/CI, review rollout of historical provenance |

**Full REL-09: EXTERNAL GATE — NOT RUN.** A's Saga exists on its parallel
branch; it is not absent or implemented on B's base. The B/base-A Trip cleanup
test uses a cancelled domain obligation, not Payment failure or a persisted Saga.
Neither that proof nor A's injected authority-shaped outcome tests prove the
combined C Payment execution path. Human A/C approval remains pending.

### Compensation reconciliation

1. Query `driver_command_receipts` by A's stable Release message ID. Its
   disposition/reason is durable evidence of handling, not Saga completion.
2. `Applied` identifies an atomic release effect and `event_message_id`; locate
   that Outbox row and its pending/published/visible-failure state. Replayed
   historical `Applied` can coexist with a newer current reservation.
3. Only the authoritative exact release event may confirm the matching Trip
   cleanup. A successful command ACK can precede event publication; it does
   not complete the obligation. `Rejected`/`NotFound` emit no success event.
4. `RESERVATION_ROUND_MISMATCH`, `RESERVATION_ROUND_REQUIRED`,
   `RELEASE_PROVENANCE_REQUIRED`, stale-token and missing-Driver dispositions
   require A/B reconciliation and visible unresolved/review state. Do not
   repeatedly change message identity hoping for a different result; do not
   release a newer R or infer a historical matching round.
5. Newly seen missing/invalid round is poison through C's existing visible
   error handling. An identical pre-upgrade three-field receipt still replays;
   no new execution bypasses the four-field guard. Do not delete receipts.

The new execution guard is broker-specific; W7 HTTP exact Release remains
unchanged. There is no schema migration or historical backfill in Phase 2.
Rollback uses the existing CommandsEnabled flag and preserves receipts/Outbox.
No automatic A-side reconciliation worker or C retry changes are included.
Fixture restarts stop/start only Testcontainers-owned servers, refresh mapped
ports and recreate the database pool; production/user containers are untouched.

## Retained Phase 1 handoff

Implementation: `b1435f79d1fd5b29696de68eeeaedcbd989e7a96`.
Local mandatory tests/build and independent P0/P1 review passed. Delivery uses
a regular Ready-for-Review PR with base `feature/B-W09-driver-outbox` while
[W9 #37](https://github.com/minhqng/ride-hailing-microservices-system/pull/37)
is unmerged. C dependency [#35](https://github.com/minhqng/ride-hailing-microservices-system/pull/35)
remains open at `7a0575a`. No auto-merge or force push.

## Review and rollout

- Lead/A (`minhqng`): exact Driver–Trip assignment agreement, both event types,
  payment-authorization prerequisite in A's producer, stale command fencing.
- C (`Nerduckk`): shared ACK/retry/dead-letter/Outbox integration and stable
  envelopes after uncertain publication. No C production edits were made.
- B/PR author: monitor command receipts with Rejected/NotFound dispositions,
  pending/FAILED_VISIBLE outgoing intents and driver.commands.q.dead.
- Apply migration 005 and shared C Outbox schema before accepting commands.
  Standard Driver startup does this; the worker retries failed initialization.
  Each successful schema bootstrap is retained across broker reconnects.
- CommandsEnabled defaults true; container environment
  `RabbitMq__CommandsEnabled=false` suspends ingress. Preserve the queue and
  ledger during rollback. PublishingEnabled separately controls outgoing relay.
- Historical ON_TRIP rows without ledger provenance require B/lead investigation
  using authoritative evidence; do not synthesize reservation IDs or delete
  receipts to make commands appear new. No historical backfill is supplied.
- Preserve lifecycle/receipt history for the accepted replay horizon. Retention
  and pruning require an explicit operational design, not arbitrary deletion.

Native gh is installed but unauthenticated. Read-only gh preflight failed;
connected GitHub tools provide PR/reviewer/CI operations. Normal Git push uses
the existing credential helper. No credentials were read or copied.

## External gates and exact remaining Phase 2 work

1. **A:** implement/verify actual persisted W10 Start/Complete command producers
   using the three exact payload IDs and canonical envelope. No such producer
   exists on the verified base; current proofs use canonical real-broker fixtures.
   Trip intake accepts both B mapper event payloads; this is not a complete
   live A→B→A Saga proof. Human B-W10-09 review remains requested.
2. **A/C:** wire terminal authorization failure with stable Saga/operation
   lineage, persisted COMPENSATING state and exact Release intent in A's Outbox.
3. **B/A:** integrate that real Release command with the existing exact-token
   handler, observe authoritative DriverReservationReleased, and let A confirm
   the matching cleanup/compensation disposition. Duplicate, late and stale
   Release must never free a newer reservation or an ON_TRIP driver.
4. **A/B/C:** run REL-09 with real payment terminal failure, duplicate/redelivered
   outcomes and interrupted/restarted dispatch. Assert failed/cancelled Trip,
   completed compensation, available unbound Driver, no leaked reservation,
   no duplicate monetary/business effect, no early ACK or missing intent.
5. **Lead:** integrate C/W9 and coordinate A's parallel W9 branch before the
   combined W10 rollout. Duplicate W9 PR #38 is a separate lead housekeeping
   decision; it was not closed or retargeted in this task.
6. **W11 owner:** full external process-kill/fault campaign remains outside this
   change. Phase 1 has real connection-close redelivery and recreated repository
   proofs, not a W11 process-kill claim.

See [README](README.md) for issue coverage and proof matrix,
[manifest](manifest.md) for command/count/hash evidence.
