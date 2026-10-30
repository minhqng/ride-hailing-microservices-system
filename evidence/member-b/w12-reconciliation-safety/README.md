# Reservation-reconciliation safety verification

## A. Live topology and continuation boundary

OpenCode stopped after committing the repair as `6e59983`, with a clean
`D:/rh-w12-fix` worktree on `feature/B-W12-matching-recovery`. Its evidence
still described the earlier production-worker repair. The reviewed baseline
is `160e2f3897d00ed6fcfe5121e81bdb5cae7ed5d2`; the original W12 layer is
`4aea3f5`, and the production-worker implementation is `b4cf93c`.

Live discovery found remote main at `86062af`, local main at `ba5bb40`, W11/PR
#44 at `3ba3724`, and PR #45's actual head branch
`feature/B-W12-observability-g4` at `160e2f3`. The separate remote recovery
branch also pointed to `160e2f3`. No main or W11 history was changed.

The repair and strengthened proofs are forward commits: `6e59983` and
`ea6b650cda90dd30ccf19f42e6e369d59d27b8d9`. The latter is the final executable
source; its tree is `f903ec135ff8d26ae5a922bbb1a9106595b691ed`. Subsequent bundle
changes contain evidence only. No A/C-owned production files were changed.

## B. Canonical safety requirements

Authority was reconstructed from Docs 00–03, Doc 04's review/G4 ownership, and
Doc 05B's W11/W12 obligations. Doc 01 DSI-001/003/004/005 preserve Driver
exclusion, legal OCC transitions, exact release fencing and same-R idempotency.
DSI-017/018/031 prohibit another candidate while the original attempt is
unresolved and require its original identity. DSI-030/033 retain Trip authority
and exact cleanup. DSR-007/008 and Doc 02 §12.4–12.9 require write-ahead intent,
same-reservation recovery, durable duplicate convergence, atomic terminal
Outbox effects and reconciliation during cancellation. REL-01/02 require real
lost-response/restart evidence.

An authoritative negative is a fencing decision, not a status-code guess.
AVAILABLE requires a returned version greater than the durable maximum
dispatched fence; an incompatible state requires the existing fenced
predicate. Missing fence provenance stays unresolved. Historical ownership
must be checked before interpreting current status as incompatible.

## C. L1 reproduction and cause

The final safety test source was copied onto an isolated archive of
`160e2f3`, without altering the review worktree. The matrix failed 10/13 there
and passed 13/13 on corrected source. All four STARTED/COMPLETED ×
IN_FLIGHT/UNKNOWN cases enter the round executor directly. On old source,
assertions observed a different winning reservation: execution advanced to D2.

`MarkTripStarted` clears live reservation references; completion leaves the
Driver AVAILABLE. The durable lifecycle effect retains the original identity.
Old reservation queries ignored that history, and reserve recovery interpreted
`STALE_RESERVATION` as rejection. Thus a successful, consumed reservation could
be rejected locally and a second Driver reserved.

## D. L2 reproduction and cause

Old `HttpDriverClient` interpreted bare version-read 404 as a missing Driver
and bare Reserve 404 as a domain rejection. The new post-commit HTTP proof
sends a real successful Driver POST, then substitutes only its response with a
bare 404. Old source reaches a false terminal outcome. Corrected source keeps
UNKNOWN/PROCESSING, leaves D2 untouched, and recovers D1/R by a real query.
Exactly one POST and one reservation Outbox effect are asserted.

## E. Identity-aware design

The smallest repair reuses the existing lifecycle ledger and reservation query
vocabulary. It reconciles the original identity before replay and treats
insufficient negative evidence as durable uncertainty.

## F. Driver-side changes

Driver queries now read current state and exact `(driver_id, reservation_id)`
STARTED/COMPLETED history in one SQL snapshot. A matching history entry returns
the existing ownership outcome even after lifecycle advancement or a later
reservation. No status-only or trip-only ownership inference is used.

Lifecycle rows contain Driver, Trip, reservation, phase, version and event
identity; they do not need a new matchingRound column. Matching's immutable
attempt supplies Trip/round/Driver lineage, mints reservation IDs internally,
and globally enforces `UNIQUE(reservation_id)` before dispatch. The established
Driver query contract is Driver+reservation (Doc 02 §12.6). This combination
provides exact supported identity without a schema or cross-service API
redesign. Manual identity corruption/retention that defeats those constraints
is outside this proof.

## G. Matching-side changes

Matching queries before replay for UNKNOWN and for IN_FLIGHT with a committed
dispatch fence. Same-R authoritative ownership converges attempt and round;
unavailable/inconclusive evidence preserves ambiguity. `STALE_RESERVATION`
persists `RESERVE_STALE_IDENTITY`, never universal no-effect rejection.
Fresh, never-dispatched missing Drivers remain conclusively rejectable using
the structured Driver-domain response. Bare 404s at every relevant boundary
remain transport ambiguity. Existing OCC versions, release fencing, stages,
cancellation, message identities and transaction/Outbox boundaries are retained.

## H. Demo/production isolation

