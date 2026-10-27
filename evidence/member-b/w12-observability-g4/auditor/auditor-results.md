# B-W12-08 authoritative invariant auditor — results

- Source commit: `7bc4510cbf524406d545b7529c938414b85f9c9c`
- Source tree: `27ba100e59bec471489a5f7cdfaa5dc7c54361b9`
- Generated: `2026-10-27T04:17:43.2718192+00:00`
- Checks (all runs): PASS 52, LOCAL_PROXY_PASS 0, FAIL 8, NOT RUN 48, BLOCKED 4
- Positive scenarios only: PASS 31, FAIL 0, NOT RUN 17, BLOCKED 0
- Negative/control runs: FAIL 8 (expected detections), BLOCKED 4 (expected honesty), PASS 21, NOT RUN 31

Statuses are final per check; `LOCAL_PROXY_PASS` is supporting evidence only
and never a full PASS for the distributed invariant. `NOT RUN` records an
owner dependency; `BLOCKED` means the required authority was unreachable.

## Run `B12-AUD-cancel-release-quiesced-20261027T041729Z-3bb608` — scenario `cancel-release-quiesced`

Observation: `POST_SCENARIO_QUIESCED_B_SCOPE`; elapsed 64 ms.

| Check | Status | Authority | Inspected | Finding codes | Limitation |
| --- | --- | --- | ---: | --- | --- |
| AUD-01 — Exclusive Driver reservation: at most one current active reservation/trip per Driver | **PASS** | driver_db | 9 | — | — |
| AUD-02 — Driver state coherence and lifecycle provenance | **PASS** | driver_db | 7 | — | Historical lifecycle_effects rows are never treated as current reservations; only the drivers row is active authority. |
| AUD-03 — One current Trip assignment | **NOT RUN** | trip_db (A-owned) | 0 | — | Trip assignment authority is A-owned (TripInvariantAuditor, PR #43). B executes AUD-04 against real Trip rows but does not duplicate the Trip-only audit; A's evidence is bound to a different source SHA/scenario set, so it is not reused as B's PASS. |
| AUD-04 — Driver–Trip exact reservation identity agreement | **PASS** | driver_db + trip_db | 5 | — | Driver is authoritative for reservation identity; Trip is authoritative for assignment; agreement is evaluated from both final states. |
| AUD-05 — Payment logical effect uniqueness | **NOT RUN** | payment_db (C-owned) | 0 | — | Payment authority is C-owned; no Payment database is supplied to the B audit. Explicit C dependency: payment effect ledger inspection. |
| AUD-06 — Matching terminal uniqueness and durable result intent | **PASS** | matching_db + driver_db | 11 | — | — |
| AUD-07 — No leaked pre-start Driver reservation after quiescence | **PASS** | trip_db + driver_db | 5 | — | Only the current drivers row is reservation authority; historical lifecycle effects are not leaks. |
| AUD-08 — Outbox/attempt unresolved state and age | **PASS** | driver_db + matching_db | 4 | `TRIP_SAGA_SCOPE` | Accepted policy: fresh PENDING <= 15 min, unresolved attempts/rounds <= 15 min, FAILED_VISIBLE is visible-by-design and reported for review. |

## Run `B12-AUD-control-empty-dataset-20261027T041727Z-552a32` — scenario `control-empty-dataset`

Observation: `EMPTY_DATASET_CONTROL`; elapsed 37 ms.

| Check | Status | Authority | Inspected | Finding codes | Limitation |
| --- | --- | --- | ---: | --- | --- |
| AUD-01 — Exclusive Driver reservation: at most one current active reservation/trip per Driver | **NOT RUN** | driver_db | 8 | — | Empty Driver dataset; PASS would be vacuous. |
| AUD-02 — Driver state coherence and lifecycle provenance | **NOT RUN** | driver_db | 6 | — | Empty Driver dataset; PASS would be vacuous. |
| AUD-03 — One current Trip assignment | **NOT RUN** | trip_db (A-owned) | 0 | — | Trip assignment authority is A-owned (TripInvariantAuditor, PR #43). B executes AUD-04 against real Trip rows but does not duplicate the Trip-only audit; A's evidence is bound to a different source SHA/scenario set, so it is not reused as B's PASS. |
| AUD-04 — Driver–Trip exact reservation identity agreement | **NOT RUN** | driver_db + trip_db | 0 | — | Empty Trip or Driver dataset; PASS would be vacuous. |
| AUD-05 — Payment logical effect uniqueness | **NOT RUN** | payment_db (C-owned) | 0 | — | Payment authority is C-owned; no Payment database is supplied to the B audit. Explicit C dependency: payment effect ledger inspection. |
| AUD-06 — Matching terminal uniqueness and durable result intent | **NOT RUN** | matching_db + driver_db | 7 | — | Empty Matching terminal dataset; PASS would be vacuous. |
| AUD-07 — No leaked pre-start Driver reservation after quiescence | **NOT RUN** | trip_db + driver_db | 0 | — | No terminal Trip in this scenario; leak PASS would be vacuous. |
| AUD-08 — Outbox/attempt unresolved state and age | **NOT RUN** | driver_db + matching_db | 0 | `TRIP_SAGA_SCOPE` | No Outbox/attempt/round rows inspected; PASS would be vacuous. |

## Run `B12-AUD-control-unreachable-trip-20261027T041733Z-f76128` — scenario `control-unreachable-trip`

Observation: `NEGATIVE_CONTROL_SEEDED`; elapsed 8035 ms.

| Check | Status | Authority | Inspected | Finding codes | Limitation |
| --- | --- | --- | ---: | --- | --- |
| AUD-01 — Exclusive Driver reservation: at most one current active reservation/trip per Driver | **NOT RUN** | driver_db | 8 | — | Empty Driver dataset; PASS would be vacuous. |
| AUD-02 — Driver state coherence and lifecycle provenance | **NOT RUN** | driver_db | 6 | — | Empty Driver dataset; PASS would be vacuous. |
| AUD-03 — One current Trip assignment | **NOT RUN** | trip_db (A-owned) | 0 | — | Trip assignment authority is A-owned (TripInvariantAuditor, PR #43). B executes AUD-04 against real Trip rows but does not duplicate the Trip-only audit; A's evidence is bound to a different source SHA/scenario set, so it is not reused as B's PASS. |
| AUD-04 — Driver–Trip exact reservation identity agreement | **BLOCKED** | driver_db + trip_db | 0 | `AUTHORITY_UNREACHABLE` | Unreachable/invalid authority dataset; not a business verdict. |
| AUD-05 — Payment logical effect uniqueness | **NOT RUN** | payment_db (C-owned) | 0 | — | Payment authority is C-owned; no Payment database is supplied to the B audit. Explicit C dependency: payment effect ledger inspection. |
| AUD-06 — Matching terminal uniqueness and durable result intent | **BLOCKED** | matching_db + driver_db | 0 | `AUTHORITY_UNREACHABLE` | Unreachable/invalid authority dataset; not a business verdict. |
| AUD-07 — No leaked pre-start Driver reservation after quiescence | **BLOCKED** | trip_db + driver_db | 0 | `AUTHORITY_UNREACHABLE` | Unreachable/invalid authority dataset; not a business verdict. |
| AUD-08 — Outbox/attempt unresolved state and age | **BLOCKED** | driver_db + matching_db | 0 | `AUTHORITY_UNREACHABLE` | Unreachable/invalid authority dataset; not a business verdict. |

## Run `B12-AUD-driver-lifecycle-20261027T041743Z-ae6b23` — scenario `driver-lifecycle`

Observation: `POST_SCENARIO_QUIESCED`; elapsed 9 ms.

| Check | Status | Authority | Inspected | Finding codes | Limitation |
| --- | --- | --- | ---: | --- | --- |
| AUD-01 — Exclusive Driver reservation: at most one current active reservation/trip per Driver | **PASS** | driver_db | 10 | — | — |
| AUD-02 — Driver state coherence and lifecycle provenance | **PASS** | driver_db | 11 | — | Historical lifecycle_effects rows are never treated as current reservations; only the drivers row is active authority. |
| AUD-03 — One current Trip assignment | **NOT RUN** | trip_db (A-owned) | 0 | — | Trip assignment authority is A-owned (TripInvariantAuditor, PR #43). B executes AUD-04 against real Trip rows but does not duplicate the Trip-only audit; A's evidence is bound to a different source SHA/scenario set, so it is not reused as B's PASS. |
| AUD-04 — Driver–Trip exact reservation identity agreement | **NOT RUN** | driver_db + trip_db | 0 | — | Trip database not supplied to this scenario; cross-service agreement cannot be evaluated. |
| AUD-05 — Payment logical effect uniqueness | **NOT RUN** | payment_db (C-owned) | 0 | — | Payment authority is C-owned; no Payment database is supplied to the B audit. Explicit C dependency: payment effect ledger inspection. |
| AUD-06 — Matching terminal uniqueness and durable result intent | **NOT RUN** | matching_db + driver_db | 0 | — | Matching database not supplied; terminal uniqueness cannot be evaluated. |
| AUD-07 — No leaked pre-start Driver reservation after quiescence | **NOT RUN** | trip_db + driver_db | 0 | — | Trip database not supplied; terminal-trip leak detection cannot be evaluated. |
| AUD-08 — Outbox/attempt unresolved state and age | **NOT RUN** | driver_db + matching_db | 0 | — | Matching database not supplied; Outbox/attempt resolution cannot be evaluated. |

## Run `B12-AUD-late-release-fenced-20261027T041726Z-849fd4` — scenario `late-release-fenced`

Observation: `POST_SCENARIO_QUIESCED`; elapsed 102 ms.

| Check | Status | Authority | Inspected | Finding codes | Limitation |
| --- | --- | --- | ---: | --- | --- |
| AUD-01 — Exclusive Driver reservation: at most one current active reservation/trip per Driver | **PASS** | driver_db | 9 | — | — |
| AUD-02 — Driver state coherence and lifecycle provenance | **PASS** | driver_db | 7 | — | Historical lifecycle_effects rows are never treated as current reservations; only the drivers row is active authority. |
| AUD-03 — One current Trip assignment | **NOT RUN** | trip_db (A-owned) | 0 | — | Trip assignment authority is A-owned (TripInvariantAuditor, PR #43). B executes AUD-04 against real Trip rows but does not duplicate the Trip-only audit; A's evidence is bound to a different source SHA/scenario set, so it is not reused as B's PASS. |
| AUD-04 — Driver–Trip exact reservation identity agreement | **PASS** | driver_db + trip_db | 6 | — | Driver is authoritative for reservation identity; Trip is authoritative for assignment; agreement is evaluated from both final states. |
| AUD-05 — Payment logical effect uniqueness | **NOT RUN** | payment_db (C-owned) | 0 | — | Payment authority is C-owned; no Payment database is supplied to the B audit. Explicit C dependency: payment effect ledger inspection. |
| AUD-06 — Matching terminal uniqueness and durable result intent | **PASS** | matching_db + driver_db | 14 | — | — |
| AUD-07 — No leaked pre-start Driver reservation after quiescence | **PASS** | trip_db + driver_db | 5 | — | Only the current drivers row is reservation authority; historical lifecycle effects are not leaks. |
| AUD-08 — Outbox/attempt unresolved state and age | **PASS** | driver_db + matching_db | 7 | `TRIP_SAGA_SCOPE` | Accepted policy: fresh PENDING <= 15 min, unresolved attempts/rounds <= 15 min, FAILED_VISIBLE is visible-by-design and reported for review. |

## Run `B12-AUD-negative-contradictory-terminal-20261027T041743Z-8c9296` — scenario `negative-contradictory-terminal`

Observation: `NEGATIVE_CONTROL_SEEDED`; elapsed 33 ms.

| Check | Status | Authority | Inspected | Finding codes | Limitation |
| --- | --- | --- | ---: | --- | --- |
| AUD-01 — Exclusive Driver reservation: at most one current active reservation/trip per Driver | **PASS** | driver_db | 9 | — | — |
| AUD-02 — Driver state coherence and lifecycle provenance | **PASS** | driver_db | 7 | — | Historical lifecycle_effects rows are never treated as current reservations; only the drivers row is active authority. |
| AUD-03 — One current Trip assignment | **NOT RUN** | trip_db (A-owned) | 0 | — | Trip assignment authority is A-owned (TripInvariantAuditor, PR #43). B executes AUD-04 against real Trip rows but does not duplicate the Trip-only audit; A's evidence is bound to a different source SHA/scenario set, so it is not reused as B's PASS. |
| AUD-04 — Driver–Trip exact reservation identity agreement | **PASS** | driver_db + trip_db | 2 | — | Driver is authoritative for reservation identity; Trip is authoritative for assignment; agreement is evaluated from both final states. |
| AUD-05 — Payment logical effect uniqueness | **NOT RUN** | payment_db (C-owned) | 0 | — | Payment authority is C-owned; no Payment database is supplied to the B audit. Explicit C dependency: payment effect ledger inspection. |
| AUD-06 — Matching terminal uniqueness and durable result intent | **FAIL** | matching_db + driver_db | 11 | `MULTIPLE_RESERVED_ATTEMPTS` | — |
| AUD-07 — No leaked pre-start Driver reservation after quiescence | **NOT RUN** | trip_db + driver_db | 1 | — | No terminal Trip in this scenario; leak PASS would be vacuous. |
| AUD-08 — Outbox/attempt unresolved state and age | **PASS** | driver_db + matching_db | 4 | `TRIP_SAGA_SCOPE` | Accepted policy: fresh PENDING <= 15 min, unresolved attempts/rounds <= 15 min, FAILED_VISIBLE is visible-by-design and reported for review. |

## Run `B12-AUD-negative-disagreement-20261027T041732Z-d5c853` — scenario `negative-disagreement`

Observation: `NEGATIVE_CONTROL_SEEDED`; elapsed 37 ms.

| Check | Status | Authority | Inspected | Finding codes | Limitation |
| --- | --- | --- | ---: | --- | --- |
| AUD-01 — Exclusive Driver reservation: at most one current active reservation/trip per Driver | **PASS** | driver_db | 9 | — | — |
| AUD-02 — Driver state coherence and lifecycle provenance | **PASS** | driver_db | 7 | — | Historical lifecycle_effects rows are never treated as current reservations; only the drivers row is active authority. |
| AUD-03 — One current Trip assignment | **NOT RUN** | trip_db (A-owned) | 0 | — | Trip assignment authority is A-owned (TripInvariantAuditor, PR #43). B executes AUD-04 against real Trip rows but does not duplicate the Trip-only audit; A's evidence is bound to a different source SHA/scenario set, so it is not reused as B's PASS. |
| AUD-04 — Driver–Trip exact reservation identity agreement | **FAIL** | driver_db + trip_db | 2 | `DRIVER_TRIP_DISAGREEMENT`, `DRIVER_TRIP_DISAGREEMENT` | Driver is authoritative for reservation identity; Trip is authoritative for assignment; agreement is evaluated from both final states. |
| AUD-05 — Payment logical effect uniqueness | **NOT RUN** | payment_db (C-owned) | 0 | — | Payment authority is C-owned; no Payment database is supplied to the B audit. Explicit C dependency: payment effect ledger inspection. |
| AUD-06 — Matching terminal uniqueness and durable result intent | **PASS** | matching_db + driver_db | 10 | — | — |
| AUD-07 — No leaked pre-start Driver reservation after quiescence | **NOT RUN** | trip_db + driver_db | 1 | — | No terminal Trip in this scenario; leak PASS would be vacuous. |
| AUD-08 — Outbox/attempt unresolved state and age | **PASS** | driver_db + matching_db | 3 | `TRIP_SAGA_SCOPE` | Accepted policy: fresh PENDING <= 15 min, unresolved attempts/rounds <= 15 min, FAILED_VISIBLE is visible-by-design and reported for review. |

## Run `B12-AUD-negative-false-cleanup-20261027T041728Z-d71006` — scenario `negative-false-cleanup`

Observation: `NEGATIVE_CONTROL_SEEDED`; elapsed 51 ms.

| Check | Status | Authority | Inspected | Finding codes | Limitation |
| --- | --- | --- | ---: | --- | --- |
| AUD-01 — Exclusive Driver reservation: at most one current active reservation/trip per Driver | **PASS** | driver_db | 9 | — | — |
| AUD-02 — Driver state coherence and lifecycle provenance | **PASS** | driver_db | 7 | — | Historical lifecycle_effects rows are never treated as current reservations; only the drivers row is active authority. |
| AUD-03 — One current Trip assignment | **NOT RUN** | trip_db (A-owned) | 0 | — | Trip assignment authority is A-owned (TripInvariantAuditor, PR #43). B executes AUD-04 against real Trip rows but does not duplicate the Trip-only audit; A's evidence is bound to a different source SHA/scenario set, so it is not reused as B's PASS. |
| AUD-04 — Driver–Trip exact reservation identity agreement | **FAIL** | driver_db + trip_db | 5 | `TERMINAL_TRIP_WITH_ACTIVE_RESERVATION`, `DRIVER_POINTS_AT_TERMINAL_TRIP` | Driver is authoritative for reservation identity; Trip is authoritative for assignment; agreement is evaluated from both final states. |
| AUD-05 — Payment logical effect uniqueness | **NOT RUN** | payment_db (C-owned) | 0 | — | Payment authority is C-owned; no Payment database is supplied to the B audit. Explicit C dependency: payment effect ledger inspection. |
| AUD-06 — Matching terminal uniqueness and durable result intent | **PASS** | matching_db + driver_db | 10 | — | — |
| AUD-07 — No leaked pre-start Driver reservation after quiescence | **FAIL** | trip_db + driver_db | 5 | `LEAKED_RESERVATION` | Only the current drivers row is reservation authority; historical lifecycle effects are not leaks. |
| AUD-08 — Outbox/attempt unresolved state and age | **PASS** | driver_db + matching_db | 3 | `TRIP_SAGA_SCOPE` | Accepted policy: fresh PENDING <= 15 min, unresolved attempts/rounds <= 15 min, FAILED_VISIBLE is visible-by-design and reported for review. |

## Run `B12-AUD-negative-missing-intent-20261027T041742Z-54bdee` — scenario `negative-missing-intent`

Observation: `NEGATIVE_CONTROL_SEEDED`; elapsed 34 ms.

| Check | Status | Authority | Inspected | Finding codes | Limitation |
| --- | --- | --- | ---: | --- | --- |
| AUD-01 — Exclusive Driver reservation: at most one current active reservation/trip per Driver | **PASS** | driver_db | 10 | — | — |
| AUD-02 — Driver state coherence and lifecycle provenance | **PASS** | driver_db | 8 | — | Historical lifecycle_effects rows are never treated as current reservations; only the drivers row is active authority. |
| AUD-03 — One current Trip assignment | **NOT RUN** | trip_db (A-owned) | 0 | — | Trip assignment authority is A-owned (TripInvariantAuditor, PR #43). B executes AUD-04 against real Trip rows but does not duplicate the Trip-only audit; A's evidence is bound to a different source SHA/scenario set, so it is not reused as B's PASS. |
| AUD-04 — Driver–Trip exact reservation identity agreement | **PASS** | driver_db + trip_db | 3 | — | Driver is authoritative for reservation identity; Trip is authoritative for assignment; agreement is evaluated from both final states. |
| AUD-05 — Payment logical effect uniqueness | **NOT RUN** | payment_db (C-owned) | 0 | — | Payment authority is C-owned; no Payment database is supplied to the B audit. Explicit C dependency: payment effect ledger inspection. |
| AUD-06 — Matching terminal uniqueness and durable result intent | **FAIL** | matching_db + driver_db | 10 | `MISSING_OR_MISMATCHED_FAILURE_INTENT` | — |
| AUD-07 — No leaked pre-start Driver reservation after quiescence | **NOT RUN** | trip_db + driver_db | 2 | — | No terminal Trip in this scenario; leak PASS would be vacuous. |
| AUD-08 — Outbox/attempt unresolved state and age | **PASS** | driver_db + matching_db | 3 | `TRIP_SAGA_SCOPE` | Accepted policy: fresh PENDING <= 15 min, unresolved attempts/rounds <= 15 min, FAILED_VISIBLE is visible-by-design and reported for review. |

## Run `B12-AUD-negative-stuck-cleanup-20261027T041733Z-7120f2` — scenario `negative-stuck-cleanup`

Observation: `NEGATIVE_CONTROL_SEEDED`; elapsed 36 ms.

| Check | Status | Authority | Inspected | Finding codes | Limitation |
| --- | --- | --- | ---: | --- | --- |
| AUD-01 — Exclusive Driver reservation: at most one current active reservation/trip per Driver | **PASS** | driver_db | 9 | — | — |
| AUD-02 — Driver state coherence and lifecycle provenance | **PASS** | driver_db | 7 | — | Historical lifecycle_effects rows are never treated as current reservations; only the drivers row is active authority. |
| AUD-03 — One current Trip assignment | **NOT RUN** | trip_db (A-owned) | 0 | — | Trip assignment authority is A-owned (TripInvariantAuditor, PR #43). B executes AUD-04 against real Trip rows but does not duplicate the Trip-only audit; A's evidence is bound to a different source SHA/scenario set, so it is not reused as B's PASS. |
| AUD-04 — Driver–Trip exact reservation identity agreement | **FAIL** | driver_db + trip_db | 5 | `STUCK_CLEANUP_OBLIGATION`, `DRIVER_POINTS_AT_TERMINAL_TRIP` | Driver is authoritative for reservation identity; Trip is authoritative for assignment; agreement is evaluated from both final states. |
| AUD-05 — Payment logical effect uniqueness | **NOT RUN** | payment_db (C-owned) | 0 | — | Payment authority is C-owned; no Payment database is supplied to the B audit. Explicit C dependency: payment effect ledger inspection. |
| AUD-06 — Matching terminal uniqueness and durable result intent | **PASS** | matching_db + driver_db | 10 | — | — |
| AUD-07 — No leaked pre-start Driver reservation after quiescence | **FAIL** | trip_db + driver_db | 5 | `STUCK_CLEANUP_OBLIGATION` | Only the current drivers row is reservation authority; historical lifecycle effects are not leaks. |
| AUD-08 — Outbox/attempt unresolved state and age | **PASS** | driver_db + matching_db | 3 | `TRIP_SAGA_SCOPE` | Accepted policy: fresh PENDING <= 15 min, unresolved attempts/rounds <= 15 min, FAILED_VISIBLE is visible-by-design and reported for review. |

## Run `B12-AUD-negative-stuck-unresolved-20261027T041727Z-c1f5b7` — scenario `negative-stuck-unresolved`

Observation: `NEGATIVE_CONTROL_SEEDED`; elapsed 36 ms.

| Check | Status | Authority | Inspected | Finding codes | Limitation |
| --- | --- | --- | ---: | --- | --- |
| AUD-01 — Exclusive Driver reservation: at most one current active reservation/trip per Driver | **NOT RUN** | driver_db | 8 | — | Empty Driver dataset; PASS would be vacuous. |
| AUD-02 — Driver state coherence and lifecycle provenance | **NOT RUN** | driver_db | 6 | — | Empty Driver dataset; PASS would be vacuous. |
| AUD-03 — One current Trip assignment | **NOT RUN** | trip_db (A-owned) | 0 | — | Trip assignment authority is A-owned (TripInvariantAuditor, PR #43). B executes AUD-04 against real Trip rows but does not duplicate the Trip-only audit; A's evidence is bound to a different source SHA/scenario set, so it is not reused as B's PASS. |
| AUD-04 — Driver–Trip exact reservation identity agreement | **NOT RUN** | driver_db + trip_db | 0 | — | Empty Trip or Driver dataset; PASS would be vacuous. |
| AUD-05 — Payment logical effect uniqueness | **NOT RUN** | payment_db (C-owned) | 0 | — | Payment authority is C-owned; no Payment database is supplied to the B audit. Explicit C dependency: payment effect ledger inspection. |
| AUD-06 — Matching terminal uniqueness and durable result intent | **PASS** | matching_db + driver_db | 10 | — | — |
| AUD-07 — No leaked pre-start Driver reservation after quiescence | **NOT RUN** | trip_db + driver_db | 0 | — | No terminal Trip in this scenario; leak PASS would be vacuous. |
| AUD-08 — Outbox/attempt unresolved state and age | **FAIL** | driver_db + matching_db | 3 | `STUCK_PENDING_OUTBOX`, `STUCK_UNRESOLVED_ATTEMPT`, `STUCK_PROCESSING_ROUND`, `TRIP_SAGA_SCOPE` | Accepted policy: fresh PENDING <= 15 min, unresolved attempts/rounds <= 15 min, FAILED_VISIBLE is visible-by-design and reported for review. |

## Run `B12-AUD-no-driver-terminal-20261027T041731Z-855607` — scenario `no-driver-terminal`

Observation: `POST_SCENARIO_QUIESCED`; elapsed 37 ms.

| Check | Status | Authority | Inspected | Finding codes | Limitation |
| --- | --- | --- | ---: | --- | --- |
| AUD-01 — Exclusive Driver reservation: at most one current active reservation/trip per Driver | **PASS** | driver_db | 10 | — | — |
| AUD-02 — Driver state coherence and lifecycle provenance | **PASS** | driver_db | 8 | — | Historical lifecycle_effects rows are never treated as current reservations; only the drivers row is active authority. |
| AUD-03 — One current Trip assignment | **NOT RUN** | trip_db (A-owned) | 0 | — | Trip assignment authority is A-owned (TripInvariantAuditor, PR #43). B executes AUD-04 against real Trip rows but does not duplicate the Trip-only audit; A's evidence is bound to a different source SHA/scenario set, so it is not reused as B's PASS. |
| AUD-04 — Driver–Trip exact reservation identity agreement | **PASS** | driver_db + trip_db | 3 | — | Driver is authoritative for reservation identity; Trip is authoritative for assignment; agreement is evaluated from both final states. |
| AUD-05 — Payment logical effect uniqueness | **NOT RUN** | payment_db (C-owned) | 0 | — | Payment authority is C-owned; no Payment database is supplied to the B audit. Explicit C dependency: payment effect ledger inspection. |
| AUD-06 — Matching terminal uniqueness and durable result intent | **PASS** | matching_db + driver_db | 11 | — | — |
| AUD-07 — No leaked pre-start Driver reservation after quiescence | **PASS** | trip_db + driver_db | 3 | — | Only the current drivers row is reservation authority; historical lifecycle effects are not leaks. |
| AUD-08 — Outbox/attempt unresolved state and age | **PASS** | driver_db + matching_db | 4 | `TRIP_SAGA_SCOPE` | Accepted policy: fresh PENDING <= 15 min, unresolved attempts/rounds <= 15 min, FAILED_VISIBLE is visible-by-design and reported for review. |

## Run `B12-AUD-recognized-pending-cleanup-20261027T041730Z-ed3d2f` — scenario `recognized-pending-cleanup`

Observation: `MID_FLIGHT_OBSERVATION`; elapsed 55 ms.

| Check | Status | Authority | Inspected | Finding codes | Limitation |
| --- | --- | --- | ---: | --- | --- |
| AUD-01 — Exclusive Driver reservation: at most one current active reservation/trip per Driver | **PASS** | driver_db | 9 | — | — |
| AUD-02 — Driver state coherence and lifecycle provenance | **PASS** | driver_db | 7 | — | Historical lifecycle_effects rows are never treated as current reservations; only the drivers row is active authority. |
| AUD-03 — One current Trip assignment | **NOT RUN** | trip_db (A-owned) | 0 | — | Trip assignment authority is A-owned (TripInvariantAuditor, PR #43). B executes AUD-04 against real Trip rows but does not duplicate the Trip-only audit; A's evidence is bound to a different source SHA/scenario set, so it is not reused as B's PASS. |
| AUD-04 — Driver–Trip exact reservation identity agreement | **PASS** | driver_db + trip_db | 5 | `RECOGNIZED_PENDING_CLEANUP` | Driver is authoritative for reservation identity; Trip is authoritative for assignment; agreement is evaluated from both final states. |
| AUD-05 — Payment logical effect uniqueness | **NOT RUN** | payment_db (C-owned) | 0 | — | Payment authority is C-owned; no Payment database is supplied to the B audit. Explicit C dependency: payment effect ledger inspection. |
| AUD-06 — Matching terminal uniqueness and durable result intent | **PASS** | matching_db + driver_db | 10 | — | — |
| AUD-07 — No leaked pre-start Driver reservation after quiescence | **PASS** | trip_db + driver_db | 5 | `RECOGNIZED_PENDING_CLEANUP` | Only the current drivers row is reservation authority; historical lifecycle effects are not leaks. |
| AUD-08 — Outbox/attempt unresolved state and age | **PASS** | driver_db + matching_db | 3 | `TRIP_SAGA_SCOPE` | Accepted policy: fresh PENDING <= 15 min, unresolved attempts/rounds <= 15 min, FAILED_VISIBLE is visible-by-design and reported for review. |

## Run `B12-AUD-reserved-agreement-20261027T041727Z-d30a8f` — scenario `reserved-agreement`

Observation: `POST_SCENARIO_QUIESCED`; elapsed 39 ms.

| Check | Status | Authority | Inspected | Finding codes | Limitation |
| --- | --- | --- | ---: | --- | --- |
| AUD-01 — Exclusive Driver reservation: at most one current active reservation/trip per Driver | **PASS** | driver_db | 9 | — | — |
| AUD-02 — Driver state coherence and lifecycle provenance | **PASS** | driver_db | 7 | — | Historical lifecycle_effects rows are never treated as current reservations; only the drivers row is active authority. |
| AUD-03 — One current Trip assignment | **NOT RUN** | trip_db (A-owned) | 0 | — | Trip assignment authority is A-owned (TripInvariantAuditor, PR #43). B executes AUD-04 against real Trip rows but does not duplicate the Trip-only audit; A's evidence is bound to a different source SHA/scenario set, so it is not reused as B's PASS. |
| AUD-04 — Driver–Trip exact reservation identity agreement | **PASS** | driver_db + trip_db | 2 | — | Driver is authoritative for reservation identity; Trip is authoritative for assignment; agreement is evaluated from both final states. |
| AUD-05 — Payment logical effect uniqueness | **NOT RUN** | payment_db (C-owned) | 0 | — | Payment authority is C-owned; no Payment database is supplied to the B audit. Explicit C dependency: payment effect ledger inspection. |
| AUD-06 — Matching terminal uniqueness and durable result intent | **PASS** | matching_db + driver_db | 10 | — | — |
| AUD-07 — No leaked pre-start Driver reservation after quiescence | **NOT RUN** | trip_db + driver_db | 1 | — | No terminal Trip in this scenario; leak PASS would be vacuous. |
| AUD-08 — Outbox/attempt unresolved state and age | **PASS** | driver_db + matching_db | 3 | `TRIP_SAGA_SCOPE` | Accepted policy: fresh PENDING <= 15 min, unresolved attempts/rounds <= 15 min, FAILED_VISIBLE is visible-by-design and reported for review. |

## Finding detail

### `cancel-release-quiesced` / AUD-08 — PASS
- `TRIP_SAGA_SCOPE`: Trip Saga/Outbox unresolved age is A-owned and intentionally not inspected here; A's AUD-08 PASS covers the Trip authority.

### `control-empty-dataset` / AUD-08 — NOT RUN
- `TRIP_SAGA_SCOPE`: Trip Saga/Outbox unresolved age is A-owned and intentionally not inspected here; A's AUD-08 PASS covers the Trip authority.

### `control-unreachable-trip` / AUD-04 — BLOCKED
- `AUTHORITY_UNREACHABLE`: AUD-04 could not inspect its authority: NpgsqlException: Failed to connect to 127.0.0.1:1

### `control-unreachable-trip` / AUD-06 — BLOCKED
- `AUTHORITY_UNREACHABLE`: AUD-06 could not inspect its authority: NpgsqlException: Failed to connect to 127.0.0.1:1

### `control-unreachable-trip` / AUD-07 — BLOCKED
- `AUTHORITY_UNREACHABLE`: AUD-07 could not inspect its authority: NpgsqlException: Failed to connect to 127.0.0.1:1

### `control-unreachable-trip` / AUD-08 — BLOCKED
- `AUTHORITY_UNREACHABLE`: AUD-08 could not inspect its authority: NpgsqlException: Failed to connect to 127.0.0.1:1

### `late-release-fenced` / AUD-08 — PASS
- `TRIP_SAGA_SCOPE`: Trip Saga/Outbox unresolved age is A-owned and intentionally not inspected here; A's AUD-08 PASS covers the Trip authority.

### `negative-contradictory-terminal` / AUD-06 — FAIL
- `MULTIPLE_RESERVED_ATTEMPTS`: RESERVED round has 2 RESERVED attempt rows; exactly one is required. Record: be4eaf38-f8f8-41d3-996d-9e3580f21f2a/1.

### `negative-contradictory-terminal` / AUD-08 — PASS
- `TRIP_SAGA_SCOPE`: Trip Saga/Outbox unresolved age is A-owned and intentionally not inspected here; A's AUD-08 PASS covers the Trip authority.

### `negative-disagreement` / AUD-04 — FAIL
- `DRIVER_TRIP_DISAGREEMENT`: Trip expects driver=a9fb23a9-63c1-41a5-8e45-e8cd73b0b467 trip=bb85b66e-d92b-4811-a84e-3e1a7c57f3fe reservation=234b833f-46bf-4086-946c-8a16fbf3d325 round=1; Driver row is status=RESERVED, reservedTrip=bb85b66e-d92b-4811-a84e-3e1a7c57f3fe, reservation=96c9d628-fbbe-435e-9c5b-ad6257e3e52d, round=1. Record: bb85b66e-d92b-4811-a84e-3e1a7c57f3fe.
- `DRIVER_TRIP_DISAGREEMENT`: Driver current reservation identity does not match the Trip assignment. Record: a9fb23a9-63c1-41a5-8e45-e8cd73b0b467/bb85b66e-d92b-4811-a84e-3e1a7c57f3fe.

### `negative-disagreement` / AUD-08 — PASS
- `TRIP_SAGA_SCOPE`: Trip Saga/Outbox unresolved age is A-owned and intentionally not inspected here; A's AUD-08 PASS covers the Trip authority.

### `negative-false-cleanup` / AUD-04 — FAIL
- `TERMINAL_TRIP_WITH_ACTIVE_RESERVATION`: Terminal Trip still holds a live Driver reservation without a durable cleanup obligation. Record: 0080cc54-8f7e-47a1-8f08-1f1d8106b396.
- `DRIVER_POINTS_AT_TERMINAL_TRIP`: Driver current reservation/trip points at a terminal Trip without a fresh recognized cleanup. Record: 88236a3a-cf38-48d7-b266-838416d7374a/0080cc54-8f7e-47a1-8f08-1f1d8106b396.

### `negative-false-cleanup` / AUD-07 — FAIL
- `LEAKED_RESERVATION`: Terminal Trip still holds an active Driver reservation with no open cleanup obligation. Record: 0080cc54-8f7e-47a1-8f08-1f1d8106b396/88236a3a-cf38-48d7-b266-838416d7374a.

### `negative-false-cleanup` / AUD-08 — PASS
- `TRIP_SAGA_SCOPE`: Trip Saga/Outbox unresolved age is A-owned and intentionally not inspected here; A's AUD-08 PASS covers the Trip authority.

### `negative-missing-intent` / AUD-06 — FAIL
- `MISSING_OR_MISMATCHED_FAILURE_INTENT`: NO_DRIVER result identity has no matching MatchingFailedV1 Outbox intent. Record: bc3cc300-dda0-44ae-8b06-d7cdfd351040/1.

### `negative-missing-intent` / AUD-08 — PASS
- `TRIP_SAGA_SCOPE`: Trip Saga/Outbox unresolved age is A-owned and intentionally not inspected here; A's AUD-08 PASS covers the Trip authority.

### `negative-stuck-cleanup` / AUD-04 — FAIL
- `STUCK_CLEANUP_OBLIGATION`: Recognized cleanup obligation exceeded the accepted age while the Driver stayed reserved. Record: d95c7be7-95f2-4fbc-88c6-dc4a95394719.
- `DRIVER_POINTS_AT_TERMINAL_TRIP`: Driver current reservation/trip points at a terminal Trip without a fresh recognized cleanup. Record: bf3e4731-ea76-4e9f-86b9-ad604677accd/d95c7be7-95f2-4fbc-88c6-dc4a95394719.

### `negative-stuck-cleanup` / AUD-07 — FAIL
- `STUCK_CLEANUP_OBLIGATION`: Cleanup remains open beyond the accepted age while the Driver is still reserved. Record: d95c7be7-95f2-4fbc-88c6-dc4a95394719/bf3e4731-ea76-4e9f-86b9-ad604677accd.

### `negative-stuck-cleanup` / AUD-08 — PASS
- `TRIP_SAGA_SCOPE`: Trip Saga/Outbox unresolved age is A-owned and intentionally not inspected here; A's AUD-08 PASS covers the Trip authority.

### `negative-stuck-unresolved` / AUD-08 — FAIL
- `STUCK_PENDING_OUTBOX`: driver Outbox row stayed PENDING for 120.0 min (policy 15 min). Record: stuck-driver-intent/DriverReservedV1.
- `STUCK_UNRESOLVED_ATTEMPT`: Matching attempt stayed UNKNOWN for 120.0 min (policy 15 min). Record: f0ef0f14-e01a-4820-8a4e-741bcbd85a63/1/0.
- `STUCK_PROCESSING_ROUND`: Matching round stayed PROCESSING for 120.0 min (policy 15 min). Record: f0ef0f14-e01a-4820-8a4e-741bcbd85a63/1.
- `TRIP_SAGA_SCOPE`: Trip Saga/Outbox unresolved age is A-owned and intentionally not inspected here; A's AUD-08 PASS covers the Trip authority.

### `no-driver-terminal` / AUD-08 — PASS
- `TRIP_SAGA_SCOPE`: Trip Saga/Outbox unresolved age is A-owned and intentionally not inspected here; A's AUD-08 PASS covers the Trip authority.

### `recognized-pending-cleanup` / AUD-04 — PASS
- `RECOGNIZED_PENDING_CLEANUP`: Cleanup is durably recognized and still within policy; not counted as a leak. Record: f7f70ff3-5689-4a1a-a406-12bf418b47d8.

### `recognized-pending-cleanup` / AUD-07 — PASS
- `RECOGNIZED_PENDING_CLEANUP`: Cleanup is durably recognized and still within policy; not counted as a leak. Record: f7f70ff3-5689-4a1a-a406-12bf418b47d8/1965eafa-0412-45bf-a7f0-3cdaf2defe45.

### `recognized-pending-cleanup` / AUD-08 — PASS
- `TRIP_SAGA_SCOPE`: Trip Saga/Outbox unresolved age is A-owned and intentionally not inspected here; A's AUD-08 PASS covers the Trip authority.

### `reserved-agreement` / AUD-08 — PASS
- `TRIP_SAGA_SCOPE`: Trip Saga/Outbox unresolved age is A-owned and intentionally not inspected here; A's AUD-08 PASS covers the Trip authority.

