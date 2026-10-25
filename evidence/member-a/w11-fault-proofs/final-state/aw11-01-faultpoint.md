# A-W11-01 — Saga.AfterPersistBeforeDispatch faultpoint

- **Run ID**: A-W11-01 / code 6047a7a / 11 gate+wiring tests + 5-process REL-04 proof
- **Tests**: `TripW11FaultPointTests` (11), `ProducerProcess_CrashedAtSagaDispatchFaultPoint_...`

## Implementation

`src/Services/Trip/RideHailing.TripService/Integration/FaultPoints/`:

- `TripFaultPointNames` — the canonical name
  `Saga.AfterPersistBeforeDispatch` (Doc 02 §27.2 spelling) and the
  message-type scope (the saga's three payment dispatches).
- `TripFaultPointGate` — settings (`TripFaultPoints:*`) + the gate. The gate
  rides the relay's pre-publish seam
  (`OutboxRelayOptions.BeforePublish`, wired in `TripMessagingRegistration`),
  which is necessarily AFTER the local commit: the relay can only observe
  rows a committed transaction produced. It never wraps business code, opens
  no transaction, and mutates no domain state (Doc 02 §27.3).

## Why the boundary is correct

The intended sequence (Doc 05A §19, mission §5) is:

```
Trip/Saga transition accepted
    → Trip + Saga + required Outbox committed   (one EF unit of work)
    → Saga.AfterPersistBeforeDispatch           (relay pre-publish seam)
    → dispatch/relay continues
```

Because W09 decoupled dispatch into the relay (the outbox pattern), the only
code that publishes is the relay worker, and it only sees committed rows.
Holding the relay immediately before `PublishAsync` therefore cannot fire
"before commit while being described as after-commit": an uncommitted intent
is unreachable at that point. The faultpoint adds no second transaction.

## Activation controls (asserted by tests)

- Default OFF: no `TripFaultPoints` configuration → `BeforePublish` is null
  (production relay path unchanged).
- Double gate: `TripFaultPoints:Enabled=true` + explicit `Activated` list +
  `SignalDirectory` AND a non-Production host environment; an explicit enable
  in Production throws at startup (fail-fast misconfiguration handling).
- No HTTP surface of any kind — the faultpoint cannot be activated by an
  unauthenticated or untrusted request.
- Misconfiguration fails loudly: missing signal directory, unknown activated
  name, or unbounded/absent wait timeout are startup faults (a typo can never
  silently disable a deterministic proof).

## Synchronization and safety (asserted by tests)

- Observable "reached": atomic marker file
  `faultpoint-Saga.AfterPersistBeforeDispatch.reached` containing
  faultPoint/processId/messageId/messageType/reachedAtUtc.
- Release: `faultpoint-Saga.AfterPersistBeforeDispatch.release` appears
  (harness-owned); the held dispatch then continues.
- Bounded wait: `WaitTimeoutSeconds` (default 30, validated 1..600). With no
  release signal the gate gives up at the deadline with a warning — the
  faultpoint never blocks forever.
- Scope: non-saga dispatches and unactivated gates return immediately with no
  marker (asserted for `ReleaseDriverReservationV1` and for
  disabled/unactivated configurations).

## C contract freeze

C's shared W11 platform (Doc 05C §30) is not delivered. The frozen contract
(name, location, scope, activation gates, file signaling protocol, bounded
wait) is documented on `TripFaultPointGate` and summarized in
`scripts/fault/README.md`. When C's `FaultPointProvider` lands, Trip migrates
behind it with unchanged semantics; no competing production faultpoint
framework is introduced.

## Observed

11/11 gate+wiring tests pass; the faultpoint deterministically holds the real
process's relay in the REL-04 proof (reached marker → pre-crash assertions →
SIGKILL → restart publishes the same identity), 5/5 campaign passes.

## Files

`src/Services/Trip/RideHailing.TripService/Integration/FaultPoints/*.cs`,
`Integration/Messaging/TripMessagingRegistration.cs`,
`tests/Integration/RideHailing.TripService.IntegrationTests/TripW11FaultPointTests.cs`