Production intake locks the existing round and refuses a demo/test-origin
`auto_execute = FALSE` row with `MATCHING_ROUND_ORIGIN_CONFLICT`. It neither
overwrites identity/pickup nor adopts seeded attempts. Such rows are ineligible
for production scanning. Production duplicates retain the first authoritative
identity and pickup. Existing demo mutation paths refuse production-owned
rounds. Real PostgreSQL controls prove demo-first rejection, production-first
duplicate convergence, preserved Testing usage and zero Driver/Outbox effects
from rejected contamination. Shared JWT/Gateway authentication is separate.

## I. L4/L5/L6 disposition

- L4: initial committed stage-snapshot identities now increment
  `matching.attempt.created_total`. Frozen replay/healing does not count new
  logical attempts. The production stage-snapshot persistence method has
  a direct PostgreSQL/MeterListener proof. Metrics are best-effort after commit; export is
  neither a transaction participant nor a durable audit ledger.
- L5: migration 007 replaces only the known legacy
  `matching_attempts_state_check`; unrelated state CHECK constraints survive
  two complete reapplications. Valid CANCELLED and invalid-state controls
  verify the replacement vocabulary. Empty and upgrade migration tests pass.
- L6: verification against committed Git blobs found two stale README hashes
  in the earlier bundles. The final bundle repairs those entries and verifies
  every listed artifact. Hash manifests exclude themselves; raw output stays
  ignored. New evidence is bound to the tested code commit, not a circular
  assertion about its own future evidence commit.

## J. L7/L8 operational disposition

A missing persisted stage is exposed as `STAGE_CONFIGURATION_MISMATCH`; the
round and attempts are retained. Restore configuration containing the original
persisted stage/policy; do not rewind stage or replace its snapshot.
`RECONCILE_EXHAUSTED` retains UNKNOWN with durable error/check timestamps and
worker backoff (`RECONCILE_PENDING`). Restore Driver/query connectivity and
inspect the same identity/fence, then allow authoritative recovery. Do not
manually reject or erase an unresolved attempt. A new terminal review state
requires a canonical decision and was not invented. Finite per-pass polling
does not promise automatic success during permanent dependency loss.

## K. Authoritative regression matrix

All ownership assertions use real Driver and Matching PostgreSQL persistence.
HTTP cases use real Kestrel/HttpDriverClient with controlled boundary faults.
Scenario exploration focused on timing, state transitions, environment/config,
error cascades, authorization, data integrity, integration and business rules.
User-type and input-extreme coverage is inherited from the existing HTTP
suites; no actor or input contract was added. Scale/benchmark and compliance
changes are outside this repair; no evidence of those properties is inferred.

| Scenario | Observed invariant / proof |
|---|---|
| IN_FLIGHT + STARTED, same R | Original D1/R wins; D2 AVAILABLE; one reserve/lifecycle effect |
| UNKNOWN + STARTED, same R | Same identity recovered; no D2 or false terminal |
| IN_FLIGHT + COMPLETED, same R | Historical ownership wins despite AVAILABLE live row |
| UNKNOWN + COMPLETED, same R | Original winner retained; no D2 |
| ON_TRIP under another Trip/R | Original R is never reported owned; fenced negative allows legitimate D2 |
| Existing RESERVED replay | Same result/identity, one logical effect |
| Committed IN_FLIGHT + version/replay 404 | Query precedes replay; no second Reserve POST or D2 |
| Real committed POST + response replaced by bare 404 | UNKNOWN/PROCESSING, then same-R recovery; exactly one POST/effect |
| Fresh version-read bare 404 | Unresolved, no D2, no MatchingFailed |
| Fresh Reserve bare 404 | UNKNOWN, no D2, no false terminal |
| Fresh real missing Driver | Structured negative advances to real D2 (positive control) |
| Reservation query unavailable | UNKNOWN/exhaustion remains durable; no fabricated ownership/rejection |
| Concurrent same-R recovery | One logical winner, stable identity and effect count |
| Real Matching kill/restart after STARTED or COMPLETED | Both process cases preserve original R, D2 READY/AVAILABLE, lifecycle and Outbox effects |
| Production worker competition, stage and cancel | Existing real-process/broker stage/cancellation regressions pass |
| Demo origin, metric and repeated migration | Origin/effect, exact creation counts and sentinel CHECK controls pass |

Oracles assert Driver rows, lifecycle effects, Matching attempts/round and
reservation/terminal Outbox counts. Real process tests cover two lifecycle
phases in addition to the 13-case component/HTTP matrix.

## L. Fresh executable verification

See `test-output.txt` and `manifest.json` for exact source, commands, counts,
environment and PASS/FAIL/NOT RUN classifications. Old-source 10/13 failures
are expected discrimination evidence, not failures of the corrected source.
The first concurrent full solution run exposed a pre-existing shared Platform
test race at `MessagingMetricsTests.cs:59` (callback writes a List while
assertions enumerate it). Its isolated test passed 1/1 and the separate full
Platform rerun passed 60/60. This initial failure is retained, not reported as
an unqualified green full-solution run. That C-owned file is unchanged from
the reviewed baseline; no A/C production correction was made here.

