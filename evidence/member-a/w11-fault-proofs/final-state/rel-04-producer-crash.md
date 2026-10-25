# REL-04 — Producer process crash before publish (A-W11-02)

- **Run ID**: REL-04 / code 6047a7a / five-pass campaign passes 1–5 (test-output.txt §4)
- **Scenario test**: `TripW11FaultProcessTests.ProducerProcess_CrashedAtSagaDispatchFaultPoint_RestartPublishesSameIdentity`

## Claim

The real Trip producer process is abruptly terminated (SIGKILL) after the
authoritative local commit and before the corresponding `AuthorizePaymentV1`
message is published. A restarted process using the SAME durable database and
broker recovers the pending intent and publishes the identical logical
message. No business transition is re-executed.

## Setup

- Disposable PostgreSQL 18.6 probe database (fresh per run, migrated).
- Real RabbitMQ 4.3.6 container; `payment.commands.q` purged before the run.
- Committed producer state seeded through the REAL handler chain (one
  transaction): Trip `DRIVER_ACCEPTED`, saga `AUTHORIZE_PENDING` with the
  frozen authorize key `{tripId}:authorize:v1`, outbox row
  `trip:{tripId:N}:authorize:v1` PENDING.
- Trip service process #1 launched from the compiled Release apphost with the
  `Saga.AfterPersistBeforeDispatch` faultpoint activated
  (signal directory per run; bounded wait 120 s; relay claim lease 2 s).

## Fault

The relay claims the committed authorize intent and reaches the deterministic
faultpoint (observable `faultpoint-Saga.AfterPersistBeforeDispatch.reached`
marker, content: faultPoint/processId/messageId/messageType/reachedAtUtc).
The harness observes the marker, asserts the pre-crash state, then SIGKILLs
exactly the process it launched (`Process.Kill(entireProcessTree)`, exit
verified; graceful cancellation is never used).

## Crash point — authoritative pre-crash assertions (inside the test)

| Oracle | Observed (asserted) |
| --- | --- |
| Trip business state committed | `DRIVER_ACCEPTED` |
| Saga | `ACTIVE` / `AUTHORIZE_PENDING`, authorize key = frozen lineage |
| Outbox row | `PENDING`, claimed by the dying relay, `published_at` NULL |
| Original messageId | `trip:{tripId:N}:authorize:v1` persisted |
| No successful publication | `payment.commands.q` ready count = 0 |

## Recovery

A NEW Trip process starts against the same database and broker (faultpoints
inactive). Its relay reclaims the orphaned claim after the configured lease
and publishes through the real broker.

## Post-restart final state (asserted)

- Outbox row `PUBLISHED`, same `message_id`, `attempt_count = 0` (the crash
  burned no publication attempt), exactly the two transition-staged intents
  exist for the trip (RideRequested + AuthorizePayment) — the restart added
  nothing.
- `payment.commands.q` holds exactly one `AuthorizePaymentV1` with the same
  `messageId`, `sagaId` and `operationKey` lineage.
- Trip still `DRIVER_ACCEPTED`; saga still `AUTHORIZE_PENDING` with unchanged
  `StepVersion` — no second logical transition.

## Expected vs observed

Expected: durable pending intent survives the crash; the restarted relay
publishes the same identity; business state never re-executes. Observed:
identical (PASS, 5/5 campaign passes).

## Limitations

- The window is held at the relay pre-publish seam; the claim lease for the
  restarted process is configuration (2 s) so reclaim is prompt — a timing
  assumption is not load-bearing for the assertions.
- Downstream Payment consumption is C-W10-pending; transport delivery to
  `payment.commands.q` is the proof boundary, NOT downstream completion, and
  no exactly-once-delivery claim is made.

## Files

`tests/Integration/RideHailing.TripService.IntegrationTests/TripW11FaultProcessTests.cs`
(`ProducerProcess_CrashedAtSagaDispatchFaultPoint_RestartPublishesSameIdentity`),
`TripW11ProcessHarness.cs`.
