# REL-07 — Saga restart while Payment pending (A-W11-03)

- **Run ID**: REL-07 / code 6047a7a / five-pass campaign passes 1–5 (test-output.txt §4)
- **Scenario test**: `TripW11FaultProcessTests.SagaRestart_WhilePaymentOutcomeBacklogs_SameSagaContinuesToCompletion`

## Claim

With the saga durably at `AUTHORIZE_PENDING` and the authorize command
dispatched, the Trip process is killed. The Payment success outcome arrives
while the service is DOWN (durably backlogged). A restarted process continues
the SAME saga — unchanged identity, unchanged operationKey, legal step
advance — through the full remaining lifecycle to terminal COMPLETED, and
late stale/duplicate outcomes cannot regress it.

## Setup

Same seeded committed state as REL-04 (DRIVER_ACCEPTED + AUTHORIZE_PENDING +
pending authorize intent). Process #1 starts WITHOUT the faultpoint; its
relay publishes the authorize command (DB state barrier: outbox row
`PUBLISHED`); `StepVersion` captured before the kill.

## Fault

SIGKILL of process #1 at the deterministic point "dispatch durable, outcome
still pending" (both facts read from the database, not inferred from logs).
The harness then waits for the broker to unregister the dead connection's
consumers (bounded poll on `trip.payment-outcome.q` consumer count = 0) and
publishes `PaymentAuthorizedV1` with the saga's exact `sagaId` +
`operationKey` lineage onto the real broker.

## Backlog evidence (asserted while the service is down)

- `trip.payment-outcome.q` consumer count = 0
- `trip.payment-outcome.q` ready count = 1 (quorum queue holds the outcome)

## Restart and continuation (all asserted)

1. Restarted process consumes the backlogged outcome:
   - Trip `DRIVER_ACCEPTED` → `READY_TO_START`
   - saga `ACTIVE`/`AUTHORIZED`, same `SagaId`, same authorize key,
     `StepVersion = pre-kill + 1` (exactly one legal advance)
   - receipt disposition `Applied` for the outcome `messageId`
   - `payment.commands.q` contains exactly ONE authorize message (the
     pre-kill publication) — no second dispatch after restart.
2. `DriverTripStartedV1` (real broker delivery): Trip → `IN_PROGRESS`.
3. `DriverTripCompletedV1`: Trip → `COMPLETED`, saga → `CAPTURE_PENDING`,
   `CapturePaymentV1` intent published with the frozen capture key
   `{tripId}:capture:v1` and the same `sagaId` lineage.
4. `PaymentCapturedV1`: saga → terminal `COMPLETED`/`CAPTURED`.
5. REL-17 monotonicity on the terminal state:
   - stale `PaymentAuthorizationFailedV1` (authorize key, saga now terminal):
     receipt `NeedsReview`/`STALE_PAYMENT_OUTCOME`; Trip stays `COMPLETED`
     with unchanged Version; saga unchanged.
   - logically duplicate `PaymentCapturedV1` (fresh messageId, same key):
     receipt `BusinessReplay`; no regression, no second effect.

## Expected vs observed

Expected: same saga/operation identity, one legal step advance, full forward
continuation, terminal monotonicity under late events. Observed: identical
(PASS, 5/5 campaign passes).

## Limitations

- C-W10's real Payment outcome producer is pending: the outcome is published
  by the harness acting as the Payment authority with the saga's exact
  lineage. This is an A-side real-consumer restart proof, NOT a three-service
  Payment execution claim; no provider effect is fabricated.
- Driver authority facts are harness-published (B-W10 PR #39 OPEN); Driver
  authoritative state effects are not claimed.

## Files

`tests/Integration/RideHailing.TripService.IntegrationTests/TripW11FaultProcessTests.cs`
(`SagaRestart_WhilePaymentOutcomeBacklogs_SameSagaContinuesToCompletion`).
