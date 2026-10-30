> Historical verification: the current reservation-safety repair and final-source evidence supersede this bundle's readiness assessment. See [reservation-reconciliation safety](../w12-reconciliation-safety/README.md). Earlier measurements retain their original source binding.

# B-REC-01 / DSI-021 — Production Matching recovery and stage correctness

**Verdict: `B PRODUCTION MATCHING RECOVERY AND STAGES GREEN — SHARED G4 GATES TRACKED`**

This bundle records a forward repair on the B-W12 layer. It replaces the
B-owned findings `B-REC-01` (no autonomous reserve/reconcile trigger) and
`DSI-021` (persisted stage never left S0) from the W12 audit with executable
production-path proofs. It does not claim SEC-AUTH-01..04, A/C audit rows, or
overall G4 green.

| Item | Value |
|---|---|
| Repair base | `4aea3f51b2730f12b986e4f46815981460b6ebb7` (B-W12 head) |
| Base tree | `5902b6f38d5e448dd89a55398e5bd517e5045145` |
| Branch | `feature/B-W12-matching-recovery` |
| Historical W11 PR | #44 at `3ba3724e7e7df378e8c234af685c027ad9d26863` (untouched) |
| Historical W12 PR | #45 at `4aea3f5` (base for this repair) |
| Final repair head | `b4cf93c2e318b32fd191bd8ef9dae8030ec3335d` |
| Final repair tree | `109f8c7aee39259d68f309236706436c2e420e05` |

## 1. Canonical ledger reconstructed before editing

| Contract | Source | Repair obligation |
|---|---|---|
| DSI-015 one terminal per round | Doc 01 §8.5 | terminal only at the final stage; exact replay identity |
| DSI-016 current-round guard | Doc 01 §8.6 | unchanged A-owned Trip gate |
| DSI-017 unresolved blocks next candidate | Doc 01 §8.7 | worker reconciles unresolved attempts before any new target |
| DSI-018 attempt identity stability | Doc 01 §8.8 | one `reservationId` per logical attempt across retry/restart |
| DSI-019 deterministic ordering | Doc 01 §8.9 | frozen ranked stage snapshots |
| DSI-020 eligibility before ranking | Doc 01 §8.10 | unchanged typed eligibility |
| DSI-021 stage monotonicity | Doc 01 §9.3 | durable `current_stage`, no backward transition, restart reload |
| DSI-022 no advance on UNKNOWN | Doc 01 §9.4 | stage advance predicate blocks IN_FLIGHT/UNKNOWN |
| DSI-023 no fresh conclusive retry | Doc 01 §9.5 | later stages exclude every driver already attempted |
| DSI-031 no new Driver before UNKNOWN resolved | Doc 01 §12.2 | reconcile first; same-R query/replay |
| DSI-035 durable terminal | Doc 01 §12.6 | NO_DRIVER + Outbox in one transaction |
| DSL-001 reconciliation terminates | Doc 01 §13 | bounded polls, durable review marker, retry with backoff |
| DSL-003 round terminates | Doc 01 §13 | finite stage/candidate set + explicit unresolved state |
| Doc 02 §12.2–12.9 | durable round/attempt, write-ahead, reconcile, duplicate delivery, NO_DRIVER, cancellation | production worker + cancel consumer |
| Doc 01 §9.1 | stage radii/budgets are configuration | configured S0/S1/S2, not hard-coded correctness constants |
| 05B B-REC-01 | W12 audit finding | durable autonomous execution trigger |

## 2. Pre-repair runtime path and confirmed root causes

```text
RideRequestedV1
  → RideRequestedHandler
  → MatchingAttemptRepository.EnsureRoundAsync
  → ACK
  → (no production caller)
```

Confirmed at base `4aea3f5`:

1. `RideRequestedHandler` did not persist pickup; a later restart could not
   rediscover candidates from the broker payload.
2. No hosted service or scanner invoked `MatchingReserveOrchestrator`; only the
   Testing-only `MatchingTestEndpoints` could start reserve/reconcile work.
   The W11 REL-01/REL-02 proofs were therefore harness-triggered.
