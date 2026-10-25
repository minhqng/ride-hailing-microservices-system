# REL-17 — Late stale events after terminal Trip (A-W11-04)

- **Run ID**: REL-17 / code 6047a7a / five-pass campaign passes 1–5 (test-output.txt §4)
- **Scenario test**: `LateReservationAfterCancel_RealDelivery_ExactCleanupAndBusinessReplayDuplicate`
  (plus the terminal-monotonicity legs inside `SagaRestart_WhilePaymentOutcomeBacklogs_...`
  and `AuthorizedAfterCancel_VoidAndReleaseLegs_...`)

## Case selection rationale

Existing W07/W08/W09/W10 negative tests already prove, at the handler/
in-process level: stale rounds, terminal rejection, late reservation cleanup,
stale payment outcomes, out-of-order results and receipt replay. W11 adds the
missing REAL-DELIVERY + RESTART dimension and does not duplicate the
combinatorial suite:

| Case | Covered here by |
| --- | --- |
| CANCELLED + late `DriverReservedV1` | `LateReservationAfterCancel...` (real broker delivery into the real process) |
| CANCELLED + late `PaymentAuthorizedV1` | `AuthorizedAfterCancel_VoidAndReleaseLegs...` (exact void path) |
| COMPLETED + late conflicting lifecycle/payment result | terminal legs of `SagaRestart_...` (stale DECLINED + duplicate capture on COMPLETED/CAPTURED) |
| duplicate delivery → no second business mutation | logical duplicate in `LateReservationAfterCancel...` (BusinessReplay) |

## Claim A — late reservation on CANCELLED

A late `DriverReservedV1` for a NEW exact reservation identity, delivered
through the real broker to the real process, must not reactivate the trip and
must create only the exact cleanup obligation for that identity.

## Observed (asserted)

- Trip stays `CANCELLED`; Version advances exactly once (the cleanup record).
- New outbox row `trip:{tripId:N}:release:{lateReservationId:N}` /
  `ReleaseDriverReservationV1` whose payload carries the exact `driverId` and
  the late `reservationId` (read back from the durable row, not from the
  input envelope).
- Exactly one new intent is added; the receipt disposition is `Applied`.

## Claim B — logically duplicate delivery

A second delivery of the same authority fact under a FRESH `messageId`
(distinct deliveries discovering the same obligation) must be a durable
BusinessReplay.

## Observed (asserted)

- Receipt for the duplicate fact: `BusinessReplay`.
- Trip Version unchanged from the post-late value; outbox intent count
  unchanged; total receipts for the trip = 2 (one per delivery).

## Claim C — terminal monotonicity after COMPLETED (in the REL-07 scenario)

- Stale `PaymentAuthorizationFailedV1` on the terminal saga:
  `NeedsReview`/`STALE_PAYMENT_OUTCOME`; Trip stays `COMPLETED`, Version and
  saga StepVersion unchanged.
- Duplicate `PaymentCapturedV1`: `BusinessReplay`; no regression.

## Expected vs observed

Expected: no state regression, exact cleanup identities, no second mutation
under duplicates, poison/stale/transient classifications preserved. Observed:
identical (PASS, 5/5 campaign passes).

## Limitations

- Driver-side authoritative state (what B's database believes) is not
  inspected; the exactness claim is Trip-side (the identities the release
  command carries).

## Files

`tests/Integration/RideHailing.TripService.IntegrationTests/TripW11FaultProcessTests.cs`
