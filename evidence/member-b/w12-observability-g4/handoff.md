# B-W12 Phase 2 handoff

**Status: `HANDOFF DELIVERED` · `HUMAN REVIEW PENDING` · `PEER RERUN PENDING`**

## Current state

Branch `feature/B-W12-observability-g4`, PR
[#45](https://github.com/minhqng/ride-hailing-microservices-system/pull/45)
against the W11 branch (PR #44, base `main`). Tested B code:
`7bc4510cbf524406d545b7529c938414b85f9c9c` (tree
`27ba100e59bec471489a5f7cdfaa5dc7c54361b9`). Evidence is a docs/evidence-only
commit after the tested head.

**Verdict: `B-W12 DELIVERABLES COMPLETE — G4 SECURITY BLOCKED`.**

## B-W12 completion

| Task | Disposition |
|---|---|
| B-W12-01 Driver metrics | COMPLETE |
| B-W12-02 Matching metrics | COMPLETE |
| B-W12-03 Redis projection metrics | COMPLETE |
| B-W12-04 Reserve/reconcile tracing | COMPLETE — in-process chain; durable cross-restart traceparent is a recorded limitation |
| B-W12-05 Driver authz/spoofing | OPEN — SECURITY DEPENDENCY (`SYSTEM_AUTH_PENDING`; SEC-AUTH-01..04) |
| B-W12-06 Invalid location | COMPLETE |
| B-W12-07 Formal full regression | COMPLETE — 1099 passed / 1 skipped / 0 failed; no B-owned regression |
| B-W12-08 Authoritative auditor | COMPLETE — executable AUD-01..08, honest AUD-03/05 NOT RUN, non-vacuous controls |
| B-W12-09 G4 assessment | COMPLETE — red verdict (security + DSI-021 evidence gap) |
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
| DSI-021 stage-monotonicity disposition | B + lead | NOT RUN; evidence gap |
| A/C peer rerun and sign-off | A + C | PENDING |
| G4 lead acceptance | lead | BLOCKED until security remediation |
| CI triggering for stacked base | leader | NOT TRIGGERED (shared workflow/billing) |

## Honest limitations

- Auditor AUD-03 (Trip) and AUD-05 (Payment) are NOT RUN; A/C own those
  authorities. The B audit never reports proxy evidence as full PASS.
- The full happy ride (FUN-05) is PARTIAL: B's matching/reserve leg is green,
  but A's Saga and C's Payment consumer are not integrated in this B run.
- No authorization PASS: real HTTP exposure is reproduced as a Critical
  vulnerability.
- CI has zero check runs for the stacked PR; this is not green.
- AI review is not a substitute for A/C execution or human sign-off.


---

# Appendix — Phase 1 handoff (historical full text)

# B-W12 Phase 1 handoff

## Current state

Branch `feature/B-W12-observability-g4` on verified W11 HEAD `3ba3724`.
PR https://github.com/minhqng/ride-hailing-microservices-system/pull/45 targets `feature/B-W11-deterministic-faults` while W11 PR #44
remains open.

- Implementation commit: `699a80151984ce91e3990be5621526df8bdcca56`, tree `8f80eb728cd32aff0b3b67b39354759805e231d4`.
- Evidence commit: `3f2ed52` (bundle) + final metadata commit on this PR head.
- Full local regression: 1083 passed, 1 skipped, 0 failed.
- W12 focused proofs: 19/19.
- Independent fresh-context review: completed; P1 findings fixed, remaining
  P2 trade-offs recorded below.
- GitHub CI: `NOT TRIGGERED` — `ci.yml` `pull_request.branches` excludes `feature/B-W11-deterministic-faults`; 0 check runs. No green claim.

**Verdict: `B-W12 PHASE 1: CODE VERIFIED — SECURITY/EXTERNAL GATES REMAIN`.**

## B-W12 status

| Task | Status |
|---|---|
| B-W12-01 Driver metrics | COMPLETE — commit-after-effect, bounded labels, MeterListener + real HTTP/repository/broker proofs |
| B-W12-02 Matching metrics | COMPLETE — duration/candidates/attempts/unknown/reconcile/no-driver; replay-safe |
| B-W12-03 Redis projection metrics | COMPLETE — real FCALL/handler, stale/degraded separation, no duplicate layer |
| B-W12-04 Reserve/reconcile tracing | COMPLETE for the supported in-process workflow; durable cross-restart traceparent is a recorded limitation |
| B-W12-05 HTTP authz/spoofing | **OPEN SECURITY FINDING — `SYSTEM_AUTH_PENDING`**; no fake auth was introduced |
| B-W12-06 Invalid location | COMPLETE — 18 invalid HTTP cases, no mutation/publication/projection change |
| B-W12-07 B full regression | NOT CLAIMED — local regression is supporting evidence, formal G4-scope run remains |
| B-W12-08 Invariant auditor | NOT RUN |
| B-W12-09 G4 review | NOT CLAIMED |
| B-W12-10 Cross-explain B mechanisms | NOT DONE |

## Remaining work

### W12-07 — formal B full regression
Re-run the commands in `manifest.md` against the merged W11+W12 tree, including
the invariant-auditor scope required by Doc 03. The local 19/19 W12 proofs and
full solution run are inputs, not the formal G4 sign-off.

### W12-08 — invariant auditor
Run AUD-01… against the real databases after the G4-scope regression; record
DSI-001..007/015..023/030/031/033 and DSL-001/003 evidence. Not run in Phase 1.

### W12-09 — G4 review
Use Doc 05B §21.3. This Phase 1 does not claim:
- FUN/CON/REL/DSI/DSL checklist completion,
- no S0/S1 B-owned defect,
- performance/G4 acceptance.

### W12-10 — cross-explain
Walk A/C through metric semantics, the reserve/reconcile span chain, and the
`SYSTEM_AUTH_PENDING` boundary.

## External gates and owners

| Gate | Owner | Status |
|---|---|---|
| Authenticated principal / token contract | Shared/A (Identity) + C (Gateway) | Missing. Identity/Gateway are skeletons; no JWT/token issuance or validation. |
| Service-side authorization once contract exists | B | Not implementable without the shared contract; B owns the Driver/Matching enforcement follow-up. |
| Internal route network isolation | C (Compose/Gateway) + lead | Driver 8102 / Matching 8104 are host-published; internal routes are directly reachable. |
| W12 PR review / merge | B + A/C | PR open; do not merge automatically. |
| GitHub Actions | leader | `NOT TRIGGERED` — `ci.yml` `pull_request.branches` excludes `feature/B-W11-deterministic-faults`; 0 check runs. No green claim; billing-blocked runs are also not green. |
| Peer rerun | A/C + B | PENDING; AI review is not a substitute. |
| G4 auditor / final acceptance | B + lead | Not run in Phase 1. |

## Independent review disposition

| Finding | Disposition |
|---|---|
| P1 `matching_unknown_total` could re-count retries/pre-effect failures | Fixed: counts only committed UNKNOWN transitions; repeated execution on UNKNOWN and pre-effect failures are excluded; test added |
| P1 reconcile trace claimed durable linkage | Fixed evidence: test resumes through a real captured workflow span; W12 does not persist traceparent; limitation documented |
| P1 stale-release counter included provenance/illegal refusals | Fixed: only `STALE_RESERVATION` and `RESERVATION_ROUND_MISMATCH`; broker-path test and unit predicate test added |
| P2 reserve conflict includes not-found/unique backstop | Documented as definitive domain rejection with a narrow identity-collision backstop; no DB/transport failure counted |
| P2 round total missed thrown requests | Fixed: `outcome=error` recorded; test added |
| P2 projection latency missed content-conflict | Fixed: sample recorded before throw; test added |
| P2 candidate_count described as durable snapshot | Relabeled as offered candidate-sequence length (normally the frozen snapshot) |
| P2 privacy/PlatformBootstrap test gaps | Fixed: location request added to privacy trace; test host calls C bootstrap + B extension |
| P2 C-owned Observability files modified | Fixed: C files reverted; B-owned additive `AddDriverTelemetry`/`AddMatchingTelemetry` register sources/meters/HTTP instrumentation |
| P2 exposure coverage narrower than inventory | Fixed for create/read/offline; Matching round trigger is explicitly marked reviewed-not-exercised |

## Review focus

1. Verify Driver success is post-commit and replay-excluded; stale-release
   labels only cover token/round fencing.
2. Verify `matching_unknown_total`, `matching.no_driver_total` and
   `matching.attempt.created_total` are replay/duplicate safe.
3. Verify the Redis stale counters are emitted only from the FCALL reply and
   exact-replay semantics follow the frozen W4 contract.
4. Verify the trace chain and REL-01 no-fabricated-success behavior, and the
   documented durable-trace limitation.
5. Confirm `SYSTEM_AUTH_PENDING` is not misreported as an authorization PASS
   and that no C-owned production file changed.
