# A-W12-06 — invariant-auditor execution record

Auditor: `TripInvariantAuditor` (test project; read-only SQL over the
Trip-owned database in disposable test environments; verification tooling,
not business code — Doc 03 §11.1).

Executed on the verified code revision `976c7f8662e05c0abe17a0dedcf8a45d94b03592`
against real PostgreSQL 18.6 (Testcontainers), one disposable database per
scenario. Verified by `TripW12InvariantAuditorTests` (all six lanes green in
the W12 focused run and the full-suite run recorded in `test-output.txt`).

## Run 1 — compensated scenario (real handlers end-to-end)

Scenario (all through the REAL handlers, single transactions):
`Trip.Create → StartMatching → ApplyDriverReserved → RecordDriverAccepted
(saga ACTIVE/AUTHORIZE_PENDING + AuthorizePayment intent) → CancelRide
(saga → COMPENSATING) → DriverReservationReleasedV1 (release obligation
completed) → PaymentAuthorizationFailedV1 resultCode=DECLINED on the frozen
authorize lineage (saga → COMPENSATED)`.

Asserted verdicts (the test asserts each status individually):

```text
AUD-01 Exclusive Driver Reservation ........ NOT RUN   (B-owned Driver DB; no cross-service read access)
AUD-02 Driver State Coherence .............. NOT RUN   (B-owned Driver DB)
AUD-03 Single current Trip assignment ...... PASS
AUD-04 Driver/Trip Agreement ............... NOT RUN   (B-owned Driver reservation state)
AUD-05 Payment Logical Uniqueness .......... PASS      (Trip-side proxy; also DB-CHECK-enforced; Payment half NOT RUN)
AUD-06 Matching Terminal Uniqueness ........ PASS      (Trip-side proxy; Matching ledger half NOT RUN)
AUD-07 No leaked pre-start reservation ..... PASS
AUD-08 Workflow Resolution ................. PASS
Overall: PARTIAL (NOT RUN present)
```

The saga was asserted COMPENSATED before the audit ran. NOT RUN is a
recorded dependency, never a silent green cell.

## Non-vacuity runs (seeded violations must FAIL — and did)

| Lane | Seed | Asserted outcome |
| --- | --- | --- |
| SeededLeakedReservation | terminal CANCELLED trip holding an assignment with NO release obligation | AUD-07 FAIL, finding "leaked pre-start reservation" |
| OpenReleaseObligation | release obligation present but open | AUD-07 FAIL when quiesced; PASS mid-flight with a "recognized-pending" note (Doc 03 §11.3 observation modes) |
| StuckOutboxAndSaga | pending outbox row + REQUIRES_REVIEW saga, both 2 h old | AUD-08 FAIL with per-row ages in the findings |
| ConflictingAssignmentReferences | open release intent pointing at a foreign reservation | AUD-03 FAIL, "not the single current assignment" |
| ContradictoryTerminalReceipts | applied DriverReservedV1 AND MatchingFailedV1, same (trip, round) | AUD-06 FAIL, "applied BOTH" |

AUD-05's duplicate-key half is not seedable through SQL without violating
the `ride_sagas` lineage CHECK + UNIQUE(trip_id) constraints — the
invariant is structurally enforced at the storage layer; the auditor still
re-verifies it read-only (defense in depth), and this is recorded in the
check note and in the G4 matrix.

## Reproduce

```bash
dotnet test tests/Integration/RideHailing.TripService.IntegrationTests \
  --filter "FullyQualifiedName~TripW12InvariantAuditorTests"
```

Lane included in `reproduce.sh`.
