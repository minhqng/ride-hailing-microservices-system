# B-W12-09 — Core Release Gate G4 assessment

**VERDICT: `B-W12 DELIVERABLES COMPLETE — G4 BLOCKED (SECURITY + B-OWNED RECOVERY TRIGGER)`**

This is the intended B outcome: completing the G4 assessment with a red verdict
is successful execution of B-W12-09. A green team G4 is **not** claimed.

## Source binding

| Item | Value |
|---|---|
| Repository | `minhqng/ride-hailing-microservices-system` |
| Branch | `feature/B-W12-observability-g4` |
| B tested head | `3f7aa18d26739085b4b8d82b06290233cf76a40f` |
| B tested tree | `208b4e74e4fca0d1d7dd4c883bff96c9a2a3fae1` |
| Base | W11 head `3ba3724e7e7df378e8c234af685c027ad9d26863` (PR #44) |
| PR | #45 (base `feature/B-W11-deterministic-faults`) |
| A-owned evidence head | `132ecfd4ebc7f452af5e28f030bf66754e9b0df7` (unmerged A-W12) |

## Mandatory blockers (G4 cannot be green)

1. **SEC-AUTH-01 — CRITICAL, OPEN.** Real HTTP probes prove unauthenticated
   callers can mutate Driver authoritative state and commit Outbox intents.
   SEC-AUTH-02 (actor spoofing), SEC-AUTH-03 (exposed internal authority
   endpoints) and SEC-AUTH-04 (missing object-level authorization) remain open.
   These are reproduced exposures, not passing negative tests. No fake auth,
   unsigned-header trust, development JWT key or invented Identity provider was
   introduced. Remediation agreement:
   `security/auth-remediation-handoff.md`.
2. **DSI-021 — NOT RUN.** No executable S0→S1→S2 stage-monotonicity proof
   exists; `current_stage` remains the schema default `S0`, and the serial
   candidate design has no multi-stage transition. The invariant is vacuous in
   the current implementation but is not filled with PASS. This is a B-owned
   evidence gap requiring lead disposition.
3. **B-REC-01 — HIGH, OPEN (B-owned).** The production Matching runtime does
   not execute reserve rounds and has no restart/redelivery-triggered
   reconciliation: only the Testing-only `MatchingTestEndpoints` calls
   `ExecuteRoundAsync`/`ReconcileAsync`. W11 REL-01/REL-02 prove the recovery
   *logic* under a harness; unattended recovery is **NOT IMPLEMENTED**.
   REL-01/REL-02/DSL-001 are therefore PARTIAL. W13/W14 must wire the
   production reserve path and an unattended reconcile trigger before
   accepting recovery results.
4. **FUN-05 — PARTIAL.** B's matching/reserve leg is green; the full happy ride
   also needs A's Saga and C's Payment consumer. No full FUN-05 PASS is claimed.
5. **A/C peer rerun and sign-off pending.** AI review is not a substitute;
   A-owned rows cite the unmerged A-W12 branch.
6. **CI NOT TRIGGERED.** PR #45 has zero check runs because `ci.yml`
   `pull_request.branches` excludes the W11 base (leader-owned shared workflow;
   billing issues are not green). Zero checks is not green.

## What is green (B-owned scope)

- Formal Release build and full solution tests: **1101 passed, 1 skipped, 0
  failed** (1083 baseline + 18 audit; the only skip is the Identity
  `RIDE_AUTH_FIXTURE` absent case, not counted as verification).
- Auditor: AUD-01/02/04/06/07/08 PASS on real scenario final states; negative
  controls detect every seeded violation; constraint-enforced guards are
  demonstrated, not bypassed; empty/unreachable datasets produce NOT RUN/BLOCKED.
- W11 critical campaign: five consecutive passes of REL-01/02, CON-02/04,
  REL-12/13/14 at the audit commit `60f5cd4` (production `src/` tree identical to the tested head; see
  `test-output/w11-critical-regression.txt`). REL-01/02 recovery is
  harness-triggered; see B-REC-01.
- All seven architectural prohibitions have executable B-owned proofs.
- AUD-03 (Trip) and AUD-05 (Payment) are **NOT RUN** with named A/C
  dependencies; they are never reported as PASS.

## Honest limitations

- The auditor inspects B-owned Driver/Matching authority plus real Trip rows for
  cross-service agreement; Payment authority is absent (C dependency).
- AUD-08 covers Driver/Matching Outbox/attempt ages; Trip Saga/Outbox is
  A-owned (A's AUD-08 PASS).
- Reconciliation trace linkage requires a caller-supplied workflow/consumer span;
  traceparent is not persisted in the Matching ledger.
- No production caller executes a Matching reserve round or reconciles an
  unresolved attempt; the W11 proofs use the Testing-only seam (B-REC-01).
- Release evidence is bound to the code commit above; evidence-only commits do
  not change production behavior.

## Exit criteria for a future green G4

1. Approve and implement the shared authenticated-principal and service-identity
   contract (A Identity + C Gateway + B enforcement).
2. Pass the reject-without-mutation matrix and re-run SEC-AUTH-01..04 tests.
3. Wire the production Matching reserve path and an unattended
   restart/redelivery reconcile trigger, then re-run REL-01/REL-02/DSL-001
   without the Testing seam (B-REC-01).
4. Resolve DSI-021 (implement/prove multi-stage monotonicity or formally retire
   the stage model with lead approval).
5. Integrate the A/C legs for FUN-05 and complete the full happy-path proof.
6. Fix CI triggering for the stacked base and obtain green exact-head runs.
7. Obtain A/C peer rerun and human sign-off.
