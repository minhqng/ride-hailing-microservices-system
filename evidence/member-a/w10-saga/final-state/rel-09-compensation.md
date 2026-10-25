# REL-09 — Terminal authorization failure: exact compensation final state

Durable assertion record (Doc 03 §25 `final-state/`). The database is a
disposable real PostgreSQL probe per test (Testcontainers `postgres:18.6`),
so the authoritative record is the exact assertion set the named tests
execute against it; nothing below is hand-written SQL output. Test names
resolve in `evidence/member-a/w10-saga/test-output.txt` §3/§4 and in
`tests/Integration/RideHailing.TripService.IntegrationTests/TripW10SagaTests.cs`.

## Claim

A terminal (DECLINED) authorization failure cancels the ride and compensates
by releasing the **exact** reservation — never a Driver id alone, never a
newer reservation — while an UNCERTAIN (transient/UNKNOWN) outcome never
compensates. Verified code commit: see `manifest.json` (`codeCommit`).

## Observed final states (per test)

`DeclinedAuthorization_CompensatesWithExactRelease_UncertainNeverCompensates`
— after `PaymentAuthorizationFailedV1` with `resultCode=TRANSIENT_FAILURE`
(UNCERTAIN):

- `ride_sagas`: status `ACTIVE`, step `AUTHORIZE_PENDING`, `last_error_code
  = TRANSIENT_FAILURE`, authorize operationKey unchanged.
- `trips`: state `DRIVER_ACCEPTED` (unchanged).
- `outbox_messages`: no intent whose message type contains `Release`.

…then after the terminal `DECLINED` fact (single atomic commit):

- `trips`: state `CANCELLED`.
- `ride_sagas`: status `COMPENSATING`, step `RELEASE_PENDING`.
- `outbox_messages`: exactly one `ReleaseDriverReservationV1` whose payload
  carries the exact lineage tuple `tripId`, `driverId`, `reservationId`,
  `matchingRound = 1` (DSI-033: exact identity, never a Driver id alone);
  exactly one `CancelMatchingRoundV1`; exactly one `AuthorizePaymentV1`;
  **zero** `CapturePaymentV1`/`VoidPaymentAuthorizationV1` (compensation is
  release-only).
- `trips.cleanup_intents`: every `ReconcilePayment` intent has
  `completed_at` set — DECLINED is conclusive no-effect evidence, so the
  reconcile obligation is discharged in the same commit.
- After the late `DriverReservationReleasedV1` confirmation:
  `ride_sagas.status = COMPENSATED`.

`DeclinedAfterCancel_ReleasesWithoutVoid_AndStaleSuccessCannotRevive`
— DECLINED resolved after rider cancel:

- `ride_sagas`: step `RELEASE_PENDING`, `void_operation_key` NULL; no
  `VoidPaymentAuthorizationV1` intent exists; `trips.state = CANCELLED`
  (a stale success cannot revive the cancelled ride).

`AuthorizedAfterCancel_DispatchesExactVoid_CompensationNeedsBothHalves`
— authorization wins the race after cancel:

- Exact `VoidPaymentAuthorizationV1` dispatched under the frozen key
  `{tripId:D}:void:v1` (`RideSagaOperationKeys.Void`); a foreign void
  lineage (`…:void:v9`) classifies `NeedsReview` and never resolves the
  saga; compensation terminates `COMPENSATED` only after BOTH the driver
  release confirmation and the void outcome resolve.

`CancelRace_ReviewSignalNeverOwnsCompensation_VoidStillDispatchesAndResolves`
— a Payment `REQUIRES_REVIEW` signal for the authorize key is an observation
only (saga stays `COMPENSATING`), and the exact void still dispatches and
resolves to `COMPENSATED`.

`CancelRace_ReleaseConfirmedFirst_LaterDeclinedFinishesCompensation`
- release-first ordering still completes compensation.

## Reservation lineage and the "no wrong release" fence

The release obligation is derived from the persisted reservation lineage
(`reservationId` + `matchingRound`) committed with the acceptance, and the
outbox intent identity is deterministic
(`trip:{tripId:N}:release:{reservationId:N}`, W09-frozen), so a newer
reservation can never be released by an older obligation: a second logical
release for the same reservation is blocked by the deterministic identity
(W09 `DuplicateLogicalIntent_BlocksStateCommit_ThenLeavesOneRow`), and the
release payload assertions above pin the exact tuple.

## Provider scope

Payment outcome envelopes are authoritative-shaped facts delivered through
the real consumer transaction; the C-side Payment provider effect execution
is NOT claimed (3-service integration `INTEGRATION_PENDING`, see
`manifest.json` `crossOwnerPending`).
