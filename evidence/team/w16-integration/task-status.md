# W16 acceptance matrix

Status vocabulary: `DONE_VERIFIED`, `IMPLEMENTED_UNVERIFIED`, `PARTIAL`,
`BLOCKED`, `NOT_STARTED`, `DEFERRED_BY_CANONICAL_SCOPE`.

## Member A

| Task | Required output | Status | Evidence / adjudication |
| --- | --- | --- | --- |
| A-W16-01 | Re-run A release evidence | DONE_VERIFIED | Member ledger records A build/unit/integration/compose evidence; exact-tree CI independently re-runs build and full tests. |
| A-W16-02 | Rehearse Payment failure→compensation 3x | PARTIAL | No three A-owned rehearsals. The integrated core-runtime gate executes cancellation/void exact compensation once; W10 evidence retains the deterministic compensation/capture lanes. |
| A-W16-03 | Rehearse Outbox crash backup demo | PARTIAL | W11 producer-crash evidence retained; no W16 three-run A rehearsal. |
| A-W16-04 | Rehearse Saga restart demo | PARTIAL | W11 restart evidence retained; no W16 three-run A rehearsal. |
| A-W16-05 | Oral defense drill | NOT_STARTED | Human drill cannot be manufactured; A supplied the report/answer material. |
| A-W16-06 | Review B/C demo handoffs | NOT_STARTED | B's runbook records A/C boundary inputs; actual human review/sign-off pending. |
| A-W16-07 | Final README/setup verification | DONE_VERIFIED | Exact-tree CI clean checkout, restore, build, Compose startup and smoke. |
| A-W16-08 | Final tag/evidence/checksum verification | PARTIAL | Evidence checksums recorded; final tag is not authorized or created. |
| A-W16-09 | Backup trace/screenshots/raw state | PARTIAL | Historical A evidence retained; no new W16 raw-state bundle. |
| A-W16-10 | Clean release smoke | DONE_VERIFIED | Integrated platform-smoke and release overlay checks; A's PowerShell gate was not re-executed natively (no host SDK). |

## Member B

| Task | Required output | Status | Evidence / adjudication |
| --- | --- | --- | --- |
| B-W16-01 | Re-run release CON-01/REL-01 evidence | DONE_VERIFIED | Three real-provider trials; source-bound B release evidence. |
| B-W16-02 | Rehearse live contention 3x | DONE_VERIFIED | Three trials, 10 contenders, one winner, nine conflicts, one reservation/Outbox effect. |
| B-W16-03 | Rehearse reserve-response-loss | DONE_VERIFIED | Each trial: commit+intent, UNKNOWN, no D2, same-R reconcile, final agreement. |
| B-W16-04 | Rehearse Redis stale-event explanation | DONE_VERIFIED (technical) | Production Function replay rejects stale version/sequence; spoken drill remains human. |
| B-W16-05 | Oral defense drill | PARTIAL | All 45 five-field answers and proof links prepared; timed human drill not performed. |
| B-W16-06 | Review A/C demo handoff | PARTIAL | Runbook/handoff table prepared; actual A/C sign-off pending. |
| B-W16-07 | Final performance table validation | DONE_VERIFIED | Claims validator PASS; 174 digest checks; accepted populations retained. |
| B-W16-08 | Release tag/evidence verification | PARTIAL | Evidence and source verification complete; release tag not created. |
| B-W16-09 | Clean smoke | DONE_VERIFIED | Fresh empty-volume rehearsals and teardown; integrated CI smoke. |

## Member C

