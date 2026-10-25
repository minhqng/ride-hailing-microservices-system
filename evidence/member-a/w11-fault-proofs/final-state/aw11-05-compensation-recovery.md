# A-W11-05 — Cancellation compensation outage/crash recovery

- **Run ID**: A-W11-05 (void race + outage) / code 6047a7a / five-pass campaign passes 1–5 (test-output.txt §4)
- **Scenario tests**:
  - `AuthorizedAfterCancel_VoidAndReleaseLegs_ExactIdentityToCompensated`
  - `CompensationOutstanding_ProcessCrashed_BackloggedConfirmationResolvesAfterRestart`

## Chosen failure windows (from the current W10 compensation implementation)

1. **Authorization outcome uncertain at cancellation → success arrives after
   cancellation**: the hold exists at Payment authority, so an exact void is
   mandatory; the void and release legs then resolve in different orders.
2. **Service crash while compensation obligations are outstanding**: the
   release obligation is unresolved when the process dies; the confirmation
   arrives during the downtime and must resolve after restart.

## Scenario 1 — authorized-after-cancel exact void (observed, asserted)

Setup: committed cancelled-during-authorize state (Trip `CANCELLED`, saga
`COMPENSATING`/`AUTHORIZE_PENDING`, release + cancel-round + reconcile
obligations) through the real handler chain; real process running.

- Late `PaymentAuthorizedV1` (exact lineage) through the real broker:
  - Trip STAYS `CANCELLED` (never revived — DSI-010).
  - saga → `VOID_PENDING` with `VoidOperationKey = {tripId}:void:v1` (frozen
    lineage; read back from the durable row).
  - `VoidPaymentAuthorizationV1` dispatched through the real broker with the
    same `operationKey` + `sagaId` on the wire.
- `PaymentAuthorizationVoidedV1` (exact void key): void leg resolves; saga →
  `RELEASE_PENDING` (release still outstanding — COMPENSATED is NOT claimed).
- `DriverReservationReleasedV1` (exact driver/reservation identity): release
  leg resolves; saga → terminal `COMPENSATED` — only now, with BOTH legs
  resolved. Receipts for both confirmations: `Applied`.

## Scenario 2 — crash with obligations outstanding (observed, asserted)

Setup: seeded DRIVER_ACCEPTED/saga; process #1 running.

- Relay publishes the authorize command (DB barrier), then
  `PaymentAuthorizationFailedV1` with `resultCode=DECLINED` (terminal
  no-effect, REL-09 semantics) is delivered through the real broker:
  Trip → `CANCELLED`, saga → `COMPENSATING`/`RELEASE_PENDING`, exact release
  obligation staged (`trip:{tripId:N}:release:{reservationId:N}`).
- SIGKILL of process #1 while the release obligation is unresolved (verified
  process exit; no graceful stop).
- While the service is DOWN: `DriverReservationReleasedV1` backlogs on
  `trip.driver-state.q` (consumer count 0, ready count 1 asserted).
- Restart: the restarted process consumes the backlogged confirmation and the
  saga reaches `COMPENSATED` — same `SagaId`, same authorize key, exact
  reservation identity in the release row; the release intent is dispatched
  (`PUBLISHED`) by the restarted relay; NO `VoidPaymentAuthorizationV1` row
  ever exists on the DECLINED path (uncertain/declined classification
  preserved: DECLINED is no-effect, so no void obligation may appear).
- Trip stays `CANCELLED` throughout (terminal monotonicity).

## Expected vs observed

Expected: terminal legality, exact release/void identities, `COMPENSATED`
only after every mandatory leg resolves, no obligation silently disappears
across the crash, DECLINED ≠ UNKNOWN ≠ void-required. Observed: identical
(PASS, 5/5 campaign passes).

## Limitations

- B's authoritative Driver state is not queried (B-W10 PR #39 OPEN); the
  release/void exactness is proven on the Trip side (command identities and
  saga transitions).
- Payment void confirmation is harness-published with the exact void key
  (C-W10 producer pending); no provider effect is fabricated.

## Files

`tests/Integration/RideHailing.TripService.IntegrationTests/TripW11FaultProcessTests.cs`
