# B-W12 Phase 1 handoff

## Current state

Branch `feature/B-W12-observability-g4` on verified W11 HEAD `3ba3724`.
PR `<PR_URL>` targets `feature/B-W11-deterministic-faults` while W11 PR #44
remains open.

- Implementation commit: `4f523d10de592a5bd05585abb19f68b540e8b867`, tree `629a53e7146f3e01a9a4042d8479cdeb293e5c6f`.
- Evidence commit: `<EVIDENCE_SHA>`.
- Full local regression: 1083 passed, 1 skipped, 0 failed.
- W12 focused proofs: 19/19.
- Independent fresh-context review: completed; P1 findings fixed, remaining
  P2 trade-offs recorded below.
- GitHub CI: `<CI_STATUS>`.

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
| GitHub Actions | leader | `<CI_STATUS>`; billing-blocked runs are not green. |
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
