# B-W12 Phase 2 handoff

**Status: `HANDOFF DELIVERED` · `HUMAN REVIEW PENDING` · `PEER RERUN PENDING`**

## Current state

Branch `feature/B-W12-observability-g4`, PR
[#45](https://github.com/minhqng/ride-hailing-microservices-system/pull/45)
against the W11 branch (PR #44, base `main`). Tested B code:
`3f7aa18d26739085b4b8d82b06290233cf76a40f` (tree
`208b4e74e4fca0d1d7dd4c883bff96c9a2a3fae1`). Evidence is a docs/evidence-only
commit after the tested head.

**Verdict: `B-W12 DELIVERABLES COMPLETE — G4 BLOCKED (SECURITY + B-OWNED RECOVERY TRIGGER)`.**

## B-W12 completion

| Task | Disposition |
|---|---|
| B-W12-01 Driver metrics | COMPLETE |
| B-W12-02 Matching metrics | COMPLETE |
| B-W12-03 Redis projection metrics | COMPLETE |
| B-W12-04 Reserve/reconcile tracing | COMPLETE — in-process chain; durable cross-restart traceparent is a recorded limitation |
| B-W12-05 Driver authz/spoofing | OPEN — SECURITY DEPENDENCY (`SYSTEM_AUTH_PENDING`; SEC-AUTH-01..04) |
| B-W12-06 Invalid location | COMPLETE |
| B-W12-07 Formal full regression | COMPLETE — 1101 passed / 1 skipped / 0 failed; no B-owned regression |
| B-W12-08 Authoritative auditor | COMPLETE — executable AUD-01..08, honest AUD-03/05 NOT RUN, non-vacuous controls |
| B-W12-09 G4 assessment | COMPLETE — red verdict (security + B-REC-01 recovery trigger + DSI-021 evidence gap) |
| B-W12-10 Cross-team handoff | COMPLETE — HANDOFF DELIVERED / SIGN-OFF PENDING |

## What a reviewer should run

```bash
# full regression
dotnet build RideHailing.Distributed.slnx -c Release
dotnet test RideHailing.Distributed.slnx -c Release --no-build

# B authoritative auditor + negative controls
./scripts/audit/b-w12-g4.sh

# W11 five-pass fault campaign (clean tree; Docker required)
./scripts/fault/b-w11-full-campaign.sh
```

## Read first

1. `cross-team-handoff.md` — Driver/Matching correctness, fault boundaries,
   observability semantics, G4/security responsibilities.
2. `g4/g4-verdict.md` — why G4 is red and the exact exit criteria.
3. `g4/coverage-matrix.md` — every required contract row and its authority.
4. `auditor/auditor-results.md` and `auditor/negative-controls.md` — audit
   statuses and non-vacuity controls.
5. `security/auth-remediation-handoff.md` — SEC-AUTH-01..04 remediation
   agreement.
6. `test-output/formal-full-regression.txt` and
   `test-output/w11-critical-regression.txt` — executed evidence.

## Owner gates

| Gate | Owner | Status |
|---|---|---|
| Shared authenticated-principal/token contract | A (Identity) + C (Gateway) | Missing; SEC-AUTH-01 Critical |
| Service-side Driver/Matching enforcement | B | Not implementable until the shared contract is approved |
| Internal route network isolation | C + lead | Open (host-published internal routes) |
| Production Matching reserve/reconcile trigger (B-REC-01) | B | NOT IMPLEMENTED — only the Testing seam calls ExecuteRoundAsync/ReconcileAsync; REL-01/02/DSL-001 PARTIAL |
| DSI-021 stage-monotonicity disposition | B + lead | NOT RUN; evidence gap |
| A/C peer rerun and sign-off | A + C | PENDING |
| G4 lead acceptance | lead | BLOCKED until security remediation |
| CI triggering for stacked base | leader | NOT TRIGGERED (shared workflow/billing) |

## Honest limitations

- Auditor AUD-03 (Trip) and AUD-05 (Payment) are NOT RUN; A/C own those
  authorities. The B audit never reports proxy evidence as full PASS.
- The full happy ride (FUN-05) is PARTIAL: B's matching/reserve leg is green,
  but A's Saga and C's Payment consumer are not integrated in this B run.
- B-REC-01: the production Matching runtime has no reserve execution or
  unattended restart/redelivery reconcile trigger. REL-01/REL-02/DSL-001 are
  PARTIAL; their W11 proofs are harness-triggered.
- No authorization PASS: real HTTP exposure is reproduced as a Critical
  vulnerability.
- CI has zero check runs for the stacked PR; this is not green.
- A/C execution and human sign-off remain required; this handoff is not a
  substitute.