Identity's missing auth fixture is NOT RUN (one skip), and SystemTests is
NOT RUN (zero discovered). A full Trip+Driver+Matching host journey is NOT RUN:
there is no executable system scenario in the current suite. Trip integration
and the independently authoritative real Driver/Matching process oracle are
reported separately; neither is presented as a full-host end-to-end proof.

## M. Final W11 five-pass campaign

PASS: five consecutive passes on clean source `ea6b650cda90dd30ccf19f42e6e369d59d27b8d9`, completed `2026-10-30T02:04:25.518Z`. REL-01, REL-02, CON-02, CON-04, REL-12, REL-13 and REL-14 each passed 5/5 (35 required cells). The campaign rebuilt the final images; no previous five-pass result was reused. The matrix and identities are in `w11-five-pass/`; authoritative final rows/Outbox and actual killed/restarted process identities are in `final-state/w11-reservations.json`. The two production STARTED/COMPLETED process proofs are additional integration tests; this W11 campaign retains its established Testing-only reserve trigger.

## N. Failure-window assessment

| Window | Truth / durable identity | Allowed result, blocking rule and restart evidence |
|---|---|---|
| Driver commit before response | Driver row+Outbox; exact R and Matching dispatch fence | UNKNOWN until authoritative query; D2 blocked; same attempt survives |
| STARTED before Matching success | Driver lifecycle transaction; Driver/Trip/R/STARTED | Historical ownership resolves RESERVED locally; no replay or D2 |
| COMPLETED before recovery | Durable STARTED/COMPLETED history | Original result still owned; AVAILABLE alone cannot negate it |
| Broker redelivery after advancement | Original round/attempt and R | Resume immutable identities; no new candidate sequence |
| Bare 404 on version/reserve/query | Transport lacks domain provenance | Keep unresolved; structured fresh absence only is conclusive |
| Competing production workers | PostgreSQL per-round session lease plus attempt ledger | Serial round work; same-R duplicates converge; process loss releases lease |
| PostgreSQL interruption | Service-local transactions and durable intent/fence | Failed persistence authorizes no send; ambiguous remote work is recovered after DB return |
| Matching restart | IN_FLIGHT/UNKNOWN and maximum dispatched version | Query original R; durable work scan retries, never starts D2 while unresolved |
| Cancellation vs lifecycle | Cancel tombstone/round flag and Driver lifecycle | Query-only recovery; committed ownership survives; Trip owns exact cleanup |
| Delayed events/stale versions | Version guards, exact lifecycle receipts and release token | No regression or stale release; one authoritative local Outbox effect |

The PostgreSQL-interruption row is a source/transaction-boundary assessment,
not a newly executed database-partition campaign. Existing persistence/fence
tests and real lost-response/restart tests provide the stated component proof.
No unsupported database outage experiment is claimed.

## O. Independent adversarial review

A separate read-only reviewer inspected the complete `160e2f3..6e59983` delta
and then the strengthened test/runbook delta. No confirmed production-reachable
BLOCKER/HIGH or production-authority MEDIUM remained. Two initial concerns were
retested against source and excluded: distinct ordinals cannot be dispatched
concurrently by the production path because its only dispatcher holds the
round lease throughout; reused reservation identity across Trips is excluded
by internal generation plus the durable globally unique attempt index before
dispatch. Unleased reserve endpoints are Testing-only. The review was static;
the controller executed the containerized tests. Human A/C review and peer
rerun remain required by Doc 04 and are not replaced by this review.

## P. Shared dependencies and G4

SEC-AUTH-01 (critical unauthenticated mutations), SEC-AUTH-02 (actor spoofing),
SEC-AUTH-03 (internal endpoint/service identity), and SEC-AUTH-04 (object-level
authorization) remain OPEN under the existing
`../w12-observability-g4/security/auth-remediation-handoff.md` ownership. These
require A's principal contract, C's Gateway/network boundary, B's subsequent
enforcement and lead approval. Trip assignment/cleanup/Saga acceptance and
Payment/platform acceptance stay with A/C. The shared Platform test race and
required peer rerun are recorded separately. Team G4 remains BLOCKED.

## Q. Git, authorship and delivery

Existing repair authorship was preserved. The source and test commits record
the locally configured implementation identity `minhqng`; PR creator
`Fried-toofuu` was not substituted as author. No history was rewritten.
Delivery targets PR #45's existing head branch only; no duplicate push to the
separate remote recovery branch is needed. PR #44/#45 remain open/unmerged.
The PR body/title must reflect the production worker, stages, this safety
repair, actual checks and shared G4 blockers. CI zero checks is NOT green.

## R. W13 readiness

The B-owned reservation correctness baseline is suitable for technical
preparation after the recorded proof and review gates. Shared G4 release and
human review dependencies remain blocked. No E01/E02/E03, W14 E04/REL-16,
benchmark, W13/W14 branch or PR merge was started.

## S. Final verdict

B RESERVATION RECOVERY SAFETY GREEN — SHARED G4 GATES STILL BLOCKED
