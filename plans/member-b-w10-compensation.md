# Member B W10 Phase 2

Outcome: finish B-owned exact compensation on the existing W10 branch and PR #39,
with production A Release compatibility, executable authority assertions, independent
review, separate implementation/evidence commits, normal push and exact-head CI.

Constraints: preserve Phase 1 and W7/W9 history; no reset/rebase/force push, main
mutation, unrelated merges or A/C production changes. No Saga or Payment implementation.
Acceptance: the requested compensation matrix, affected Release build/test suites,
review with no unresolved B-owned P0/P1, evidence and Ready for Review PR updated.
Full REL-09 requires genuine persisted A Saga and C terminal Payment integration;
an injected payment fixture cannot satisfy it.

## Preflight and contract research — 2026-10-25 Asia/Saigon

W10 clean at 2d9a3032978296246b172b53001123c22015d7fd. W9 66d7d1c and
C 7a0575a are verified ancestors. PR #39 targets W9; #37 and A #36 target
C; C #35 targets main. All open. W10 prior CI 40174663715 succeeded; no
reviews/comments submitted, A/minhqng and C/Nerduckk requested.
Native gh located but unauthenticated; connected GitHub is the available fallback.

A #36 head 5f61cff4fc7a6dd1c178c7ffac3d6709369f7bf3 production
TripOutboxIntents.ReleaseReservation emits exact driverId/tripId/reservationId
and positive matchingRound, stable trip:...:release:... messageId and operationKey.
Its Trip intake confirms only an exact owned Driver/reservation cleanup obligation.
Its W9 producer explicitly excludes W10 Saga/Payment execution. No Saga production
files exist on that head or the B base. Broader live branch inspection found A
W10 PR #40 at e469fda1e36e6072d887b028f0726f7365aaeafd: persisted Saga and
compensation exist there; Release builder is unchanged. C Payment command consumer
and outcome publisher are absent from every live remote branch, including A W10.
Full REL-09: EXTERNAL GATE — NOT RUN. A W10 was source-reviewed, not merged.

Missing B guarantees: round is fingerprinted but not validated/fenced; AVAILABLE
returns unproven Replayed. Require positive round for broker Release, compare it
to locked RESERVED provenance, reject missing legacy provenance without guessing.
Preserve HTTP W7 semantics. Same-message redelivery returns its durable receipt;
a fresh command without current provenance must require reconciliation, never
claim compensation success from availability or ACK alone. No schema change needed.

## Milestones

- [x] Audit canonical Docs 01/02/03/05A/05B, owners and live Git/PR state.
- [x] Implement minimal round/state guard and canonical A production contract tests.
- [x] Run real PostgreSQL/RabbitMQ compensation and affected regressions/build.
- [x] Independent fresh-context review; repair and rerun affected checks.
- [x] Prepare final evidence separately from the implementation commit.
- Publication/normal push, exact-head CI and human review follow-up are recorded
  on [PR #39](https://github.com/minhqng/ride-hailing-microservices-system/pull/39),
  the live delivery ledger. No merge is authorized.

## Scenario coverage

One bounded pass. High severity: malformed/missing round; exact/duplicate release;
wrong round/driver/trip/token; stale release after new reservation; release vs Start;
ON_TRIP/OFFLINE/AVAILABLE/missing Driver; legacy round null; receipt/outbox failure;
commit-before-ACK; relay publish-before-mark; broker/DB restart; exact Trip cleanup;
late DriverReserved after terminal cleanup. Each maps to a DB state/version,
receipt and required event assertion, not a broker ACK assertion.
Relevant dimensions: inputs, timing, state, environment/restart, error cascades,
integrity, integration, business rules and exact-token authorization. Actor roles,
UI/device/accessibility and compliance are skipped because this is internal service
compensation with no changed user roles, UI, personal data or retention contract.
Scale testing is excluded: bounded deterministic concurrency proves the requested
locking invariant; throughput is not an acceptance criterion.

Rollback: suspend Driver command ingress using existing CommandsEnabled flag and
restore previous application; retain durable receipts/outbox. Legacy missing round
requires A/B authority reconciliation; never infer a historical round.

## Review repair

Fresh independent review found one P1: enforcing the new round syntax before
receipt lookup prevented exact committed Phase1 three-field Release replay.
The failing upgrade regression reproduced it. Parsing now retains its original
semantic fingerprint, receipt lookup precedes new execution validation, and
only an identical committed receipt bypasses round requirements. A fresh roundless
command is rejected before mutation. Historical receipt replay leaves a newer
reservation untouched. Final build and all seven suites are rerunning after repair.
The scoped simplifier replaced test scenario setup conditionals with a switch;
no proof/assertions were removed. Final selected suites: 930 PASS, 0 FAIL/SKIP;
focused compensation 16 PASS; Release build PASS with one pre-existing warning.
Independent re-review: no remaining P0/P1. Implementation committed as
87b639f5c471861129f05811968e92a3090a5ce7. Evidence/publication follows separately.
