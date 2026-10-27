# B-W12-08 authoritative invariant auditor — results

- Source commit: `3f7aa18d26739085b4b8d82b06290233cf76a40f`
- Source tree: `208b4e74e4fca0d1d7dd4c883bff96c9a2a3fae1`
- Generated: `2026-10-27T04:44:32.1302919+00:00`
- Checks (all runs): PASS 56, LOCAL_PROXY_PASS 0, FAIL 11, NOT RUN 57, BLOCKED 4
- Positive scenarios only: PASS 31, FAIL 0, NOT RUN 17, BLOCKED 0
- Negative/control runs: FAIL 11 (expected detections), BLOCKED 4 (expected honesty), PASS 25, NOT RUN 40

Statuses are final per check; `LOCAL_PROXY_PASS` is supporting evidence only
and never a full PASS for the distributed invariant. `NOT RUN` records an
owner dependency; `BLOCKED` means the required authority was unreachable.

## Run `B12-AUD-cancel-release-quiesced-20261027T044419Z-4bc268` — scenario `cancel-release-quiesced`

Observation: `POST_SCENARIO_B_QUIESCED_NO_RELAY`; elapsed 34 ms.

| Check | Status | Authority | Inspected | Finding codes | Limitation |
| --- | --- | --- | ---: | --- | --- |
| AUD-01 — Exclusive Driver reservation: at most one current active reservation/trip per Driver | **PASS** | driver_db | 9 | — | — |
| AUD-02 — Driver state coherence and lifecycle provenance | **PASS** | driver_db | 7 | — | Historical lifecycle_effects rows are never treated as current reservations; only the drivers row is active authority. |
| AUD-03 — One current Trip assignment | **NOT RUN** | trip_db (A-owned) | 0 | — | Trip assignment authority is A-owned (TripInvariantAuditor, PR #43). B executes AUD-04 against real Trip rows but does not duplicate the Trip-only audit; A's evidence is bound to a different source SHA/scenario set, so it is not reused as B's PASS. |
| AUD-04 — Driver–Trip exact reservation identity agreement | **PASS** | driver_db + trip_db | 5 | — | Driver is authoritative for reservation identity; Trip is authoritative for assignment; agreement is evaluated from both final states. |
| AUD-05 — Payment logical effect uniqueness | **NOT RUN** | payment_db (C-owned) | 0 | — | Payment authority is C-owned; no Payment database is supplied to the B audit. Explicit C dependency: payment effect ledger inspection. |
| AUD-06 — Matching terminal uniqueness and durable result intent | **PASS** | matching_db + driver_db | 11 | — | — |
| AUD-07 — No leaked pre-start Driver reservation after quiescence | **PASS** | trip_db + driver_db | 5 | — | Only the current drivers row is reservation authority; historical lifecycle effects are not leaks. Fresh recognized cleanup is accepted only in mid-flight observation; post-scenario modes require completed release obligations. |
| AUD-08 — Outbox/attempt unresolved state and age | **PASS** | driver_db + matching_db | 4 | `TRIP_SAGA_SCOPE` | Relay not executed for this observation; fresh PENDING <= 15 min accepted by the age policy, unresolved attempts/rounds <= 15 min, FAILED_VISIBLE is visible-by-design and reported for review. |

## Run `B12-AUD-control-empty-dataset-20261027T044417Z-66558e` — scenario `control-empty-dataset`

Observation: `EMPTY_DATASET_CONTROL`; elapsed 36 ms.

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

## Run `B12-AUD-control-unreachable-trip-20261027T044422Z-0ddf83` — scenario `control-unreachable-trip`

Observation: `NEGATIVE_CONTROL_SEEDED`; elapsed 8047 ms.

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

## Run `B12-AUD-driver-lifecycle-20261027T044431Z-e86245` — scenario `driver-lifecycle`

Observation: `POST_SCENARIO_B_QUIESCED_NO_RELAY`; elapsed 8 ms.

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

## Run `B12-AUD-late-release-fenced-20261027T044415Z-903c9b` — scenario `late-release-fenced`

Observation: `POST_SCENARIO_B_QUIESCED_NO_RELAY`; elapsed 92 ms.

| Check | Status | Authority | Inspected | Finding codes | Limitation |
| --- | --- | --- | ---: | --- | --- |
| AUD-01 — Exclusive Driver reservation: at most one current active reservation/trip per Driver | **PASS** | driver_db | 9 | — | — |
| AUD-02 — Driver state coherence and lifecycle provenance | **PASS** | driver_db | 7 | — | Historical lifecycle_effects rows are never treated as current reservations; only the drivers row is active authority. |
| AUD-03 — One current Trip assignment | **NOT RUN** | trip_db (A-owned) | 0 | — | Trip assignment authority is A-owned (TripInvariantAuditor, PR #43). B executes AUD-04 against real Trip rows but does not duplicate the Trip-only audit; A's evidence is bound to a different source SHA/scenario set, so it is not reused as B's PASS. |
| AUD-04 — Driver–Trip exact reservation identity agreement | **PASS** | driver_db + trip_db | 6 | — | Driver is authoritative for reservation identity; Trip is authoritative for assignment; agreement is evaluated from both final states. |
| AUD-05 — Payment logical effect uniqueness | **NOT RUN** | payment_db (C-owned) | 0 | — | Payment authority is C-owned; no Payment database is supplied to the B audit. Explicit C dependency: payment effect ledger inspection. |
| AUD-06 — Matching terminal uniqueness and durable result intent | **PASS** | matching_db + driver_db | 14 | — | — |
| AUD-07 — No leaked pre-start Driver reservation after quiescence | **PASS** | trip_db + driver_db | 5 | — | Only the current drivers row is reservation authority; historical lifecycle effects are not leaks. Fresh recognized cleanup is accepted only in mid-flight observation; post-scenario modes require completed release obligations. |
| AUD-08 — Outbox/attempt unresolved state and age | **PASS** | driver_db + matching_db | 7 | `TRIP_SAGA_SCOPE` | Relay not executed for this observation; fresh PENDING <= 15 min accepted by the age policy, unresolved attempts/rounds <= 15 min, FAILED_VISIBLE is visible-by-design and reported for review. |

## Run `B12-AUD-negative-contradictory-terminal-20261027T044431Z-8e0ec6` — scenario `negative-contradictory-terminal`

Observation: `NEGATIVE_CONTROL_SEEDED`; elapsed 34 ms.

| Check | Status | Authority | Inspected | Finding codes | Limitation |
| --- | --- | --- | ---: | --- | --- |
| AUD-01 — Exclusive Driver reservation: at most one current active reservation/trip per Driver | **PASS** | driver_db | 9 | — | — |
| AUD-02 — Driver state coherence and lifecycle provenance | **PASS** | driver_db | 7 | — | Historical lifecycle_effects rows are never treated as current reservations; only the drivers row is active authority. |
| AUD-03 — One current Trip assignment | **NOT RUN** | trip_db (A-owned) | 0 | — | Trip assignment authority is A-owned (TripInvariantAuditor, PR #43). B executes AUD-04 against real Trip rows but does not duplicate the Trip-only audit; A's evidence is bound to a different source SHA/scenario set, so it is not reused as B's PASS. |
| AUD-04 — Driver–Trip exact reservation identity agreement | **PASS** | driver_db + trip_db | 2 | — | Driver is authoritative for reservation identity; Trip is authoritative for assignment; agreement is evaluated from both final states. |
| AUD-05 — Payment logical effect uniqueness | **NOT RUN** | payment_db (C-owned) | 0 | — | Payment authority is C-owned; no Payment database is supplied to the B audit. Explicit C dependency: payment effect ledger inspection. |
| AUD-06 — Matching terminal uniqueness and durable result intent | **FAIL** | matching_db + driver_db | 11 | `MULTIPLE_RESERVED_ATTEMPTS` | — |
| AUD-07 — No leaked pre-start Driver reservation after quiescence | **NOT RUN** | trip_db + driver_db | 1 | — | No terminal Trip in this scenario; leak PASS would be vacuous. |
| AUD-08 — Outbox/attempt unresolved state and age | **PASS** | driver_db + matching_db | 4 | `TRIP_SAGA_SCOPE` | Relay not executed for this observation; fresh PENDING <= 15 min accepted by the age policy, unresolved attempts/rounds <= 15 min, FAILED_VISIBLE is visible-by-design and reported for review. |

## Run `B12-AUD-negative-disagreement-20261027T044421Z-ed1bc9` — scenario `negative-disagreement`

Observation: `NEGATIVE_CONTROL_SEEDED`; elapsed 34 ms.

| Check | Status | Authority | Inspected | Finding codes | Limitation |
| --- | --- | --- | ---: | --- | --- |
| AUD-01 — Exclusive Driver reservation: at most one current active reservation/trip per Driver | **PASS** | driver_db | 9 | — | — |
| AUD-02 — Driver state coherence and lifecycle provenance | **PASS** | driver_db | 7 | — | Historical lifecycle_effects rows are never treated as current reservations; only the drivers row is active authority. |
| AUD-03 — One current Trip assignment | **NOT RUN** | trip_db (A-owned) | 0 | — | Trip assignment authority is A-owned (TripInvariantAuditor, PR #43). B executes AUD-04 against real Trip rows but does not duplicate the Trip-only audit; A's evidence is bound to a different source SHA/scenario set, so it is not reused as B's PASS. |
| AUD-04 — Driver–Trip exact reservation identity agreement | **FAIL** | driver_db + trip_db | 2 | `DRIVER_TRIP_DISAGREEMENT`, `DRIVER_TRIP_DISAGREEMENT` | Driver is authoritative for reservation identity; Trip is authoritative for assignment; agreement is evaluated from both final states. |
| AUD-05 — Payment logical effect uniqueness | **NOT RUN** | payment_db (C-owned) | 0 | — | Payment authority is C-owned; no Payment database is supplied to the B audit. Explicit C dependency: payment effect ledger inspection. |
| AUD-06 — Matching terminal uniqueness and durable result intent | **PASS** | matching_db + driver_db | 10 | — | — |
| AUD-07 — No leaked pre-start Driver reservation after quiescence | **NOT RUN** | trip_db + driver_db | 1 | — | No terminal Trip in this scenario; leak PASS would be vacuous. |
| AUD-08 — Outbox/attempt unresolved state and age | **PASS** | driver_db + matching_db | 3 | `TRIP_SAGA_SCOPE` | Relay not executed for this observation; fresh PENDING <= 15 min accepted by the age policy, unresolved attempts/rounds <= 15 min, FAILED_VISIBLE is visible-by-design and reported for review. |

## Run `B12-AUD-negative-false-cleanup-20261027T044418Z-a70a6f` — scenario `negative-false-cleanup`

Observation: `NEGATIVE_CONTROL_SEEDED`; elapsed 37 ms.

| Check | Status | Authority | Inspected | Finding codes | Limitation |
| --- | --- | --- | ---: | --- | --- |
| AUD-01 — Exclusive Driver reservation: at most one current active reservation/trip per Driver | **PASS** | driver_db | 9 | — | — |
| AUD-02 — Driver state coherence and lifecycle provenance | **PASS** | driver_db | 7 | — | Historical lifecycle_effects rows are never treated as current reservations; only the drivers row is active authority. |
| AUD-03 — One current Trip assignment | **NOT RUN** | trip_db (A-owned) | 0 | — | Trip assignment authority is A-owned (TripInvariantAuditor, PR #43). B executes AUD-04 against real Trip rows but does not duplicate the Trip-only audit; A's evidence is bound to a different source SHA/scenario set, so it is not reused as B's PASS. |
| AUD-04 — Driver–Trip exact reservation identity agreement | **FAIL** | driver_db + trip_db | 5 | `TERMINAL_TRIP_WITH_ACTIVE_RESERVATION`, `DRIVER_POINTS_AT_TERMINAL_TRIP` | Driver is authoritative for reservation identity; Trip is authoritative for assignment; agreement is evaluated from both final states. |
| AUD-05 — Payment logical effect uniqueness | **NOT RUN** | payment_db (C-owned) | 0 | — | Payment authority is C-owned; no Payment database is supplied to the B audit. Explicit C dependency: payment effect ledger inspection. |
| AUD-06 — Matching terminal uniqueness and durable result intent | **PASS** | matching_db + driver_db | 10 | — | — |
| AUD-07 — No leaked pre-start Driver reservation after quiescence | **FAIL** | trip_db + driver_db | 5 | `LEAKED_RESERVATION` | Only the current drivers row is reservation authority; historical lifecycle effects are not leaks. Fresh recognized cleanup is accepted only in mid-flight observation; post-scenario modes require completed release obligations. |
| AUD-08 — Outbox/attempt unresolved state and age | **PASS** | driver_db + matching_db | 3 | `TRIP_SAGA_SCOPE` | Relay not executed for this observation; fresh PENDING <= 15 min accepted by the age policy, unresolved attempts/rounds <= 15 min, FAILED_VISIBLE is visible-by-design and reported for review. |

## Run `B12-AUD-negative-fresh-cleanup-quiesced-20261027T044418Z-83c89f` — scenario `negative-fresh-cleanup-quiesced`

Observation: `POST_SCENARIO_B_QUIESCED_NO_RELAY`; elapsed 44 ms.

| Check | Status | Authority | Inspected | Finding codes | Limitation |
| --- | --- | --- | ---: | --- | --- |
| AUD-01 — Exclusive Driver reservation: at most one current active reservation/trip per Driver | **PASS** | driver_db | 9 | — | — |
| AUD-02 — Driver state coherence and lifecycle provenance | **PASS** | driver_db | 7 | — | Historical lifecycle_effects rows are never treated as current reservations; only the drivers row is active authority. |
| AUD-03 — One current Trip assignment | **NOT RUN** | trip_db (A-owned) | 0 | — | Trip assignment authority is A-owned (TripInvariantAuditor, PR #43). B executes AUD-04 against real Trip rows but does not duplicate the Trip-only audit; A's evidence is bound to a different source SHA/scenario set, so it is not reused as B's PASS. |
| AUD-04 — Driver–Trip exact reservation identity agreement | **FAIL** | driver_db + trip_db | 5 | `UNRESOLVED_CLEANUP_AT_QUIESCENCE`, `UNRESOLVED_CLEANUP_AT_QUIESCENCE` | Driver is authoritative for reservation identity; Trip is authoritative for assignment; agreement is evaluated from both final states. |
| AUD-05 — Payment logical effect uniqueness | **NOT RUN** | payment_db (C-owned) | 0 | — | Payment authority is C-owned; no Payment database is supplied to the B audit. Explicit C dependency: payment effect ledger inspection. |
| AUD-06 — Matching terminal uniqueness and durable result intent | **PASS** | matching_db + driver_db | 10 | — | — |
| AUD-07 — No leaked pre-start Driver reservation after quiescence | **FAIL** | trip_db + driver_db | 5 | `UNRESOLVED_CLEANUP_AT_QUIESCENCE` | Only the current drivers row is reservation authority; historical lifecycle effects are not leaks. Fresh recognized cleanup is accepted only in mid-flight observation; post-scenario modes require completed release obligations. |
| AUD-08 — Outbox/attempt unresolved state and age | **PASS** | driver_db + matching_db | 3 | `TRIP_SAGA_SCOPE` | Relay not executed for this observation; fresh PENDING <= 15 min accepted by the age policy, unresolved attempts/rounds <= 15 min, FAILED_VISIBLE is visible-by-design and reported for review. |

## Run `B12-AUD-negative-fresh-pending-relay-quiesced-20261027T044432Z-3755ef` — scenario `negative-fresh-pending-relay-quiesced`

Observation: `POST_SCENARIO_RELAY_QUIESCED`; elapsed 36 ms.

| Check | Status | Authority | Inspected | Finding codes | Limitation |
| --- | --- | --- | ---: | --- | --- |
| AUD-01 — Exclusive Driver reservation: at most one current active reservation/trip per Driver | **NOT RUN** | driver_db | 8 | — | Empty Driver dataset; PASS would be vacuous. |
| AUD-02 — Driver state coherence and lifecycle provenance | **NOT RUN** | driver_db | 6 | — | Empty Driver dataset; PASS would be vacuous. |
| AUD-03 — One current Trip assignment | **NOT RUN** | trip_db (A-owned) | 0 | — | Trip assignment authority is A-owned (TripInvariantAuditor, PR #43). B executes AUD-04 against real Trip rows but does not duplicate the Trip-only audit; A's evidence is bound to a different source SHA/scenario set, so it is not reused as B's PASS. |
| AUD-04 — Driver–Trip exact reservation identity agreement | **NOT RUN** | driver_db + trip_db | 0 | — | Empty Trip or Driver dataset; PASS would be vacuous. |
| AUD-05 — Payment logical effect uniqueness | **NOT RUN** | payment_db (C-owned) | 0 | — | Payment authority is C-owned; no Payment database is supplied to the B audit. Explicit C dependency: payment effect ledger inspection. |
| AUD-06 — Matching terminal uniqueness and durable result intent | **NOT RUN** | matching_db + driver_db | 8 | — | Empty Matching terminal dataset; PASS would be vacuous. |
| AUD-07 — No leaked pre-start Driver reservation after quiescence | **NOT RUN** | trip_db + driver_db | 0 | — | No terminal Trip in this scenario; leak PASS would be vacuous. |
| AUD-08 — Outbox/attempt unresolved state and age | **FAIL** | driver_db + matching_db | 1 | `PENDING_OUTBOX_AT_QUIESCENCE`, `TRIP_SAGA_SCOPE` | Relay-quiesced policy: any PENDING Outbox row is a violation; unresolved attempts/rounds <= 15 min; FAILED_VISIBLE is visible-by-design and reported for review. |

## Run `B12-AUD-negative-missing-intent-20261027T044431Z-34331a` — scenario `negative-missing-intent`

Observation: `NEGATIVE_CONTROL_SEEDED`; elapsed 32 ms.

| Check | Status | Authority | Inspected | Finding codes | Limitation |
| --- | --- | --- | ---: | --- | --- |
| AUD-01 — Exclusive Driver reservation: at most one current active reservation/trip per Driver | **PASS** | driver_db | 10 | — | — |
| AUD-02 — Driver state coherence and lifecycle provenance | **PASS** | driver_db | 8 | — | Historical lifecycle_effects rows are never treated as current reservations; only the drivers row is active authority. |
| AUD-03 — One current Trip assignment | **NOT RUN** | trip_db (A-owned) | 0 | — | Trip assignment authority is A-owned (TripInvariantAuditor, PR #43). B executes AUD-04 against real Trip rows but does not duplicate the Trip-only audit; A's evidence is bound to a different source SHA/scenario set, so it is not reused as B's PASS. |
| AUD-04 — Driver–Trip exact reservation identity agreement | **PASS** | driver_db + trip_db | 3 | — | Driver is authoritative for reservation identity; Trip is authoritative for assignment; agreement is evaluated from both final states. |
| AUD-05 — Payment logical effect uniqueness | **NOT RUN** | payment_db (C-owned) | 0 | — | Payment authority is C-owned; no Payment database is supplied to the B audit. Explicit C dependency: payment effect ledger inspection. |
| AUD-06 — Matching terminal uniqueness and durable result intent | **FAIL** | matching_db + driver_db | 10 | `MISSING_OR_MISMATCHED_FAILURE_INTENT` | — |
| AUD-07 — No leaked pre-start Driver reservation after quiescence | **NOT RUN** | trip_db + driver_db | 2 | — | No terminal Trip in this scenario; leak PASS would be vacuous. |
| AUD-08 — Outbox/attempt unresolved state and age | **PASS** | driver_db + matching_db | 3 | `TRIP_SAGA_SCOPE` | Relay not executed for this observation; fresh PENDING <= 15 min accepted by the age policy, unresolved attempts/rounds <= 15 min, FAILED_VISIBLE is visible-by-design and reported for review. |

## Run `B12-AUD-negative-stuck-cleanup-20261027T044422Z-450ee7` — scenario `negative-stuck-cleanup`

Observation: `NEGATIVE_CONTROL_SEEDED`; elapsed 34 ms.

| Check | Status | Authority | Inspected | Finding codes | Limitation |
| --- | --- | --- | ---: | --- | --- |
| AUD-01 — Exclusive Driver reservation: at most one current active reservation/trip per Driver | **PASS** | driver_db | 9 | — | — |
| AUD-02 — Driver state coherence and lifecycle provenance | **PASS** | driver_db | 7 | — | Historical lifecycle_effects rows are never treated as current reservations; only the drivers row is active authority. |
| AUD-03 — One current Trip assignment | **NOT RUN** | trip_db (A-owned) | 0 | — | Trip assignment authority is A-owned (TripInvariantAuditor, PR #43). B executes AUD-04 against real Trip rows but does not duplicate the Trip-only audit; A's evidence is bound to a different source SHA/scenario set, so it is not reused as B's PASS. |
| AUD-04 — Driver–Trip exact reservation identity agreement | **FAIL** | driver_db + trip_db | 5 | `STUCK_CLEANUP_OBLIGATION`, `DRIVER_POINTS_AT_TERMINAL_TRIP` | Driver is authoritative for reservation identity; Trip is authoritative for assignment; agreement is evaluated from both final states. |
| AUD-05 — Payment logical effect uniqueness | **NOT RUN** | payment_db (C-owned) | 0 | — | Payment authority is C-owned; no Payment database is supplied to the B audit. Explicit C dependency: payment effect ledger inspection. |
| AUD-06 — Matching terminal uniqueness and durable result intent | **PASS** | matching_db + driver_db | 10 | — | — |
| AUD-07 — No leaked pre-start Driver reservation after quiescence | **FAIL** | trip_db + driver_db | 5 | `STUCK_CLEANUP_OBLIGATION` | Only the current drivers row is reservation authority; historical lifecycle effects are not leaks. Fresh recognized cleanup is accepted only in mid-flight observation; post-scenario modes require completed release obligations. |
| AUD-08 — Outbox/attempt unresolved state and age | **PASS** | driver_db + matching_db | 3 | `TRIP_SAGA_SCOPE` | Relay not executed for this observation; fresh PENDING <= 15 min accepted by the age policy, unresolved attempts/rounds <= 15 min, FAILED_VISIBLE is visible-by-design and reported for review. |

## Run `B12-AUD-negative-stuck-unresolved-20261027T044417Z-815358` — scenario `negative-stuck-unresolved`

Observation: `NEGATIVE_CONTROL_SEEDED`; elapsed 35 ms.

| Check | Status | Authority | Inspected | Finding codes | Limitation |
| --- | --- | --- | ---: | --- | --- |
| AUD-01 — Exclusive Driver reservation: at most one current active reservation/trip per Driver | **NOT RUN** | driver_db | 8 | — | Empty Driver dataset; PASS would be vacuous. |
| AUD-02 — Driver state coherence and lifecycle provenance | **NOT RUN** | driver_db | 6 | — | Empty Driver dataset; PASS would be vacuous. |
| AUD-03 — One current Trip assignment | **NOT RUN** | trip_db (A-owned) | 0 | — | Trip assignment authority is A-owned (TripInvariantAuditor, PR #43). B executes AUD-04 against real Trip rows but does not duplicate the Trip-only audit; A's evidence is bound to a different source SHA/scenario set, so it is not reused as B's PASS. |
| AUD-04 — Driver–Trip exact reservation identity agreement | **NOT RUN** | driver_db + trip_db | 0 | — | Empty Trip or Driver dataset; PASS would be vacuous. |
| AUD-05 — Payment logical effect uniqueness | **NOT RUN** | payment_db (C-owned) | 0 | — | Payment authority is C-owned; no Payment database is supplied to the B audit. Explicit C dependency: payment effect ledger inspection. |
| AUD-06 — Matching terminal uniqueness and durable result intent | **PASS** | matching_db + driver_db | 10 | — | — |
| AUD-07 — No leaked pre-start Driver reservation after quiescence | **NOT RUN** | trip_db + driver_db | 0 | — | No terminal Trip in this scenario; leak PASS would be vacuous. |
| AUD-08 — Outbox/attempt unresolved state and age | **FAIL** | driver_db + matching_db | 3 | `STUCK_PENDING_OUTBOX`, `STUCK_UNRESOLVED_ATTEMPT`, `STUCK_PROCESSING_ROUND`, `TRIP_SAGA_SCOPE` | Relay not executed for this observation; fresh PENDING <= 15 min accepted by the age policy, unresolved attempts/rounds <= 15 min, FAILED_VISIBLE is visible-by-design and reported for review. |

## Run `B12-AUD-no-driver-terminal-20261027T044420Z-2d6db5` — scenario `no-driver-terminal`

Observation: `POST_SCENARIO_B_QUIESCED_NO_RELAY`; elapsed 36 ms.

| Check | Status | Authority | Inspected | Finding codes | Limitation |
| --- | --- | --- | ---: | --- | --- |
| AUD-01 — Exclusive Driver reservation: at most one current active reservation/trip per Driver | **PASS** | driver_db | 10 | — | — |
| AUD-02 — Driver state coherence and lifecycle provenance | **PASS** | driver_db | 8 | — | Historical lifecycle_effects rows are never treated as current reservations; only the drivers row is active authority. |
| AUD-03 — One current Trip assignment | **NOT RUN** | trip_db (A-owned) | 0 | — | Trip assignment authority is A-owned (TripInvariantAuditor, PR #43). B executes AUD-04 against real Trip rows but does not duplicate the Trip-only audit; A's evidence is bound to a different source SHA/scenario set, so it is not reused as B's PASS. |
| AUD-04 — Driver–Trip exact reservation identity agreement | **PASS** | driver_db + trip_db | 3 | — | Driver is authoritative for reservation identity; Trip is authoritative for assignment; agreement is evaluated from both final states. |
| AUD-05 — Payment logical effect uniqueness | **NOT RUN** | payment_db (C-owned) | 0 | — | Payment authority is C-owned; no Payment database is supplied to the B audit. Explicit C dependency: payment effect ledger inspection. |
| AUD-06 — Matching terminal uniqueness and durable result intent | **PASS** | matching_db + driver_db | 11 | — | — |
| AUD-07 — No leaked pre-start Driver reservation after quiescence | **PASS** | trip_db + driver_db | 3 | — | Only the current drivers row is reservation authority; historical lifecycle effects are not leaks. Fresh recognized cleanup is accepted only in mid-flight observation; post-scenario modes require completed release obligations. |
| AUD-08 — Outbox/attempt unresolved state and age | **PASS** | driver_db + matching_db | 4 | `TRIP_SAGA_SCOPE` | Relay not executed for this observation; fresh PENDING <= 15 min accepted by the age policy, unresolved attempts/rounds <= 15 min, FAILED_VISIBLE is visible-by-design and reported for review. |

## Run `B12-AUD-recognized-pending-cleanup-20261027T044419Z-2b7771` — scenario `recognized-pending-cleanup`

Observation: `MID_FLIGHT_OBSERVATION`; elapsed 63 ms.

| Check | Status | Authority | Inspected | Finding codes | Limitation |
| --- | --- | --- | ---: | --- | --- |
| AUD-01 — Exclusive Driver reservation: at most one current active reservation/trip per Driver | **PASS** | driver_db | 9 | — | — |
| AUD-02 — Driver state coherence and lifecycle provenance | **PASS** | driver_db | 7 | — | Historical lifecycle_effects rows are never treated as current reservations; only the drivers row is active authority. |
| AUD-03 — One current Trip assignment | **NOT RUN** | trip_db (A-owned) | 0 | — | Trip assignment authority is A-owned (TripInvariantAuditor, PR #43). B executes AUD-04 against real Trip rows but does not duplicate the Trip-only audit; A's evidence is bound to a different source SHA/scenario set, so it is not reused as B's PASS. |
| AUD-04 — Driver–Trip exact reservation identity agreement | **PASS** | driver_db + trip_db | 5 | `RECOGNIZED_PENDING_CLEANUP` | Driver is authoritative for reservation identity; Trip is authoritative for assignment; agreement is evaluated from both final states. |
| AUD-05 — Payment logical effect uniqueness | **NOT RUN** | payment_db (C-owned) | 0 | — | Payment authority is C-owned; no Payment database is supplied to the B audit. Explicit C dependency: payment effect ledger inspection. |
| AUD-06 — Matching terminal uniqueness and durable result intent | **PASS** | matching_db + driver_db | 10 | — | — |
| AUD-07 — No leaked pre-start Driver reservation after quiescence | **PASS** | trip_db + driver_db | 5 | `RECOGNIZED_PENDING_CLEANUP` | Only the current drivers row is reservation authority; historical lifecycle effects are not leaks. Fresh recognized cleanup is accepted only in mid-flight observation; post-scenario modes require completed release obligations. |
| AUD-08 — Outbox/attempt unresolved state and age | **PASS** | driver_db + matching_db | 3 | `TRIP_SAGA_SCOPE` | Relay not executed for this observation; fresh PENDING <= 15 min accepted by the age policy, unresolved attempts/rounds <= 15 min, FAILED_VISIBLE is visible-by-design and reported for review. |

## Run `B12-AUD-reserved-agreement-20261027T044416Z-f2e4ba` — scenario `reserved-agreement`

Observation: `POST_SCENARIO_B_QUIESCED_NO_RELAY`; elapsed 35 ms.

| Check | Status | Authority | Inspected | Finding codes | Limitation |
| --- | --- | --- | ---: | --- | --- |
| AUD-01 — Exclusive Driver reservation: at most one current active reservation/trip per Driver | **PASS** | driver_db | 9 | — | — |
| AUD-02 — Driver state coherence and lifecycle provenance | **PASS** | driver_db | 7 | — | Historical lifecycle_effects rows are never treated as current reservations; only the drivers row is active authority. |
| AUD-03 — One current Trip assignment | **NOT RUN** | trip_db (A-owned) | 0 | — | Trip assignment authority is A-owned (TripInvariantAuditor, PR #43). B executes AUD-04 against real Trip rows but does not duplicate the Trip-only audit; A's evidence is bound to a different source SHA/scenario set, so it is not reused as B's PASS. |
| AUD-04 — Driver–Trip exact reservation identity agreement | **PASS** | driver_db + trip_db | 2 | — | Driver is authoritative for reservation identity; Trip is authoritative for assignment; agreement is evaluated from both final states. |
| AUD-05 — Payment logical effect uniqueness | **NOT RUN** | payment_db (C-owned) | 0 | — | Payment authority is C-owned; no Payment database is supplied to the B audit. Explicit C dependency: payment effect ledger inspection. |
| AUD-06 — Matching terminal uniqueness and durable result intent | **PASS** | matching_db + driver_db | 10 | — | — |
| AUD-07 — No leaked pre-start Driver reservation after quiescence | **NOT RUN** | trip_db + driver_db | 1 | — | No terminal Trip in this scenario; leak PASS would be vacuous. |
| AUD-08 — Outbox/attempt unresolved state and age | **PASS** | driver_db + matching_db | 3 | `TRIP_SAGA_SCOPE` | Relay not executed for this observation; fresh PENDING <= 15 min accepted by the age policy, unresolved attempts/rounds <= 15 min, FAILED_VISIBLE is visible-by-design and reported for review. |

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
- `MULTIPLE_RESERVED_ATTEMPTS`: RESERVED round has 2 RESERVED attempt rows; exactly one is required. Record: e8c87aa4-2729-4d5f-a9df-ccc9400a3ac2/1.

### `negative-contradictory-terminal` / AUD-08 — PASS
- `TRIP_SAGA_SCOPE`: Trip Saga/Outbox unresolved age is A-owned and intentionally not inspected here; A's AUD-08 PASS covers the Trip authority.

### `negative-disagreement` / AUD-04 — FAIL
- `DRIVER_TRIP_DISAGREEMENT`: Trip expects driver=1190e390-0a75-4f18-8ed5-7e0b81f32fad trip=5cbe20e2-3358-464c-a684-adec099fc98e reservation=4bc9d0c1-d580-48b9-99ec-2766e4f877c4 round=1; Driver row is status=RESERVED, reservedTrip=5cbe20e2-3358-464c-a684-adec099fc98e, reservation=8f4c81ec-2f7e-4a61-9737-4fbcde9fd4d4, round=1. Record: 5cbe20e2-3358-464c-a684-adec099fc98e.
- `DRIVER_TRIP_DISAGREEMENT`: Driver current reservation identity does not match the Trip assignment. Record: 1190e390-0a75-4f18-8ed5-7e0b81f32fad/5cbe20e2-3358-464c-a684-adec099fc98e.

### `negative-disagreement` / AUD-08 — PASS
- `TRIP_SAGA_SCOPE`: Trip Saga/Outbox unresolved age is A-owned and intentionally not inspected here; A's AUD-08 PASS covers the Trip authority.

### `negative-false-cleanup` / AUD-04 — FAIL
- `TERMINAL_TRIP_WITH_ACTIVE_RESERVATION`: Terminal Trip still holds a live Driver reservation without a durable cleanup obligation. Record: 3554bec4-45f8-4f67-876a-4bbc33294aed.
- `DRIVER_POINTS_AT_TERMINAL_TRIP`: Driver current reservation/trip points at a terminal Trip without a fresh recognized cleanup. Record: 3043c5a0-1e57-4b63-ade1-e9d177a0b326/3554bec4-45f8-4f67-876a-4bbc33294aed.

### `negative-false-cleanup` / AUD-07 — FAIL
- `LEAKED_RESERVATION`: Terminal Trip still holds an active Driver reservation with no open cleanup obligation. Record: 3554bec4-45f8-4f67-876a-4bbc33294aed/3043c5a0-1e57-4b63-ade1-e9d177a0b326.

### `negative-false-cleanup` / AUD-08 — PASS
- `TRIP_SAGA_SCOPE`: Trip Saga/Outbox unresolved age is A-owned and intentionally not inspected here; A's AUD-08 PASS covers the Trip authority.

### `negative-fresh-cleanup-quiesced` / AUD-04 — FAIL
- `UNRESOLVED_CLEANUP_AT_QUIESCENCE`: Business-quiesced observation still holds a Driver reservation with an open release obligation. Record: df084e7f-93a7-4632-8162-0c2f449780cf.
- `UNRESOLVED_CLEANUP_AT_QUIESCENCE`: Business-quiesced observation still points a Driver reservation at a terminal Trip with an open release obligation. Record: d97325f4-97be-4cb2-b2a3-5f687090d0f9/df084e7f-93a7-4632-8162-0c2f449780cf.

### `negative-fresh-cleanup-quiesced` / AUD-07 — FAIL
- `UNRESOLVED_CLEANUP_AT_QUIESCENCE`: Business-quiesced observation still holds a Driver reservation with an open release obligation. Record: df084e7f-93a7-4632-8162-0c2f449780cf/d97325f4-97be-4cb2-b2a3-5f687090d0f9.

### `negative-fresh-cleanup-quiesced` / AUD-08 — PASS
- `TRIP_SAGA_SCOPE`: Trip Saga/Outbox unresolved age is A-owned and intentionally not inspected here; A's AUD-08 PASS covers the Trip authority.

### `negative-fresh-pending-relay-quiesced` / AUD-08 — FAIL
- `PENDING_OUTBOX_AT_QUIESCENCE`: driver Outbox row is PENDING in a relay-quiesced observation (pending must be 0). Record: fresh-relay-pending/DriverReservedV1.
- `TRIP_SAGA_SCOPE`: Trip Saga/Outbox unresolved age is A-owned and intentionally not inspected here; A's AUD-08 PASS covers the Trip authority.

### `negative-missing-intent` / AUD-06 — FAIL
- `MISSING_OR_MISMATCHED_FAILURE_INTENT`: NO_DRIVER result identity has no matching MatchingFailedV1 Outbox intent. Record: 98a34c6c-b171-4c77-95c8-7fc71d5d93d7/1.

### `negative-missing-intent` / AUD-08 — PASS
- `TRIP_SAGA_SCOPE`: Trip Saga/Outbox unresolved age is A-owned and intentionally not inspected here; A's AUD-08 PASS covers the Trip authority.

### `negative-stuck-cleanup` / AUD-04 — FAIL
- `STUCK_CLEANUP_OBLIGATION`: Recognized cleanup obligation exceeded the accepted age while the Driver stayed reserved. Record: 3bd2d454-7bf0-49f5-8347-fdfd48a04b8c.
- `DRIVER_POINTS_AT_TERMINAL_TRIP`: Driver current reservation/trip points at a terminal Trip without a fresh recognized cleanup. Record: e97ae60c-e9c3-402a-b59f-1386632ef62a/3bd2d454-7bf0-49f5-8347-fdfd48a04b8c.

### `negative-stuck-cleanup` / AUD-07 — FAIL
- `STUCK_CLEANUP_OBLIGATION`: Cleanup remains open beyond the accepted age while the Driver is still reserved. Record: 3bd2d454-7bf0-49f5-8347-fdfd48a04b8c/e97ae60c-e9c3-402a-b59f-1386632ef62a.

### `negative-stuck-cleanup` / AUD-08 — PASS
- `TRIP_SAGA_SCOPE`: Trip Saga/Outbox unresolved age is A-owned and intentionally not inspected here; A's AUD-08 PASS covers the Trip authority.

### `negative-stuck-unresolved` / AUD-08 — FAIL
- `STUCK_PENDING_OUTBOX`: driver Outbox row stayed PENDING for 120.0 min (policy 15 min). Record: stuck-driver-intent/DriverReservedV1.
- `STUCK_UNRESOLVED_ATTEMPT`: Matching attempt stayed UNKNOWN for 120.0 min (policy 15 min). Record: a7409ada-9357-4cc4-bccb-853a4f4b9c17/1/0.
- `STUCK_PROCESSING_ROUND`: Matching round stayed PROCESSING for 120.0 min (policy 15 min). Record: a7409ada-9357-4cc4-bccb-853a4f4b9c17/1.
- `TRIP_SAGA_SCOPE`: Trip Saga/Outbox unresolved age is A-owned and intentionally not inspected here; A's AUD-08 PASS covers the Trip authority.

### `no-driver-terminal` / AUD-08 — PASS
- `TRIP_SAGA_SCOPE`: Trip Saga/Outbox unresolved age is A-owned and intentionally not inspected here; A's AUD-08 PASS covers the Trip authority.

### `recognized-pending-cleanup` / AUD-04 — PASS
- `RECOGNIZED_PENDING_CLEANUP`: Cleanup is durably recognized and still within policy; not counted as a leak in mid-flight observation. Record: 1f0ab03c-14ef-404b-ad63-934a89806afd.

### `recognized-pending-cleanup` / AUD-07 — PASS
- `RECOGNIZED_PENDING_CLEANUP`: Cleanup is durably recognized and still within policy; not counted as a leak in mid-flight observation. Record: 1f0ab03c-14ef-404b-ad63-934a89806afd/f741ef30-81b8-4d82-9e49-3e7c8153f782.

### `recognized-pending-cleanup` / AUD-08 — PASS
- `TRIP_SAGA_SCOPE`: Trip Saga/Outbox unresolved age is A-owned and intentionally not inspected here; A's AUD-08 PASS covers the Trip authority.

### `reserved-agreement` / AUD-08 — PASS
- `TRIP_SAGA_SCOPE`: Trip Saga/Outbox unresolved age is A-owned and intentionally not inspected here; A's AUD-08 PASS covers the Trip authority.