3. `MatchingRound.CurrentStage` was hard-coded S0 in the aggregate; no SQL wrote
   `current_stage`; no attempt row carried a stage; the policy had one radius.
4. No `CancelMatchingRoundV1` consumer existed, so a Trip cancellation could
   not stop new attempts or reconcile an unresolved effect.

A second defect was exposed by the new proof: stage snapshots create READY
attempt rows, but the orchestrator only inserted IN_FLIGHT on a first attempt and
therefore reserved a Driver while the ledger still said READY. The repair
transitions READY→IN_FLIGHT durably before dispatch and fails closed if the
transition does not persist.

A third defect was exposed by the stage proof: non-final stages passed a
`null` terminal constraint, allowing a premature `NO_DRIVER` at S0. Every worker
execution now passes the final configured stage to the terminalizer; S0/S1
refuse early termination.

## 3. Chosen architecture

### Durable intake before ACK

`RideRequestedHandler` parses the canonical A payload (`tripId`,
`matchingRound`, `pickup`) and commits, in one transaction:

```text
matching_rounds(trip_id, matching_round)
  + pickup_longitude/latitude
  + auto_execute = TRUE
  + input_message_id (envelope identity verbatim)
  + cancel_requested from a pending durable cancel tombstone
```

The same transaction uses the cancel advisory lock, so a cancel that overtakes
its ride request cannot be lost. ACK follows commit. A crash after ACK leaves a
discoverable row, not an in-memory queue entry.

### Autonomous worker

`MatchingWorkDispatcher` (hosted service) scans:

```text
PROCESSING AND auto_execute
AND (work_checked_at IS NULL OR work_checked_at <= now() - backoff)
```

Each due round is driven under a session-level `pg_try_advisory_lock` lease:
a crashed process releases it automatically; another instance can then recover
the same durable work. The worker performs, in order:

```text
cancel_requested? → query-only reconcile, then CANCELLED or authoritative RESERVED
unresolved IN_FLIGHT/UNKNOWN? → reconcile same reservationId, no new Driver
durable RESERVED? → converge the round mark
READY attempts? → READY→IN_FLIGHT fence then serial reserve
no stage work? → atomic stage snapshot + READY identities
stage exhausted? → durable S0→S1→S2 advance or final NO_DRIVER
```

Failures record `work_checked_at` + `work_error_code` and leave the round
PROCESSING. There is no tight retry loop, no sleep-as-oracle, and no
process-global ownership state.

### Cancellation

`CancelMatchingRoundV1` is consumed from the frozen `matching.commands.q`.
`MatchingWorkRepository` records a durable `(tripId, matchingRound)` tombstone,
flags an existing PROCESSING round, and the intake consumes the tombstone
atomically. Cancellation starts no new attempt; an IN_FLIGHT effect is
reconciled query-only with the same reservation; a committed reservation stays
authoritative and Trip owns exact cleanup (Doc 02 §12.9).

### Progressive stages

- `Matching:Stages` is configuration: production ships S0=5 km, S1=8 km,
  S2=12 km; radii must be strictly increasing and contiguous.
- `matching_rounds.current_stage` + `stage_evaluated` are durable.
- `matching_attempts.stage` associates each global ordinal with its logical
  stage; ordinals continue across stages.
- A stage snapshot and its READY attempt identities commit in one transaction;
  existing entries are appended, never rewritten.
- Advancement is a conditional transaction requiring every stage attempt and
  frozen entry to be conclusively REJECTED, no unresolved/reserved effect, no
  cancellation, and the round still PROCESSING.
- `NO_DRIVER` requires `current_stage == final stage`, discovery completed,
  every snapshot entry covered, and every attempt REJECTED. `MatchingFailedV1`
  commits in the same transaction.

## 4. Schema migration

`007_matching_production_work.sql` (append-only, idempotent):

- `matching_rounds`: `pickup_longitude`, `pickup_latitude`, `auto_execute`,
  `work_checked_at`, `work_error_code`, `stage_evaluated`.
