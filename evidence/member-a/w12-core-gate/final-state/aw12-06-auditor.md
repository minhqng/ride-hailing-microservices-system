# A-W12-06 — invariant-auditor execution record (reviewed & corrected 2026-10-26)

Auditor: `TripInvariantAuditor` (test project; read-only SQL over the
Trip-owned database in disposable test environments; verification tooling,
not business code — Doc 03 §11.1).

Verdict levels (encoded in `AuditStatus`, rendered in every report):
- **PASS (Level A)** — the database that authoritatively owns the invariant
  was inspected.
- **LOCAL_PROXY_PASS (Level B)** — a Trip-local condition supports a
  distributed invariant but does not prove the other service's final state.
  Supporting evidence only; never counts as a full G4 pass for the property.
- **NOT RUN** — the required proof was not executed; owner/dependency recorded.
- **FAIL** — executed and violated.

## Authority matrix (AUD-01..08)

| Check | Authoritative database(s) required | Level reached by A | Verdict emitted |
| --- | --- | --- | --- |
| AUD-01 exclusive Driver reservation | Driver (B) | not executed | NOT RUN |
| AUD-02 Driver state coherence | Driver (B) | not executed | NOT RUN |
| AUD-03 one current Trip assignment | **Trip** | A | PASS |
| AUD-04 Driver–Trip agreement | Driver (B) + Trip | not executed (Driver half) | NOT RUN |
| AUD-05 Payment logical effect uniqueness | Payment (C) + Trip | B (Trip obligation ledger: unique frozen operation keys, also DB-CHECK-enforced, re-verified read-only) | LOCAL_PROXY_PASS |
| AUD-06 Matching terminal uniqueness | Matching (B) + Trip | B (Trip applied-fact ledger: no contradictory applied terminal facts per (trip, round)) | LOCAL_PROXY_PASS |
| AUD-07 no leaked pre-start reservation | Driver (B) + Trip | B (Trip obligation ledger + Driver-confirmed releases; Driver's own reservation table not inspected) | LOCAL_PROXY_PASS |
| AUD-08 Outbox/Saga unresolved age | **Trip** (outbox_messages, ride_sagas) | A | PASS |

Executed against real PostgreSQL 18.6 (Testcontainers), one disposable
database per scenario, verified by `TripW12InvariantAuditorTests` (6/6 green
on the corrected revision `acaaa68`; the scenario flows and queries are
unchanged from the first delivery — only the verdict classification
changed, after the F2 review found proxy-level proofs surfaced as
machine-readable PASS).

## Run 1 — compensated scenario (real handlers end-to-end)

Scenario (all through the REAL handlers, single transactions):
`Trip.Create → StartMatching → ApplyDriverReserved → RecordDriverAccepted
(saga ACTIVE/AUTHORIZE_PENDING + AuthorizePayment intent) → CancelRide
(saga → COMPENSATING) → DriverReservationReleasedV1 (release obligation
completed) → PaymentAuthorizationFailedV1 resultCode=DECLINED on the frozen
authorize lineage (saga → COMPENSATED)`.

Asserted verdicts (each asserted individually by the test):

```text
AUD-01 Exclusive Driver Reservation ........ NOT RUN          (B-owned Driver DB; no cross-service read access)
AUD-02 Driver State Coherence .............. NOT RUN          (B-owned Driver DB)
AUD-03 Single current Trip assignment ...... PASS             (Level A: trips + trip_cleanup_intents)
AUD-04 Driver/Trip Agreement ............... NOT RUN          (B-owned Driver reservation state)
AUD-05 Payment Logical Uniqueness .......... LOCAL_PROXY_PASS (Level B; Payment effect ledger = C data)
AUD-06 Matching Terminal Uniqueness ........ LOCAL_PROXY_PASS (Level B; Matching ledger = B data)
AUD-07 No leaked pre-start reservation ..... LOCAL_PROXY_PASS (Level B; Driver reservation table = B data)
AUD-08 Workflow Resolution ................. PASS             (Level A: outbox_messages + ride_sagas)
Overall: PARTIAL (NOT RUN/LOCAL_PROXY present)
```

The saga was asserted COMPENSATED before the audit ran. NOT RUN and
LOCAL_PROXY_PASS are recorded dependencies/classifications, never silent
green cells; the report prints the legend above on every run.

## Non-vacuity runs (seeded violations must FAIL — and did)

| Lane | Seed | Asserted outcome |
| --- | --- | --- |
| SeededLeakedReservation | terminal CANCELLED trip holding an assignment with NO release obligation | AUD-07 FAIL, finding "leaked pre-start reservation" |
| OpenReleaseObligation | release obligation present but open | AUD-07 FAIL when quiesced; LOCAL_PROXY_PASS mid-flight with a "recognized-pending" note (Doc 03 §11.3 observation modes) |
| StuckOutboxAndSaga | pending outbox row + REQUIRES_REVIEW saga, both 2 h old | AUD-08 FAIL with per-row ages in the findings |
| ConflictingAssignmentReferences | open release intent pointing at a foreign reservation | AUD-03 FAIL, "not the single current assignment" |
| ContradictoryTerminalReceipts | applied DriverReservedV1 AND MatchingFailedV1, same (trip, round) | AUD-06 FAIL, "applied BOTH" |

AUD-05's duplicate-key half is not seedable through SQL without violating
the `ride_sagas` lineage CHECK + UNIQUE(trip_id) constraints — the
Trip-owned half of the invariant is structurally enforced at the storage
layer; the auditor still re-verifies it read-only (defense in depth).

## Cross-owner obligation for full AUD coverage (Level C)

The canonical verification design allows the external test auditor read-only
cross-service access when explicitly configured. Until B/C provide it:
- B supplies Driver-DB read access (or curated final-state exports) →
  AUD-01/02/04 executable; full AUD-07.
- C supplies Payment-DB read access → full AUD-05.
- B supplies Matching-DB read access → full AUD-06.

## Reproduce

```bash
dotnet test tests/Integration/RideHailing.TripService.IntegrationTests \
  --filter "FullyQualifiedName~TripW12InvariantAuditorTests"
```

Lane included in `reproduce.sh`.
