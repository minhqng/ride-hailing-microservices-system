# A-W12-07 — Core Release Gate G4 matrix (reviewed 2026-10-25, corrected 2026-10-26)

Reviewed against Doc 03 §35, Doc 04 §23 and Doc 05A §20.3 on the verified
A-W12 code revision `976c7f8662e05c0abe17a0dedcf8a45d94b03592` (branch
`feature/A-W12-trip-observability-g4`, stacked on OPEN PR #42 head
`46ac68a`; the 2026-10-26 targeted review added the verdict-level correction
at code revision `acaaa68` without changing any query or scenario). Statuses
use exactly: PASS / FAIL / NOT RUN / DEPENDENCY_PENDING. `LOCAL_PROXY_PASS`
is a supporting-evidence detail (Level B Trip-local proof), never a full
mandatory G4 PASS.

**VERDICT: G4 NOT YET GREEN.** A-scope entries are executable and green; the
team-level entries below carry exact blockers. A green A PR is not a green
team gate.

## Team G4 checklist (Doc 03 §35)

| Requirement | Owner(s) | Status | Executable proof / exact blocker |
| --- | --- | --- | --- |
| Happy ride path (FUN mandatory core) | A+B+C | DEPENDENCY_PENDING | A legs PASS in isolation (create→matching→reserved→accepted→authorize lineage, TripW05–W10 suites, 491 Trip tests green). End-to-end path needs B-W10 driver lifecycle (PR #39 OPEN, unmerged) and C-W10 payment consumer (PR #41 OPEN, unmerged) integrated with A's stack |
| CON-01 exclusive Driver reservation | B | DEPENDENCY_PENDING | B-W09/W10 branches OPEN (PRs #37/#38/#39); not integrated into the A stack; AUD-01 leg NOT RUN (B DB unavailable to A) |
| CON-02 GoOffline/Reserve | B | DEPENDENCY_PENDING | Same as CON-01 |
| CON-03 Payment idempotency | C | DEPENDENCY_PENDING | C-W10/W11 platform on PR #41 OPEN, unmerged; A's side of the lineage (frozen operation keys, DB CHECK) PASS |
| CON-04 stale compensation | A | PASS | W10/W11 exact-compensation proofs on A-W11 head (PR #42, CI green): stale release facts park NeedsReview, exact (driver, reservation, round) fencing |
| REL-01 reserve unknown | B | DEPENDENCY_PENDING | B-side UNKNOWN-reserve reconciliation unmerged (PR #39) |
| REL-02 Matching crash/reconcile | B | DEPENDENCY_PENDING | B-W09/W10 OPEN |
| REL-04 Outbox crash | A (+C shared) | PASS | A-W11 process-kill campaign (5/5 passes, PR #42) + W09 relay proofs; re-verified in A-W12 full-suite run (TripW09 lanes green) |
| REL-05 duplicate publish | A (+C shared) | PASS | W09 duplicate-publication proofs (same messageId republish, receipt replay); green in A-W12 regression |
| REL-06 commit-before-ACK | A (+C shared) | PASS | W08 durable-receipt commit-before-ACK oracle; green in A-W12 regression |
| REL-07 Saga restart | A | PASS | W11 real SIGKILL/restart proofs (PR #42); green in A-W12 regression |
| REL-09 compensation | A | PASS | W10/W11 exact-compensation proofs (Trip-authoritative rows); the W12 auditor compensated scenario adds supporting evidence (AUD-08 Level A PASS, AUD-07 LOCAL_PROXY_PASS) |
| REL-11 poison/DLQ | C (+A consumer) | DEPENDENCY_PENDING | Trip consumer poison classification is covered component-side (W08); the shared DLQ/deadletter topology lane belongs to C's W10/W11 platform (PR #41 OPEN) |
| SEC minimum + ownership isolation | A (Trip boundary) + C (platform) | PARTIAL: A AUTHORIZATION VERIFIED / SYSTEM AUTHENTICATION PENDING | Trip enforces role + object-level authorization given an authenticated principal and fails CLOSED for unauthenticated traffic (direct host-port exposure included — see final-state/f1-authz-boundary.md): 401/403/404-ownership, fail-closed claim parsing, SEC-04 spoofing inert, durable zero-effect oracles. AUTHENTICATION ITSELF (principal issuance/validation) does not exist yet anywhere — C-owned Identity/Gateway skeletons: SYSTEM_AUTH_PENDING. SEC-08/09 baseline belongs to C |
| Zero known S0/S1 correctness defect | A/B/C | PASS (A-scope) / TEAM PENDING | No known A-owned S0/S1 (W09–W12 proofs green, incl. W12 instrumentation introducing no behavior change — 989/989 solution tests). Team-wide confirmation needs B/C integration review |
| Invariant auditor green | A+B+C | DEPENDENCY_PENDING | Level A (Trip-authoritative DBs inspected): AUD-03, AUD-08 PASS. Level B supporting (LOCAL_PROXY_PASS): AUD-05/06/07 — Trip-local ledgers only. NOT RUN: AUD-01/02/04 + the B/C halves of AUD-05/06/07 — require B/C-owned datasets (final-state/aw12-06-auditor.md). Seeded-violation FAIL proofs confirm the auditor is not vacuous. A Trip-only dataset cannot make this row green |

## A's canonical checklist (Doc 05A §20.3)

| Item | Status | Proof |
| --- | --- | --- |
| FUN-04/06/07/08/09/10/12 | PASS | Trip component/integration suites green on the verified revision (W04–W10 lanes + W12 rerun) |
| CON-05 | PASS | W07 cancel-races + cleanup lanes green |
| REL-03/04/05/07/09/10/17 (as applicable) | PASS | W09 outbox, W11 process kills, W10 forward recovery, late-terminal-events lanes green in the A-W12 regression run |
| DSI-008..014 | PASS | W04–W07 durable-identity/idempotency/assignment proofs (evidence bundles w04–w07; tests green) |
| DSI-026..028 | PASS | W10 saga payment lineage (authorize/capture/void keys, DB CHECK) green |
| DSI-030/032/033/034 | PASS | W10/W11 terminal-state, exact-compensation, operation-key proofs green |
| DSL-002/005/006 | PASS | visible review states + saga broker delivery proofs green |
| All critical Trip outgoing intents use Outbox | PASS | No direct required publish outside the outbox (TripOutboxWriter is the single staging path; enforced by W09 concurrency proofs); W12 propagation test re-verifies the relay path |
| No unresolved A-owned S0/S1 defect | PASS | None known; 989/989 solution tests green |
| B and C can explain Saga/Outbox path | PENDING PEER VERIFICATION | Handoff published (Trip README §W12 + reproduce commands + 8-point checklist). A self-authored explanation is NOT proof of understanding — requires actual B/C rerun/signoff |

## Exact blockers to a green team G4

1. PR #36/#40/#42 (A-W09→W10→W11 stack) unmerged into main — the A chain
   itself is not integrated.
2. PR #39 (B-W10 driver lifecycle) and #37/#38 (B-W09) unmerged; B-side
   CON-01/02, REL-01/02 legs and B/C independent reruns pending.
3. PR #41 (C-W10/W11 payment messaging + shared fault platform) unmerged;
   C-side CON-03, shared REL-11 DLQ lane, SEC-01..09 platform baseline and
   the C-side payment-consumer integration pending.
4. Cross-service invariant-auditor execution (AUD-01/02/04, full 05/06)
   needs B/C dataset read access per the canonical verification design.
5. Mandatory authenticated/authorization SYSTEM evidence needs the C-owned
   Identity/Gateway JWT path (currently a skeleton).
6. B/C peer signoff on the A-W12-08 handoff (explain + rerun) pending.