| Task | Required output | Status | Evidence / adjudication |
| --- | --- | --- | --- |
| C-W16-01 | Release platform smoke | DONE_VERIFIED (team gate) | Integrated platform-smoke passes on the exact tree; C's PowerShell gate was not re-executed natively (no host SDK/pwsh). |
| C-W16-02 | Rehearse REL-06 commit-before-ACK 3x | PARTIAL | Deterministic REL-05/06/W11 evidence retained; no three new live crash-before-ACK rehearsals. |
| C-W16-03 | Rehearse Payment timeout-after-effect | PARTIAL | Payment tests and integrated payment smoke cover the behavior; no three W16 rehearsals. |
| C-W16-04 | Rehearse poison/DLQ proof | PARTIAL | W11 fault-platform/DLQ evidence retained; no W16 rehearsal. |
| C-W16-05 | Rehearse quorum evidence explanation | PARTIAL | W14 G5 real-provider quorum evidence retained; human explanation pending. |
| C-W16-06 | Oral defense drill | NOT_STARTED | Human drill cannot be manufactured. |
| C-W16-07 | Review A/B handoffs | NOT_STARTED | Human sign-off pending. |
| C-W16-08 | Final tag/config/evidence verification | PARTIAL | Config/evidence verified; final tag not created. |
| C-W16-09 | Backup trace/screenshots/raw broker state | PARTIAL | W14 raw broker evidence retained; W16 mock traffic is a presentation aid. |
| C-W16-10 | Final secret/release-profile scan | DONE_VERIFIED | Scanner PASS; release overlay positive render and fail-closed negative render. |

## Team W16

| Task | Required output | Status | Evidence / adjudication |
| --- | --- | --- | --- |
| Release | Tag final candidate | DEFERRED_BY_CANONICAL_SCOPE | Candidate prepared; tag/GitHub Release requires separate project-lead authorization. |
| Release | Clean clone | DONE_VERIFIED | Exact-tree CI fresh checkout; local clean worktree. |
| Release | Migrations from empty DB | DONE_VERIFIED | Platform-smoke boots a fresh Compose project and services create/migrate their databases. |
| Release | All G5 tests | DONE_VERIFIED (preservation) | W15/W16 add no runtime product source; accepted W14 G5 evidence remains applicable and integrated CI re-verifies G4 journeys. |
| Release | Secret scan | DONE_VERIFIED | Scanner PASS on exact candidate; CI job PASS. |
| Release | README commands | DONE_VERIFIED | CI clean checkout executes the documented build/Compose path. |
| Release | Evidence links | DONE_VERIFIED | W14/W15/W16 bundles and member reports linked. |
| Demo | Three full rehearsals | PARTIAL | Automated technical rehearsals executed (B 3x plus integrated gate); three actual human team rehearsals PENDING. |
| Demo | Live demo ownership | PARTIAL | A/B/C demo artifacts prepared; human run-of-show pending. |
| Demo | Final release tag | DEFERRED_BY_CANONICAL_SCOPE | Not created; no release authorization. |

## Cross-owner release gate

| Requirement | Status | Evidence |
| --- | --- | --- |
| Trip ↔ Payment operation identity | DONE_VERIFIED | Envelope and payload carry the same operationKey; Payment consumes it; AUD-05 checks one effect per key. |
| Saga ↔ Driver/Matching reservation | DONE_VERIFIED | Integrated core-runtime scenarios; B three-trial contention/UNKNOWN proof. |
| Driver lifecycle ↔ Trip guarded transitions | DONE_VERIFIED | Controlled production lifecycle handlers + AUD-04; W11/W12 source-bound tests. |
| Outbox/Inbox ↔ local transactions | DONE_VERIFIED | Pending authorization assertion, confirmed publication, duplicate/ACK evidence; W14 G5. |
| Identity/Gateway ↔ protected endpoints | DONE_VERIFIED | 55 real HTTP boundary checks; seven dedicated Identity negative cases. |

Journey mapping: happy = integrated completed scenario; cancellation/compensation
= integrated cancellation/void scenario; decline = integrated terminal-decline
scenario; capture recovery and payment response loss = Payment smoke/tests;
UNKNOWN reservation = B three-trial real-provider harness; crash/restart/
redelivery = W14 G5 and W11 source-bound evidence, whose runtime source is
unchanged by W15/W16.

## Acceptance adjudication

Source acceptance: W16 candidate is a docs/evidence/scripts/test delta on
W15-verified main with no runtime product change. Contract and journey evidence
is sufficient for the automated technical release gate.

Outstanding gates: three actual human team rehearsals, oral defense, A/C peer
sign-off and separate release-tag authorization. These are final submission
gates, not unverified runtime code.
