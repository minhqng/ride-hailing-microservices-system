# B-W12 Phase 2 — Formal regression, authoritative audit, G4 and handoff

> Historical audit state at the W12 head. The B-owned `B-REC-01` and `DSI-021`
> gaps below were repaired forward on the same B-W12 layer; see
> [B-REC-01/DSI-021 evidence](../b-rec01-dsi021/README.md). SEC-AUTH-01..04 and
> the A/C gates recorded here remain independent of that repair.

**Verdict: `B-W12 DELIVERABLES COMPLETE — G4 BLOCKED (SECURITY + B-OWNED RECOVERY TRIGGER)`**

Branch `feature/B-W12-observability-g4`, PR
[#45](https://github.com/minhqng/ride-hailing-microservices-system/pull/45),
based on verified W11 head `3ba3724e7e7df378e8c234af685c027ad9d26863`
(PR #44, base `main` `86062af028d01b86f07b1c902f040a41c41fe57f`).

| Item | Value |
|---|---|
| Tested B code head | `3f7aa18d26739085b4b8d82b06290233cf76a40f` |
| Tested tree | `208b4e74e4fca0d1d7dd4c883bff96c9a2a3fae1` |
| Formal full solution | 1101 passed, 1 skipped, 0 failed (1102 total) |
| W12 focused | 19/19 |
| B auditor | 16 runs / 14 negative controls; positive AUD-01/02/04/06/07/08 PASS; AUD-03/05 NOT RUN (A/C) |
| W11 critical campaign | five consecutive passes at the audit commit `60f5cd4`; production `src/` tree identical to the tested head |
| Overall G4 | **FAIL/BLOCKED — SEC-AUTH-01 Critical open; B-REC-01 recovery trigger absent; DSI-021 NOT RUN** |

## What landed in Phase 2

### B-W12-07 — formal full regression
A Release build and full solution test run against the committed Phase 2
source, with real PostgreSQL/RabbitMQ/Redis for infrastructure-bound suites.
Per-project counts and exact commands are in `g4/regression-summary.json` and
`test-output/formal-full-regression.txt`. No test assertion was weakened and no
B-owned correctness regression was found. The SystemTests project discovers
zero tests and is recorded as NOT RUN, never counted as PASS. The Identity auth
test skip is recorded separately, not counted as verification.

### B-W12-08 — authoritative invariant auditor
`scripts/audit/b-w12-g4.sh` runs the B-owned test-scope auditor over disposable
PostgreSQL environments and real scenario final states. It uses read-only
verification transactions, implements AUD-01..08 with the required result
contract (`runId`, `sourceCommit`, `scenarioId`, `observationMode`,
`authoritySources`, `inspectedRecordCount`, `status`, `findings`,
`elapsedTime`), and writes `auditor/auditor-results.{json,md}` plus
`auditor/negative-controls.md`.

- AUD-01/02/04/06/07/08: PASS on real positive scenarios (reserved agreement,
  no-Driver terminal, cancel+release, driver lifecycle, late-release fencing,
  recognized pending cleanup).
- AUD-03: NOT RUN — A-owned Trip auditor; source/scenario provenance differs.
  B does not duplicate it and does not reuse A's result as B's PASS.
- AUD-05: NOT RUN — C-owned Payment authority.
- Negative controls: contradictory Matching terminal facts, missing terminal
  intent, leaked reservation after a falsely confirmed cleanup, stuck cleanup,
  Driver–Trip disagreement, stuck unresolved rows, empty dataset, unreachable
  authority. Constraint-enforced guards are demonstrated as enforcement.
- A PASS requires the owning authority; proxy evidence cannot silently become
  PASS. A's PR #43 TripInvariantAuditor was reviewed for its status discipline;
  B's auditor is independently implemented and does not copy A's branch.

### B-W12-09 — G4 assessment
`g4/coverage-matrix.{md,json}` contains every required FUN/CON/REL/DSI/DSL row
with contract id, requirement, proof, source SHA, authority, result, evidence,
limitation and owner, plus the seven architectural prohibitions. The verdict is
in `g4/g4-verdict.md`. **G4 remains FAIL/BLOCKED** while SEC-AUTH-01 exists;
B-REC-01 leaves REL-01/REL-02/DSL-001 PARTIAL; DSI-021 has no executable proof
and is not filled with PASS.

### B-W12-10 — cross-team handoff
`cross-team-handoff.md` explains Driver correctness (OCC linearization, exact R
fencing, Outbox atomicity, lifecycle provenance, late compensation), Matching
correctness (durable attempts, same-R reconcile, NO_DRIVER terminalizer, Redis
projection-only), fault boundaries, observability semantics, and G4/security
responsibilities. Status: `HANDOFF DELIVERED / HUMAN REVIEW PENDING / PEER
RERUN PENDING`.

### B-REC-01 — production reserve/reconcile trigger (B-owned gap)
`MatchingReserveOrchestrator.ExecuteRoundAsync` and `ReconcileAsync` have no
production caller: only the Testing-only `MatchingTestEndpoints` invoke them.
`RideRequestedHandler` creates/resumes the durable round but does not execute a
reserve, and no hosted service reconciles IN_FLIGHT/UNKNOWN attempts after a
restart or broker redelivery. The W11 REL-01/REL-02 proofs are therefore
harness-triggered: the recovery *logic* (same attempt, same reservation,
version-fenced reconcile) is proven, but unattended recovery is **NOT
IMPLEMENTED**. REL-01/REL-02/DSL-001 are PARTIAL until the production path and
an unattended reconcile trigger exist and are retested.


## Security (release blocker)

Real HTTP probes reproduce unauthenticated Driver mutations and Outbox creation:

| ID | Severity | Status |
|---|---|---|
| SEC-AUTH-01 | CRITICAL | OPEN |
| SEC-AUTH-02 | HIGH | OPEN |
| SEC-AUTH-03 | HIGH | OPEN |
| SEC-AUTH-04 | MEDIUM | OPEN |
| SEC-07-01 | MEDIUM (partial) | body bound present; no rate limit |
| SEC-08 | INFO | DB ownership tests |
| SEC-09 | PASS | secret scan |

These are reproduced exposures, not successful security enforcement. The
remediation agreement is `security/auth-remediation-handoff.md`. No fake auth,
unsigned-header trust or invented Identity provider was introduced.

## Evidence map

- `test-output/formal-full-regression.txt` — build + full solution output.
- `test-output/w11-critical-regression.txt` — five-pass W11 campaign on the W12 source.
- `g4/regression-summary.json` — machine-readable counts/environment/commands.
- `g4/coverage-matrix.md`, `g4/coverage-matrix.json` — G4 checklist.
- `g4/g4-verdict.md` — red verdict and exit criteria.
- `auditor/` — auditor JSON/Markdown/controls.
- `security/auth-remediation-handoff.md` — remediation agreement.
- `cross-team-handoff.md` — B-W12-10 execution handoff.
- `manifest.md` — Phase 2 source binding and commands.
- `hashes.txt` — SHA-256 over every deliverable.

## Non-claims

- G4 is not green; SEC-AUTH-01..04 remain open.
- AUD-03 (Trip) and AUD-05 (Payment) are not run by B.
- B-REC-01: the production Matching runtime does not execute reserve rounds and
  has no unattended restart/redelivery reconcile trigger. REL-01/REL-02/DSL-001
  are PARTIAL; their W11 proofs are harness-triggered.
- DSI-021 has no executable proof.
- FUN-05 is PARTIAL for B; A/C end-to-end integration is pending.
- No W13 performance work.
- CI did not trigger for the stacked base; zero checks is not green.
- No A/C peer rerun or human sign-off yet.

## Phase 1 (historical)

Phase 1 delivered B-W12-01..06 (metrics, tracing, security exposure, invalid
location). Its evidence (1083 passed / 1 skipped, 19/19 focused) remains in
`metrics/`, `traces/`, `security/inventory.md`, `security/http-probes.txt` and
the Phase 1 test outputs. Phase 2 preserves those results and adds the four
remaining deliverables.
