# REL-10 — Capture forward recovery after completion: final state

Durable assertion record (Doc 03 §25 `final-state/`). Real disposable
PostgreSQL probe per test (Testcontainers `postgres:18.6`); the record is
the exact assertion set the named test executes, not hand-written SQL
output. Test names resolve in `evidence/member-a/w10-saga/test-output.txt`
§3 and in
`tests/Integration/RideHailing.TripService.IntegrationTests/TripW10SagaTests.cs`.

## Claim

After the completion pivot, a capture failure of any shape never regresses
the COMPLETED trip, never re-mints the capture operation key, and keeps a
visible resolution state until an authoritative capture success resolves the
saga forward. Verified code commit: see `manifest.json` (`codeCommit`).

## Observed final states

`CaptureFailure_AfterCompletion_NeverRegressesTheRide_NeverReMintsTheKey`
— baseline: `trips.state = COMPLETED`, saga step `CAPTURE_PENDING`,
`capture_operation_key = {tripId:D}:capture:v1` (frozen;
`RideSagaOperationKeys.Capture`).

1. Transient capture trouble (`PaymentCapturePendingV1`,
   `resultCode=TRANSIENT_FAILURE`):
   - `ride_sagas`: status `ACTIVE`, step `CAPTURE_PENDING`,
     `capture_operation_key` unchanged (stable key).
   - `trips.state = COMPLETED` (untouched).
2. Explicit review (`PaymentRequiresReviewV1`, `RETRY_EXHAUSTED`):
   - `ride_sagas.status = REQUIRES_REVIEW`, step `CAPTURE_PENDING`
     (visible resolution state).
   - `trips.state = COMPLETED`.
3. Terminal capture DECLINED (delivered on the capture key):
   - classified `NeedsReview`; `trips.state = COMPLETED` — never regressed.
4. Through every failure shape: exactly **one** `CapturePaymentV1` outbox
   intent for the trip (no second logical capture intent is ever minted).
5. Forward recovery (`PaymentCapturedV1` under the exact same capture key):
   - `ride_sagas`: status `COMPLETED`, step `CAPTURED`.
   - `trips.state = COMPLETED`.

`StartRequiresAuthorization_CompleteDispatchesCaptureOnce_WithDriverCommands`
pins the capture pivot itself: capture is dispatched ONLY at completion
(DSI-027), and a late authoritative `DriverTripCompletedV1` confirmation is
a business replay — still exactly one capture intent.

## Provider scope

Payment outcome envelopes are authoritative-shaped facts delivered through
the real consumer transaction; the C-side Payment provider effect execution
is NOT claimed (3-service integration `INTEGRATION_PENDING`, see
`manifest.json` `crossOwnerPending`).