- `matching_attempts`: `stage` (default S0), `last_checked_at`; state check
  extended with `CANCELLED`.
- `matching_cancel_intents`: durable cancel tombstone keyed by
  `(trip_id, matching_round)`.
- Production intake check: `auto_execute` rounds always carry pickup.
- Worker discovery index.

Legacy rows remain valid: `auto_execute` defaults false, `stage` defaults S0,
missing snapshot `stage` fields resolve to S0, and the terminal identity
constraint keeps its historical NOT VALID semantics.

## 5. Deterministic proofs (real infrastructure)

All production proofs start the real Matching service process (actual Program
registration, Kestrel, broker consumers, hosted worker) against real
PostgreSQL, Redis, RabbitMQ and the real Driver endpoints on Kestrel. Oracles
are authoritative rows/effects, not handler return codes.

| # | Proof | Contract | Authoritative oracle |
|---|---|---|---|
| 1 | `RideRequested_Through_Real_Broker_And_Worker_Reserves_Driver` | FUN-05, DSI-015/018/035 | RESERVED round + one attempt/R + Driver RESERVED + one `DriverReservedV1` outbox |
| 2 | response-loss | REL-01, DSI-017/031, DSL-001 | Driver committed while response held; ledger UNKNOWN; D2 stays READY/AVAILABLE; same-R automatic recovery |
| 3 | restart after ACK + process kill | REL-02, B-REC-01, DSI-018 | killed process; fresh process reserves same persisted R with no test endpoint |
| 4 | two Matching processes | CON-06, B-REC-01 | one round/attempt/R across two real workers; one Driver outbox identity |
| 5 | Redis outage then recovery | REL-14, B-REC-01 | round stays PROCESSING with `REDIS_UNAVAILABLE`; no NO_DRIVER; later reservation succeeds |
| 6 | cancellation while UNKNOWN | REL-03-shaped, Doc 02 §12.9 | cancel flag durable; no D2 dispatch; same-R reconcile; committed reservation remains RESERVED |
| 7 | cancel before ride request | Doc 02 §12.9, durable intent | tombstone applied atomically at intake; round CANCELLED with zero attempts/reservations |
| 8 | S0→S1→S2 progression | DSI-019/021/022/023 | three attempts in stages S0,S1,S2 at ordinals 0,1,2; first two REJECTED, third RESERVED; snapshot stages/reservation identities preserved |
| 9 | all stages exhausted | DSI-015/021/035, DSL-003 | S2 NO_DRIVER + exactly one `MatchingFailedV1`; redelivery adds none |
| 10 | stage gate component proofs | DSI-022, DSI-021, DSI-035 | READY/IN_FLIGHT/UNKNOWN/RESERVED all block advance; missing coverage blocks; no backward; terminal needs final evaluated stage |

Additional regression proofs retained:

- W7/W11 reserve, reconcile, fencing, stale projection, duplicate workers.
- W9 terminal Outbox atomicity and exact result identity.
- W12 metrics, trace continuity, authorization exposure negative tests.
- B12 auditor invariants over real final states.

## 6. Measured results

| Check | Result |
|---|---|
| Baseline Release build at `4aea3f5` | PASS, 0 errors, 14 warnings |
| Baseline Matching unit | 104/104 PASS |
| Baseline Matching integration | 158/158 PASS |
| Final Release solution build | PASS, 0 errors |
| Final Matching unit | 109/109 PASS |
| Final Matching integration (including 13 new production/gate proofs) | 171/171 PASS |
| Production proof classes | 13/13 PASS |
| Driver unit | 131/131 PASS |
| Driver integration | 113/113 PASS |
| B12 auditor (final source) | 18/18 PASS — see `auditor-final.md` |
| Platform integration | 60/60 PASS |
| Payment integration / unit | 28/28, 28/28 PASS |
| Trip integration / unit | 162/162, 299/299 PASS |
| Identity integration | 1 env-gated skip (not counted as verification) |
| SystemTests | 0 discovered (NOT RUN, never counted as PASS) |
| Full selected regression total | 1119 passed, 1 skipped, 0 failed |
| Compose config | PASS (`docker compose config --quiet`) |
| Secret/hygiene scan | PASS, no findings in the changed/new surface |
| Migration from empty + 005/006 upgrade | PASS (Matching migration/integration tests) |
| W11 critical campaign, five consecutive passes | **5/5 PASS** on `b4cf93c`, clean source status — see `w11-five-pass/` |

