# W10 Phase 1 handoff

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
