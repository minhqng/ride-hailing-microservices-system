# Member A W7–W8 review and hardening

Date: 2026-10-16 (Asia/Saigon). Review baseline: PR #26 at bf4f610.

## Sources and scope

Applied the precedence 00–03 > 04 > 05A. Read the architecture/authority baseline,
Doc 01 cancellation, exact cleanup and DSL-002; Doc 02 envelope identity,
atomic dedup, stale events, failure windows and readiness; Doc 03 CON-04,
CON-05 and commit/ACK verification; Doc 04 W7/W8 scope; all A-W07-01..09
and A-W08-01..10 requirements. Compared actual B-W8 Driver mapper/publisher
and C-W8 consumer/error behavior with the A adapters.

## Findings addressed

| Priority | Finding | Change and regression |
|---|---|---|
| P1 | Consumer registration could precede receipt migrations; configuration errors could consume useful input into metadata-only errors | Worker waits for owned schema; unmigrated-database broker test |
| P1 | Unknown Trip exhausted retries into C error metadata, losing the original payload needed for recovery | Durable PendingWorkflow/TRIP_NOT_FOUND with full envelope before ACK; duplicate broker delivery test |
| P1 | Driver status, aggregate identity and version could contradict the event being applied | Validate supplied authority metadata before receipt/effect; three negative cases plus actual lowercase B aggregate type |
| P2 | Full-envelope fingerprint rejected equivalent B republish with changed correlation/time | Semantic fingerprint, legacy receipt compatibility; preserves messageId and domain guards |
| P2 | Broker outage made an already terminal CreateRide replay return publication-pending | Load/authorize and check lifecycle before requiring the publisher; terminal replay and ownership regression |
| P2 | MATCH_FAILED late reservation did not create an exact actionable obligation | Terminal cleanup path covers cancelled/failed pre-start rides; real broker reserve/release and unit state table |
| P2 | Concurrent distinct messages discovering one cleanup could surface a unique-key conflict as permanent configuration failure | Only the cleanup PK conflict retries with a fresh scope; barrier-controlled two-writer regression |

The previous W6 terminal regression now expects a successful **cleanup intake**,
not assignment success: it asserts unchanged MATCH_FAILED, no assignment,
exact driver/reservation identities, a pending intent and one version increment.
The lower-level assignment FSM still rejects terminal assignment.

## Requirement assessment

| Requirement | Evidence / boundary |
|---|---|
| W7 pre-start policy | Frozen matching/reserved/accepted cancellation; CREATED/READY_TO_START remain policy-unresolved |
| W7 exact cleanup and late events | Same-transaction ledger, exact release identity, no terminal reactivation |
| W7 CON-04/05 and monotonicity | Both controlled schedules, old release versus newer reservation, state table and real DB assertions |
| W7 cleanup liveness | Durable pending obligations and controlled authority completion; automatic production dispatcher remains later workflow work |
| W8 canonical publish/consume | C publisher/manual ACK, existing RideRequested payload, supervised three queues |
| W8 dedup plus domain guards | Unique claim + business change transaction; replay, conflict, stale and concurrency regressions |
| W8 tracing / real Rabbit | HTTP-to-broker Matching-style sink, correlation and W3C trace assertions |
| W8 crash windows | Rollback and commit-before-ACK connection-close redelivery; broker reconnect; DB/publish gap explicitly retained for W9 |
| Peer review / full integration | Human B/C sign-off and full production dispatch are not substituted by these tests |

## Remaining integration gates

- B's DriverReservedV1 still needs authoritative matchingRound. A retains it for
  review when absent; no inferred round or claim of full B compatibility.
- PendingWorkflow needs an authorized reconciliation workflow. Unknown-Trip
  evidence is durable and visible; this change does not add autonomous reprocessing.
- Payment outcomes cannot authorize a ride until Saga operation lineage is
  available. Pending payment evidence is not a money-effect claim.
- W9 Outbox must close direct publish's commit/publish gap. Automatic cleanup,
  full Saga and authenticated end-to-end dispatch remain later work.

## Final execution evidence

Runtime/test source: `9f892db864e34d128dac12328af9a566c1b2250c`.

- Release compilation succeeded; unit build reported zero warnings/errors.
- Full Trip unit suite: **268/268 passed**, zero skipped.
- Full Trip integration suite: **126/126 passed**, zero skipped; real PostgreSQL
  18.6 and RabbitMQ 4.3.6, duration 2m52s.
- Git whitespace diff check passed.
- First intermediate run: 122/123 passed; the old W6 handler expectation was
  updated with stronger exact-cleanup assertions as described above. The final
  run includes additional semantic-republish, two-writer and schema-gating cases.

Commands:

```bash
dotnet test tests/Unit/RideHailing.TripService.UnitTests -c Release --logger "trx;LogFileName=w7-w8-hardening-unit-final.trx"
dotnet test tests/Integration/RideHailing.TripService.IntegrationTests -c Release --logger "trx;LogFileName=w7-w8-hardening-integration-final.trx"
```

On this host, the final integration run used `--no-restore`; the final unit run
used `--no-build --no-restore` after a successful Release build. Docker was
provided by the same temporary WSL loopback relay used by the initial proof.

Ignored local TRX SHA256:

- unit: `8398D8C97C15E8763F1AB5AE3B0497CC75A2229E655AED03402DA5ECC25B55AF`
- integration: `DDD2C413CDF97D7ACE6AADA651F44EEED01B0B3BC3C1CC5A0CC5E044DFF1F8FA`

This is an implementation review and local verification, not independent human
approval, a full-repository CI result or a full production dispatch claim.