### W11 five-pass campaign (final source)

Run `five-pass-20261029t125402z` on commit `b4cf93c` with an empty
`sourceStatus`. All seven required scenarios passed in all five consecutive
passes:

```text
REL-01  LostReserveResponse/container        5/5 PASS
REL-02  MatchingSIGKILL/container            5/5 PASS
CON-02  both schedules                       5/5 PASS
CON-04  direct + real broker                 5/5 PASS
REL-12  handler + real broker                5/5 PASS
REL-13  handler + real broker                5/5 PASS
REL-14  real partition + recovery            5/5 PASS
```

Machine-readable matrix: `w11-five-pass/five-pass-summary.json`; per-pass
scenario/test/result/identities: `w11-five-pass/scenarios.tsv`. The campaign's
REL-02 remains the existing harness-triggered container proof; the new
`Restart_Recovers_Acked_Work_Without_Any_Manual_Trigger` proof covers the
production worker path independently. No old five-pass evidence was reused for
the new production behavior.

### Final auditor regression

The B12 auditor was rerun on `b4cf93c` (clean tree) and all 18 audit tests
passed. Positive scenarios: 31 PASS, 0 FAIL, 17 NOT RUN (A/C-owned), 0 BLOCKED.
Negative controls detected exactly as designed (11 expected FAIL, 4 expected
BLOCKED). This is not an overall G4 green claim; see `auditor-final.md`.

Exact commands:

```bash
dotnet build RideHailing.Distributed.slnx -c Release
dotnet test tests/Unit/RideHailing.MatchingService.UnitTests -c Release
dotnet test tests/Integration/RideHailing.MatchingService.IntegrationTests -c Release
bash scripts/fault/b-w11-full-campaign.sh
```

## 7. Verdict and remaining gates

**`B PRODUCTION MATCHING RECOVERY AND STAGES GREEN — SHARED G4 GATES TRACKED`**

B-owned B-REC-01 and DSI-021 are closed on the W12 repair layer with
production-path proofs. Overall G4 is not green: SEC-AUTH-01..04 and the A/C
acceptance gates remain independent.

## 8. Remaining B-owned findings

- `POST /internal/matching/rounds` remains an unauthenticated internal demo
  seam. It cannot initiate production work (`auto_execute` stays false), but it
  can create/inspect local rounds. Gating it belongs to the shared security
  contract decision (SEC-AUTH), not this repair.
- Reconcile exhaustion remains visible as `RECONCILE_EXHAUSTED` on an UNKNOWN
  attempt with continued backoff; no terminal `REQUIRES_REVIEW` state is
  invented because Doc 01 §12.3/§13 permits visible review and the W9 terminal
  vocabulary does not define one.
- Legacy rows with a NULL dispatch fence stay UNKNOWN on AVAILABLE/incompatible
  outcomes (existing W7 conservative rule).

## 9. A/C/lead dependencies

- `SEC-AUTH-01..04` remain open until approved shared principal/service-identity
  contracts exist. This repair does not add fake JWT validation, trust unsigned
  identity headers, or expose new HTTP mutation endpoints.
- A-owned Trip current-round guard, cleanup and Trip-side cancellation remain
  A's acceptance rows.
- C-owned shared fault platform, broker HA and Outbox relay acceptance remain
  C's rows. This repair reuses the frozen topology/envelope/Outbox contracts.
- Overall G4 remains blocked by shared security and A/C acceptance; B-owned
  recovery/stage technical readiness is green on this layer.

## 10. Evidence hygiene

Only this curated README is added for the repair. Raw process logs and
container logs remain gitignored. The historical W12 audit README is not
rewritten; it carries a one-line pointer to this superseding repair.
