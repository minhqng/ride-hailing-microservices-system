# W15 acceptance matrix

Status vocabulary: `DONE_VERIFIED`, `IMPLEMENTED_UNVERIFIED`, `PARTIAL`,
`BLOCKED`, `NOT_STARTED`, `DEFERRED_BY_CANONICAL_SCOPE`.

Legend: implementation completeness, evidence completeness and integration
acceptance are separate. A green member PR is not team acceptance.

## Member A

| Task | Required output | Status | Evidence / adjudication |
| --- | --- | --- | --- |
| A-W15-01 | Freeze Trip/Saga/Outbox surface; remove dead experiments | DONE_VERIFIED | W15 delta adds no product/source change; freeze audit found no new service/technology/feature. No dead experimental surface was identified in the A W15 delta. |
| A-W15-02 | Clean-clone Trip migration/regression | DONE_VERIFIED (integrator execution) | Integrated clean-clone/CI path covers Trip migrations and regression. Executed by the integrator/CI, not personally by Member A; the human peer step remains TEAM-W15-02. |
| A-W15-03 | Trip FSM section | DONE_VERIFIED | `docs/member-a-trip-saga-report.md` FSM + authority/terminal text. |
| A-W15-04 | Outbox section | DONE_VERIFIED | Report Outbox section + four crash windows + at-least-once wording. |
| A-W15-05 | Saga section | DONE_VERIFIED | Report Saga FSM + local transaction/compensation/pivot/recovery text. |
| A-W15-06 | Failure evidence REL-04/07/09/10 | DONE_VERIFIED (team mapping) | Member report references W10/W11 bundles; `docs/team-technical-report.md` supplies the explicit REL mapping to source-bound bundles. |
| A-W15-07 | Curate diagrams | DONE_VERIFIED | Trip FSM, Saga FSM, Outbox crash table, compensation timeline in the report. |
| A-W15-08 | Review language against measured evidence | DONE_VERIFIED | Claim boundary retained; exactly-once explicitly rejected; historical source scope noted. |
| A-W15-09 | Final A regression/evidence manifest | PARTIAL | `evidence/member-a/w15-freeze/README.md` is a narrative index; W09-W14 bundles have codeCommit anchors, but W15 has no machine-readable hash manifest. Not a correctness gap; team manifest covers integration scope. |

## Member B

| Task | Required output | Status | Evidence / adjudication |
| --- | --- | --- | --- |
| B-W15-01 | Remove dead Matching experiments | DONE_VERIFIED | Freeze classification table + Matching README correction. |
| B-W15-02 | Clean-clone regression | DONE_VERIFIED | Fresh full clone, Release build, serial regression 1223 passed/0 failed/1 opt-in skip, fresh empty-volume runtime; source-bound `1984347`/tree `69b30a3`. |
| B-W15-03 | Driver authority/OCC section | DONE_VERIFIED | Report reservation section; row-lock + expected-version fencing; commit as linearization point. |
| B-W15-04 | Redis derived-state section | DONE_VERIFIED | Report Redis section; stale candidates cannot grant reservations; rebuild limits stated. |
| B-W15-05 | Matching/UNKNOWN section | DONE_VERIFIED | Report durable Matching section; no advance while UNKNOWN; same-R reconcile. |
| B-W15-06 | Performance methodology/results | DONE_VERIFIED | E01-E04 accepted populations; per-run quantiles; sample/SLA limits. |
| B-W15-07 | CON-01/REL-01 diagrams | DONE_VERIFIED | Two editable Mermaid sequences. |
| B-W15-08 | Validate claims vs raw data | DONE_VERIFIED | `validate-driver-matching-claims.py` PASS: 174 digest checks (12 overlap, 162 distinct). |
| B-W15-09 | Final evidence manifest | DONE_VERIFIED | Manifest + 7/7 canonical-LF hashes verified; source/tree anchored. |

## Member C

| Task | Required output | Status | Evidence / adjudication |
| --- | --- | --- | --- |
| C-W15-01 | Freeze topology/retry semantics | DONE_VERIFIED | Release overlay/HA config only; no topology or retry semantic change in W15. |
| C-W15-02 | Clean-clone infrastructure boot | PARTIAL | Compose release/HA rendering validated; C source records no clean-clone boot. Integrated CI boots a fresh stack. |
| C-W15-03 | Confirm/ACK section | DONE_VERIFIED | Report REL-05/06 diagrams + claim boundary. |
| C-W15-04 | Dedup/Outbox relay section | DONE_VERIFIED | Report durable dedup/relay metrics and crash-window explanation. |
| C-W15-05 | Retry/DLQ section | DONE_VERIFIED | Report REL-08/11 poison/DLQ diagram + finite classification/ACK wording. |
| C-W15-06 | Payment idempotency section | DONE_VERIFIED (team supplement) | C report claim boundary + REL-08 diagram; `docs/team-technical-report.md` states operationKey same-key conflict and timeout-after-effect reconciliation. |
| C-W15-07 | Observability/security/HA claim boundaries | DONE_VERIFIED | Report metrics, secret constraints, HA narrow claim. |
| C-W15-08 | REL-05/06/08/11/16 diagrams | DONE_VERIFIED | Four Mermaid diagrams in the report. |
| C-W15-09 | Verify source/version claims | DONE_VERIFIED | No version-specific runtime claims in the report; integration review checked code references. |
| C-W15-10 | Final evidence manifest | PARTIAL | `evidence/member-c/final/manifest.json` is minimal (no hashes/codeCommit); team manifest carries integration hashes and source binding. |

## Team W15

| Task | Required output | Status | Evidence / adjudication |
| --- | --- | --- | --- |
| TEAM-W15-01 | Feature freeze | DONE_VERIFIED | W15 delta: docs, evidence, CI, release overlay, scanner, one README correction. No new service/broker/DB/orchestration/product feature. |
| TEAM-W15-02 | Another member clean-clone | PARTIAL (process gate) | Independent automated clean-clone/CI verification passes; no evidence that a separate human member personally executed it. This is a final course/submission gate, not a technical source blocker. |
| TEAM-W15-03 | Evidence integrity | PARTIAL | B complete; A/C have source-bound historical bundles but thinner W15 manifests. Integration re-ran build, scanner, release fail-closed, anchor fixtures, perf tests, claims validator and 7/7 B hashes. |
| TEAM-W15-04 | Report assembly | DONE_VERIFIED | `docs/team-technical-report.md` + A/B/C reports + docs index + contribution traceability. |

## Acceptance adjudication

Technical source acceptance: W15 candidate is frozen, builds, passes the
deterministic integration checks above, ships a fail-closed release overlay and
a secret scanner, and contains no runtime product change relative to accepted
W14 main.

Remaining process gates: a separate human member clean clone and the W16 human
rehearsals/oral defenses. These cannot be manufactured by automation and are
reported as course/submission gates, not hidden PASS results.
