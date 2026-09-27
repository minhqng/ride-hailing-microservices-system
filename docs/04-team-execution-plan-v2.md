# DS03 DISTRIBUTED RIDE-HAILING SYSTEM
## Team Execution Plan · 16-Week Delivery · Ownership · Gates · Integration · Evidence

> **Document:** `docs/04-team-execution-plan.md`  
> **Course:** Distributed Systems  
> **Topic:** DS03 — Ride-hailing Microservices System simulating Grab/Uber  
> **Type:** Team Execution Plan + Delivery Governance + Integration/Review Contract  
> **Status:** **PROPOSED FREEZE v2.0 — REVIEW CANDIDATE**  
> **Baseline date:** **2026-09-26**  
> **Team size:** **3 members**  
> **Planning horizon:** **16 weeks**  
> **Parents:**  
> `00-project-scope-architecture.md` — scope/architecture/technology/failure model  
> `01-domain-correctness-contract.md` — domain states/invariants/race semantics  
> `02-distributed-workflow-engineering.md` — messaging/recovery/Outbox/Saga/retry semantics  
> `03-verification-experiments-demo.md` — proof catalog, experiments, evidence, demo  
> **Children:**  
> `05A-member-a-trip-workflow.md`  
> `05B-member-b-driver-matching.md`  
> `05C-member-c-payment-messaging-platform.md`  
> **Normative role:** source of truth for team ownership, 16-week chronology, integration order, issue taxonomy, dependency graph, cross-review rules, release gates, evidence deadlines, GitHub workflow, workload accounting, risk handling, scope-cut order and report/demo preparation.  
> **Not source of truth for:** business semantics (`01`), reliability semantics (`02`), exact verification meaning (`03`). If schedule conflicts with correctness, Docs 00–03 win.  
> **Execution principle:** implementation is organized around **proof-bearing vertical increments**, not around “finish all code first, test later.” Every risky distributed mechanism must become executable, breakable and reviewable in the same phase in which it is introduced.

> **Team north star.**  
> **By Week 16, all three members must be able to start the system from a clean clone, explain the end-to-end distributed workflow, reproduce at least one race/failure proof, inspect authoritative final state, and defend why the system remains correct under concurrency, duplication, timeout and partial failure.**

---

# 0. Executive plan

## 0.1 Team structure

### Member A — Trip / Workflow / Saga

Primary ownership:

```text
Trip Service
FareSnapshot
Trip FSM
Create/Cancel/Accept/Start/Complete
Trip assignment semantics
Trip Outbox
persisted Saga
compensation
Trip-side idempotency
workflow recovery
```

Secondary/cross-cutting:

```text
domain consistency
architecture coherence
report architecture/correctness sections
invariant auditor design
```

---

### Member B — Driver / Matching / Concurrency / Performance

Primary ownership:

```text
Driver / Vehicle / Location Service
Driver FSM
authoritative reservation
OCC / conditional write
reservation fencing
Redis GEO projection
Matching Service
MatchingRound / MatchingAttempt
UNKNOWN reconciliation
candidate ranking
contention proofs
performance experiments
```

Secondary/cross-cutting:

```text
concurrency methodology
load generation
final-state correctness under load
```

---

### Member C — Payment / Messaging / Platform / Reliability

Primary ownership:

```text
Payment Service
Payment idempotency
RabbitMQ topology
MessageEnvelope
publisher confirms
manual ACK
consumer dedup
bounded retry / DLQ
shared Outbox relay support
Notification worker
OpenTelemetry
Compose/bootstrap
faultpoint framework
broker HA/reconnect validation
security/release infrastructure
```

Secondary/cross-cutting:

```text
CI/release
evidence packaging
message/retry review
```

---

## 0.2 Why this ownership split is balanced

The split follows **authority and failure boundaries**, not UI/business feature count.

A's difficulty is:

```text
long-running workflow correctness
```

B's difficulty is:

```text
concurrency + stale read + unknown outcome
```

C's difficulty is:

```text
delivery semantics + idempotent side effects + infrastructure recovery
```

These are comparable in distributed-systems depth even if line counts differ.

---

## 0.3 Physical effort envelope

Recommended focused engineering envelope:

| Member | Core hours | Average/week | Heavy weeks |
|---|---:|---:|---|
| **A** | ~146 h | ~9.1 h | W8–W12 |
| **B** | ~148 h | ~9.25 h | W6–W13 |
| **C** | ~150 h | ~9.4 h | W8–W14 |
| **Team** | **~444 h** | **~27.8 h/week** | W8–W13 |

This excludes:

- lecture/study time unrelated to project;
- general report formatting;
- optional P1 after Core Gate.

It includes:

- design reading;
- implementation;
- tests;
- review;
- evidence;
- report/demo preparation.

---

## 0.4 Phase model

| Phase | Weeks | Main question |
|---|---|---|
| **P0 Freeze/Foundation** | W1–W2 | Do we agree on the architecture and can we boot it reproducibly? |
| **P1 Business Skeleton** | W3–W5 | Do authoritative services/states exist on real storage? |
| **P2 Concurrency + Messaging** | W6–W8 | Can races and asynchronous delivery be made correct? |
| **P3 Durable Workflow** | W9–W10 | Can committed work survive process/broker boundaries? |
| **P4 Fault Proof + Observability** | W11–W12 | Can we break exact windows and prove recovery? |
| **P5 Experiments + HA Validation** | W13–W14 | Can we measure scalability/recovery honestly? |
| **P6 Freeze/Report/Defense** | W15–W16 | Can another reviewer reproduce and defend the system? |

---

# 1. Delivery philosophy

## 1.1 Vertical proof over horizontal completeness

Bad semester plan:

```text
W1–W6 code every service
W7 connect everything
W8–W12 debug
W13 testing
W14 report
```

This delays discovery of architecture mistakes until integration.

Preferred:

```text
build one small authoritative path
→ integrate
→ force failure/race
→ prove invariant
→ expand
```

Example:

```text
Driver AVAILABLE
→ ReserveDriver
→ concurrency proof
```

exists before full payment/notification polish.

---

## 1.2 Risk-first sequencing

The plan deliberately implements hardest correctness risks before optional breadth.

Priority:

```text
1. exclusive Driver reservation
2. UNKNOWN reservation outcome
3. duplicate/ACK/confirm semantics
4. Outbox
5. Saga/compensation
6. stale projection
7. restart/recovery
8. performance
9. optional P1
```

If the team can prove these, the project already demonstrates strong distributed-systems depth.

---

## 1.3 Test accompanies mechanism

When introducing:

```text
OCC
```

also introduce:

```text
CON-01 / CON-02 harness
```

When introducing:

```text
manual ACK
```

also introduce:

```text
REL-06 commit-before-ACK fault
```

When introducing:

```text
Outbox
```

also introduce:

```text
REL-04 / REL-05
```

Never schedule all verification at the end.

---

# 2. Team operating model

## 2.1 GitHub issue hierarchy

Recommended hierarchy:

```text
Milestone / Week
  → Epic issue
      → sub-issues
```

Example:

```text
W06 — Driver concurrency gate
  ├─ B-W06-01 Reserve conditional mutation
  ├─ B-W06-02 Same-reservation idempotency
  ├─ B-W06-03 DB defense constraint
  └─ B-W06-04 CON-01 harness/evidence
```

GitHub currently supports sub-issues, nested issue hierarchies and issue dependencies, which is useful for representing this execution plan directly in the repository tracker.

---

## 2.2 Required project fields

Recommended GitHub Project fields:

```text
Status
Owner
Week
Phase
Priority
EstimateHours
ActualHours
Risk
Invariant
TestScenario
Reviewer
Evidence
BlockedBy
```

GitHub Projects supports custom number/date/single-select/iteration fields and parent/sub-issue progress, so the team can track effort and evidence without maintaining a second spreadsheet.

---

## 2.3 Status values

Use only:

```text
Backlog
Ready
In Progress
In Review
Evidence
Done
Blocked
Cut
```

Avoid vague:

```text
almost done
testing later
```

---

## 2.4 Priority

```text
P0-BLOCKER
P0
P1
P2
```

`P0-BLOCKER` means:

- current release gate cannot pass;
- all optional work stops.

---

## 2.5 WIP limit

Per member:

```text
max 2 implementation issues In Progress
```

Team-wide:

```text
max 1 unresolved architecture decision affecting a critical path
```

Reason:

High WIP causes:

- half-integrated services;
- stale branches;
- delayed reviews;
- hidden blockers.

---

# 3. Git and PR governance

## 3.1 Branching

Recommended lightweight flow:

```text
main
feature/<issue-id>-<short-name>
fix/<issue-id>-<short-name>
experiment/<id>-<short-name>
```

Avoid long-lived:

```text
member-a
member-b
member-c
```

branches because integration divergence becomes severe.

---

## 3.2 Main branch protection

Recommended:

```text
pull request required
at least 1 approving review
required CI checks
conversation resolution
no force push
```

GitHub branch protection currently supports required reviews and required status checks before merge.

For only three people, one independent reviewer provides better knowledge sharing than self-merge.

---

## 3.3 Critical mechanism requires specific reviewer

| Area changed | Author | Required reviewer |
|---|---|---|
| Trip assignment/Saga | A | B or C depending boundary |
| Driver reservation/OCC | B | A |
| Matching UNKNOWN | B | A + C if retry affected |
| Message envelope/RabbitMQ abstraction | C | A or B |
| Outbox relay | C/shared | A |
| Payment idempotency | C | A |
| performance harness | B | A/C methodology review |
| invariant auditor | shared | at least 2 people |

---

## 3.4 PR evidence template

Every nontrivial PR includes:

```text
Issue:
Docs:
Invariant(s):
Failure window:
What changed:
What must remain impossible:
Tests:
Evidence:
Reviewer focus:
Out of scope:
```

Example:

```text
Invariant:
DSI-004 guarded release

Failure window:
old Release(R1) arrives after new R2

Test:
CON-04

Evidence:
evidence/CON-04/run-...
```

---

## 3.5 CI status

A critical PR cannot merge with:

- red tests;
- known flaky S0/S1 test;
- missing migration;
- architecture-sensitive unreviewed semantic change.

GitHub workflow artifacts can retain logs, test outputs and performance artifacts after CI jobs complete, making them suitable for proof attachments.

---

# 4. Definition of Ready

An implementation issue is `Ready` only when:

```text
[ ] canonical doc section exists
[ ] owner is clear
[ ] reviewer is clear
[ ] authority is clear
[ ] input/output identity is clear
[ ] expected legal/conflict/duplicate behavior is clear
[ ] dependencies are available or fixture exists
[ ] test/proof is named
```

If any semantic item is unknown:

```text
architecture/design issue first
```

not coding by assumption.

---

# 5. Definition of Done

## 5.1 Ordinary implementation issue

```text
[ ] code complete
[ ] local tests
[ ] integration test where boundary matters
[ ] logs/metrics safe
[ ] docs/contracts still match
[ ] PR review complete
[ ] no hidden TODO in P0 path
```

---

## 5.2 Distributed mechanism issue

Additionally:

```text
[ ] failure/race case implemented
[ ] duplicate behavior verified
[ ] restart/recovery considered
[ ] final authoritative state asserted
[ ] evidence stored
```

---

## 5.3 Week complete

A week is green only when:

```text
exit criteria
+
required evidence
+
cross-team integration
```

are complete.

“Code mostly written” does not close a week.

---

# 6. Ownership matrix

## 6.1 Service ownership

| Component | Primary | Reviewer | Backup understanding |
|---|---|---|---|
| Identity | Shared/A | C | B |
| Gateway | C | A | B |
| Trip Service | A | B | C |
| Driver Service | B | A | C |
| Matching Service | B | A | C |
| Payment Service | C | A | B |
| Notification Worker | C | A | B |
| RabbitMQ platform | C | A/B | all |
| Redis projection | B | C | A |
| Saga | A | C | B |
| invariant auditor | Shared | all | all |
| performance | B | A/C | all |
| fault framework | C | A/B | all |

---

## 6.2 Correctness ownership

| Invariant family | Owner | Required second reviewer |
|---|---|---|
| DSI-001..007 Driver/location | B | A |
| DSI-008..014 Trip/fare | A | B |
| DSI-015..023 Matching | B | A |
| DSI-024..028 Payment/order | C/A shared | A/C |
| DSI-029 Notification | C | A |
| DSI-030..035 cross-service | A/B/C shared | ≥2 |
| DSL-001/003 Matching progress | B | A |
| DSL-002 cleanup | A+B | C |
| DSL-004 Payment resolution | C | A |
| DSL-005 Saga | A | C |
| DSL-006 Outbox delivery | C+A | B |
| DSL-007 notification non-blocking | C | A |

---

# 7. Critical dependency graph

```text
Architecture freeze
  ↓
service skeleton / DB ownership
  ↓
Driver FSM ──────→ Driver OCC
  │                 ↓
  │              Matching reserve/reconcile
  │                 ↓
Redis projection → MatchingRound
                    ↓
Trip booking → RideRequested
                    ↓
          RabbitMQ + message envelope
                    ↓
             Outbox + dedup
                    ↓
Payment FSM/idempotency
                    ↓
          persisted Trip Saga
                    ↓
          deterministic fault proof
                    ↓
      observability + invariant auditor
                    ↓
           performance / HA
                    ↓
          final demo/report
```

---

## 7.1 Critical path

The highest-risk critical path is:

```text
Driver OCC
→ Matching UNKNOWN
→ Rabbit delivery semantics
→ Outbox
→ Saga compensation
→ deterministic fault evidence
```

If any of these slips by more than one week:

- freeze P1;
- reduce cosmetic work;
- reassign reviewer time;
- preserve W15–W16 release/defense buffer.

---

# 8. Cross-member handoff contract

## 8.1 A → B

A provides:

```text
TripSnapshot
matchingRound semantics
assignment identity
Cancel behavior
```

B must not invent Trip states.

---

## 8.2 B → A

B provides:

```text
Driver reserve result
reservationId
DriverReserved authoritative event
MatchingFailed
```

A must not infer assignment from Redis.

---

## 8.3 A → C

A provides:

```text
Saga step
operationKey
Payment command
compensation intent
```

C returns authoritative Payment event.

---

## 8.4 C → A/B

C provides:

```text
message envelope
publisher/consumer abstractions
retry/DLQ conventions
OTel propagation
faultpoints
```

A/B remain responsible for their domain state.

---

# 9. Integration freeze rules

## 9.1 Contract-first deadline

By end W2:

- public/internal command shapes used in W3–W6 must be frozen enough for parallel work;
- fixtures may change additively;
- breaking semantic change requires explicit review.

---

## 9.2 Shared abstraction deadline

Do not create a generalized:

```text
GenericDistributedWorkflowFramework
```

before two concrete flows exist.

For example:

- first implement/understand Trip Outbox + Driver Outbox;
- then extract genuinely shared relay logic.

Avoid premature abstraction.

---

# 10. Weekly team cadence

## Monday / week start

30–45 minutes:

```text
previous gate
red/flaky tests
current top risks
blockers
issue Ready check
review assignments
```

No long status presentation.

---

## Midweek integration

30 minutes:

```text
A/B/C integrate current boundary
run one real path
resolve contract drift early
```

---

## End-of-week review

45–60 minutes:

```text
demo week's exit condition
review evidence
actual vs estimate
GREEN/YELLOW/RED
scope cut if needed
```

---

## Daily async update

Compact:

```text
Closed:
In progress:
Blocked:
Test/evidence:
Need review:
```

---

# 11. Weekly health classification

## GREEN

- exit criteria complete;
- no critical unknown architecture;
- critical tests stable.

## YELLOW

- one important dependency late;
- workaround exists;
- gate still recoverable next week.

## RED

- invariant test failing;
- critical path >1 week late;
- architecture disagreement unresolved;
- required dependency cannot integrate;
- fault behavior unknown.

When RED:

```text
optional work = 0
```

until recovered.

---

# 12. Week 1 — Architecture comprehension and team freeze
**Team planned effort: ~24 h**  
A: 8 h · B: 8 h · C: 8 h

## Objective

All three understand the same system before implementation.

No member should interpret:

- authority;
- timeout;
- idempotency;
- ACK;
- compensation

differently.

---

## Team tasks

### TEAM-W01-01 Read/freeze Docs 00–03

Each member produces a one-page summary:

```text
authority
top invariants
top failure windows
own responsibilities
cross-team dependencies
```

### TEAM-W01-02 Course rubric traceability review

Check official requirements:

- ≥4 business services;
- owned data;
- sync + MQ;
- concurrency;
- retry/dedup/DLQ;
- Saga;
- failure/restart;
- test/performance;
- contribution evidence.

### TEAM-W01-03 Repository skeleton

Target:

```text
README.md
src/
deploy/
tests/
scripts/
docs/
evidence/
.env.example
```

### TEAM-W01-04 GitHub Project setup

Create fields:

```text
Week
Owner
Status
Priority
Estimate
Actual
Invariant
Scenario
Reviewer
```

### TEAM-W01-05 Architecture oral walkthrough

Each member answers:

1. Who owns Driver reservation?
2. Why Redis may be stale?
3. Why timeout may be UNKNOWN?
4. What Outbox fixes?
5. ACK vs confirm?
6. What compensation means?

---

## Member A

- freeze Trip/Fare/Saga vocabulary;
- map DSI-008..014;
- draft Trip project structure.

## Member B

- freeze Driver/Matching vocabulary;
- map DSI-001..007, 015..023;
- draft concurrency proof plan.

## Member C

- freeze Payment/messaging concepts;
- map DSI-024..029;
- draft broker/infra topology.

---

## Exit

```text
[ ] docs 00–03 accepted for implementation
[ ] ownership signed off
[ ] GitHub board active
[ ] no authority disagreement
[ ] no P1 implementation opened
```

---

# 13. Week 2 — Reproducible foundation
**Team planned effort: ~27 h**  
A: 9 h · B: 9 h · C: 9 h

## Objective

Clean clone can boot infrastructure and service skeletons.

---

## Shared outputs

```text
Compose core profile
PostgreSQL roles/databases
RabbitMQ
Redis
service DNS
health/live
health/ready
OTel bootstrap
CI build/test
```

---

## Member A

### A-W02-01 Trip service skeleton

- domain/application/infrastructure/API;
- migration baseline.

### A-W02-02 Identity/shared contract support

Help freeze Rider/Driver auth role baseline.

### A-W02-03 Trip integration fixture

Real PostgreSQL.

---

## Member B

### B-W02-01 Driver service skeleton

### B-W02-02 Matching service skeleton

Matching gets:

- own DB;
- Redis;
- Driver HTTP client;

not Driver DB access.

### B-W02-03 Redis/Testcontainers fixture

---

## Member C

### C-W02-01 Compose infrastructure

### C-W02-02 DB roles

Prove wrong-service access fails.

### C-W02-03 Rabbit topology bootstrap skeleton

### C-W02-04 OTel/health/bootstrap CI

---

## Integration review

Run:

```text
docker compose up
```

Verify:

- service names resolve;
- DB migrations;
- health endpoints;
- one trace;
- cross-service DB access denied.

---

## Gate G0 partial

GREEN if foundation reproducible.

---

# 14. Week 3 — Core domain state machines
**Team planned effort: ~27 h**  
A: 9 h · B: 9 h · C: 9 h

## Objective

Authoritative local state machines exist before distributed messaging.

---

## Member A — Trip

Implement:

```text
Trip aggregate
FareSnapshot
CreateRide
basic Trip transitions
idempotent create
```

Tests:

- legal/illegal FSM;
- same-key create;
- conflicting-key request.

---

## Member B — Driver

Implement:

```text
Driver aggregate
OFFLINE/AVAILABLE/RESERVED/ON_TRIP
Vehicle association
location input validation
```

No Redis authority shortcut.

---

## Member C — Payment

Implement:

```text
Payment aggregate/FSM
Payment operation persistence
deterministic provider modes
Notification worker skeleton
```

---

## Team integration

Contract fixture exchange.

A/B/C run each other's basic service API once.

---

## Exit

```text
local authority models exist
local illegal transitions rejected
real PostgreSQL migrations/tests green
```

---

# 15. Week 4 — Location projection and booking foundation
**Team planned effort: ~28 h**  
A: 9 h · B: 10 h · C: 9 h

## Objective

Rider can create a legal Trip and Driver location can enter a safe derived Matching projection.

---

## A

Implement:

- Trip create API;
- Rider active-trip defense;
- deterministic FareSnapshot;
- Trip versioning/concurrency baseline.

Tests:

```text
FUN-04
DSI-013
DSI-014
```

---

## B

Implement:

- location accepted identity;
- Redis driver metadata;
- GEO membership;
- version/freshness guard;
- atomic Redis Function.

Tests:

```text
REL-12
REL-13
```

early integration variants.

---

## C

Implement:

- canonical MessageEnvelopeV1;
- correlation/trace headers;
- topology-as-code baseline;
- security/auth negative test skeleton.

---

## Cross-review

C reviews Redis event envelope.

A reviews Driver identity/Trip-reference semantics.

---

## Exit

```text
Driver AVAILABLE + location
→ discoverable in Redis

CreateRide
→ one stable Trip/FareSnapshot
```

---

# 16. Week 5 — Deterministic Matching pipeline
**Team planned effort: ~28 h**  
A: 9 h · B: 10 h · C: 9 h

## Objective

Matching can decide **whom to try**, but not bypass authority.

---

## B primary

Implement:

```text
GEOSEARCH
eligibility filtering
freshness
deterministic ranking
stable tie-break
MatchingRound
MatchingAttempt base
bounded candidates
```

Proof:

```text
same snapshot → same order
stale/RESERVED candidate excluded
```

---

## A

Implement:

```text
StartMatching
matchingRound identity
Trip MATCHING
assignment placeholder semantics
```

A does not accept arbitrary Driver assignment.

---

## C

Implement:

```text
Payment duplicate-safe authorize/capture/void
operationKey uniqueness
provider result persistence
```

---

## Integration

Fixture:

```text
Trip T
→ MatchingRound 1
→ deterministic D1,D2,D3 order
```

No distributed reserve yet.

---

## Exit

Matching algorithm deterministic and bounded.

---

# 17. Week 6 — Authoritative concurrency milestone
**Team planned effort: ~31 h**  
A: 9 h · B: 13 h · C: 9 h

## Objective

Prove the first flagship DS invariant:

```text
many contenders
→ one Driver
```

---

## B primary

Implement:

```text
ReserveDriver conditional write/OCC
same-reservation idempotency
DB defense
reservationId
```

Build CON-01.

Run:

```text
N=2
10
50
100
```

---

## A

Integrate Trip interpretation:

- only one authoritative reservation result becomes current assignment;
- stale/non-current round cannot overwrite.

Add Trip-side assertions.

---

## C

Payment concurrency:

```text
CON-03
same operationKey
N=10/50/100
```

Prepare Rabbit real integration fixture.

---

## Team evidence

Store first formal evidence bundle:

```text
CON-01
CON-03
```

---

## Gate G1 partial

Must be green:

```text
DSI-001
DSI-024
```

If not:

```text
all messaging expansion pauses
```

---

# 18. Week 7 — Race/fencing/UNKNOWN
**Team planned effort: ~31 h**  
A: 10 h · B: 12 h · C: 9 h

## Objective

Close the most subtle synchronous distributed failure windows.

---

## B

Implement/prove:

```text
CON-02 GoOffline vs Reserve
CON-04 stale Release
REL-01 reserve response loss
```

Persist attempt before remote call.

UNKNOWN blocks next candidate.

---

## A

Implement:

```text
Cancel vs reserve semantics
current-round assignment guard
terminal monotonicity
exact cleanup intent model
```

Prepare CON-05.

---

## C

Implement:

```text
retry taxonomy
bounded retry policy
DLQ/error conventions
Payment TIMEOUT_AFTER_EFFECT
```

---

## Cross-team walkthrough

All three explain:

```text
request timed out
but remote may have committed
```

for:

- Driver;
- Payment.

---

## Exit

```text
DSI-004
DSI-017
DSI-018
DSI-031
DSI-033
```

have executable paths.

---

# 19. Week 8 — RabbitMQ backbone
**Team planned effort: ~32 h**  
A: 10 h · B: 10 h · C: 12 h

## Objective

Move workflow across real broker with explicit delivery semantics.

---

## C primary

Implement:

```text
publisher abstraction
publisher confirms
manual ACK
message envelope
trace propagation
queue topology
```

No auto-ACK critical path.

---

## A

Produce:

```text
RideRequestedV1
```

through initial Outbox-capable publication pattern.

Consume Driver/Matching outcomes with state guards.

---

## B

Consume:

```text
RideRequestedV1
```

persist/resume MatchingRound.

Driver authoritative events publish from Driver service.

---

## Team crash-window review

Before merge, each member must answer:

```text
publisher accepted but confirm lost?
consumer committed but ACK lost?
duplicate message?
```

---

## Gate G2 foundation

```text
real RabbitMQ happy path
manual ACK visible
publisher confirm visible
trace producer→consumer
```

---

# 20. Week 9 — Dedup + Transactional Outbox
**Team planned effort: ~32 h**  
A: 11 h · B: 10 h · C: 11 h

## Objective

Eliminate two major reliability gaps:

```text
duplicate delivery
DB commit + publish dual write
```

---

## C

Implement shared:

```text
processed_messages
Outbox relay support
confirm handling
retry/DLQ integration
```

---

## A

Trip:

```text
authoritative state + Outbox same transaction
```

Implement REL-04 from Trip.

---

## B

Driver/Matching:

```text
Driver state + Driver event Outbox
NO_DRIVER + MatchingFailed Outbox
```

Do not fork Outbox semantics.

---

## Team REL proofs

```text
REL-04 producer crash before publish
REL-05 duplicate publish
REL-06 consumer commit before ACK
```

---

## Gate G2 complete

No critical business event may rely on:

```text
best-effort publish after commit
```

---

# 21. Week 10 — Persisted Saga and compensation
**Team planned effort: ~33 h**  
A: 13 h · B: 9 h · C: 11 h

## Objective

Complete the central distributed workflow.

---

## A primary

Implement:

```text
persisted Saga
DriverAccepted → Payment auth
Payment failure → compensation
READY_TO_START
IN_PROGRESS
COMPLETED
capture forward recovery
```

---

## C

Implement:

```text
AuthorizePayment consumer
Payment result Outbox
Capture
Void
operationKey
```

---

## B

Implement:

```text
exact ReleaseDriver
MarkTripStarted
MarkTripCompleted
stale command guards
```

---

## Core scenarios

```text
happy ride
REL-09 Payment failure compensation
REL-10 capture after completion failure
```

---

## Exit

```text
base ride end-to-end
Saga durable
compensation exact
```

---

# 22. Week 11 — Deterministic fault campaign
**Team planned effort: ~32 h**  
A: 11 h · B: 11 h · C: 10 h

## Objective

Replace all timing luck with deterministic proof hooks.

---

## C primary fault platform

Implement testing-only:

```text
FaultPointProvider
Consumer.AfterCommitBeforeAck
Outbox.AfterPublishBeforeMark
Saga.AfterPersistBeforeDispatch
Payment.AfterEffectBeforeResponse
```

---

## B

Implement:

```text
Driver.AfterReserveCommitBeforeResponse
matching barriers
stale-release barriers
```

Convert CON/REL tests away from sleeps.

---

## A

Automate:

```text
Cancel vs reserve
Saga restart
Outbox crash
terminal late-event proof
```

---

## Repeatability

Critical scenarios:

```text
5 controlled consecutive passes
```

recommended before declaring stable.

If one needs arbitrary sleeps:

```text
test/harness not accepted
```

---

# 23. Week 12 — Observability, security and Core Release Gate
**Team planned effort: ~29 h**  
A: 9 h · B: 9 h · C: 11 h

## Objective

Make the system diagnosable and close P0 correctness before performance.

---

## A

Instrument:

```text
Trip
Saga
Outbox
```

Metrics:

```text
trip transitions
Saga active/compensation
Outbox pending/age
```

Run A regression.

---

## B

Instrument:

```text
Driver reserve conflict/unknown
Matching attempts/duration
Redis projection stale rejects
```

Review HTTP resilience config.

---

## C

Instrument:

```text
RabbitMQ consumer/dedup/DLQ
Payment outcomes
Notification
```

Run:

```text
SEC-01..09 baseline
```

Build minimal evidence dashboard.

---

## Core Release Gate G4

Required:

```text
[ ] FUN mandatory path
[ ] CON-01
[ ] CON-02
[ ] CON-03
[ ] CON-04
[ ] REL-01
[ ] REL-02
[ ] REL-04
[ ] REL-05
[ ] REL-06
[ ] REL-07
[ ] REL-09
[ ] REL-11
[ ] security baseline
[ ] auditor green
[ ] zero S0/S1 defect
```

If red:

```text
W13 starts with repair
not performance
```

---

# 24. Week 13 — Performance campaign
**Team planned effort: ~30 h**  
A: 8 h · B: 14 h · C: 8 h

## Objective

Measure only after correctness is established.

---

## B primary

Run:

```text
PERF/E01 contention curve
PERF/E02 controlled API load
PERF/E03 consumer scaling
```

Capture:

- raw k6 output;
- hardware;
- config;
- P50/P95;
- conflict vs system error.

---

## A

Ensure:

```text
Trip/Saga invariants still audited under load
```

Provide Trip-side metrics.

---

## C

Support:

```text
consumer replicas
queue metrics
prefetch freeze
broker metrics
```

No simultaneous uncontrolled tuning.

---

## Team rule

Each major result:

```text
3–5 measured runs
```

if feasible.

No architecture rewrite because benchmark is disappointing unless correctness defect exists.

---

# 25. Week 14 — Recovery experiment + RabbitMQ HA validation
**Team planned effort: ~24 h**  
A: 7 h · B: 8 h · C: 9 h

## Objective

Close the final distributed reliability evidence.

---

## C primary

Run:

```text
REL-15 outage/backlog/recovery
REL-16 3-node Rabbit quorum one-node loss
```

---

## B

Run/interpret:

```text
PERF/E04 backlog recovery
```

Compare measured:

```text
B
μ
T_drain
```

against approximation.

---

## A

Verify:

- Outbox false-success does not occur;
- Saga/workflow final state legal;
- auditor green.

---

## Optional P1 gate

Only if:

```text
G4 green
G5 mandatory work on schedule
zero S0/S1 defect
```

At most one bounded P1.

Recommended default:

```text
NO P1
```

unless core evidence is already strong.

---

# 26. Week 15 — Feature freeze and report/evidence
**Team planned effort: ~27 h**  
A: 9 h · B: 9 h · C: 9 h

## Objective

Stop architecture expansion.

Convert implementation into reproducible academic evidence.

---

## Team tasks

### TEAM-W15-01 Feature freeze

No new:

- service;
- broker;
- DB technology;
- orchestration framework;
- product feature.

### TEAM-W15-02 Clean clone

Another member follows README from empty environment.

### TEAM-W15-03 Evidence integrity

Every major report claim links to:

- run;
- commit;
- raw data;
- assertion.

### TEAM-W15-04 Report assembly

A:

```text
domain/Saga/Outbox
```

B:

```text
Driver/Matching/concurrency/performance
```

C:

```text
messaging/Payment/reliability/observability/security
```

Shared:

```text
abstract
architecture
limitations
conclusion
```

---

## Exit

```text
release candidate
report technically complete
demo path stable
```

---

# 27. Week 16 — Release, demo and oral defense
**Team planned effort: ~29 h**  
A: 10 h · B: 10 h · C: 9 h

## Objective

Operate and defend, not merely show slides.

---

## Release tasks

```text
tag final candidate
clean clone
migrations from empty DB
all G5 tests
secret scan
README commands
evidence links
```

---

## Demo rehearsals

At least:

```text
3 full rehearsals
```

Measure:

```text
actual demo time
recovery from demo failure
role handoffs
```

---

## Live demo ownership

A:

- Trip/Saga/compensation.

B:

- Driver/OCC/UNKNOWN/performance.

C:

- RabbitMQ/dedup/Payment/observability.

---

## Final release tag

Example:

```text
v1.0.0
```

Course specification asks for a final tag/release and visible commit history/contribution by members.

---

# 28. Weekly effort map

| Week | A | B | C | Team | Risk |
|---:|---:|---:|---:|---:|---|
| W1 | 8 | 8 | 8 | 24 | medium |
| W2 | 9 | 9 | 9 | 27 | medium |
| W3 | 9 | 9 | 9 | 27 | medium |
| W4 | 9 | 10 | 9 | 28 | medium |
| W5 | 9 | 10 | 9 | 28 | medium |
| W6 | 9 | 13 | 9 | 31 | **high** |
| W7 | 10 | 12 | 9 | 31 | **high** |
| W8 | 10 | 10 | 12 | 32 | **high** |
| W9 | 11 | 10 | 11 | 32 | **high** |
| W10 | 13 | 9 | 11 | 33 | **very high** |
| W11 | 11 | 11 | 10 | 32 | **high** |
| W12 | 9 | 9 | 11 | 29 | **high** |
| W13 | 8 | 14 | 8 | 30 | high |
| W14 | 7 | 8 | 9 | 24 | medium |
| W15 | 9 | 9 | 9 | 27 | medium |
| W16 | 10 | 10 | 9 | 29 | high |
| **Total** | **151** | **161** | **151** | **463** | — |

> **Planning note:** this table is a **capacity reservation**, not an obligation to consume every hour. Individual 05A/B/C playbooks may rebalance estimates while preserving the same phase/critical-path structure. The team should optimize for evidence and correctness, not for matching a spreadsheet exactly.

---

# 29. Capacity interpretation

The Week 6–13 region is intentionally heavier.

If actual sustainable team capacity is lower than this planning envelope:

Cut in this order:

```text
1. P1
2. visual/dashboard polish
3. optional extra performance levels
4. extra notification features
5. nonessential abstraction/refactor
```

Never cut first:

```text
Driver OCC
UNKNOWN reconciliation
Outbox
consumer ACK/dedup
Saga compensation
fault proofs
auditor
```

---

# 30. Release-gate summary

## G0 — Foundation

By end W2:

```text
clean boot
DB ownership
DNS
health
CI
```

## G1 — Local/Concurrency

By W6–7:

```text
Driver exclusivity
Payment idempotency
state machines
```

## G2 — Messaging

By W9:

```text
RabbitMQ
confirm
ACK
dedup
Outbox
```

## G3 — Workflow

By W10–11:

```text
Saga
compensation
UNKNOWN
restart
```

## G4 — Core Release

By W12:

```text
all critical P0 correctness proof
```

## G5 — Final Validation

By W14–15:

```text
performance
backlog recovery
quorum validation
release evidence
```

---

# 31. Gate failure rules

If gate is red:

## One issue

Owner + reviewer spend first capacity next week fixing it.

## Multiple critical issues

Team pauses feature development.

## S0 invariant violation

Immediate:

```text
stop all P1/performance
```

until understood.

Never:

```text
mark known invariant defect as limitation
```

for P0 release.

---

# 32. Scope-cut ladder

## Keep at all costs

```text
Identity baseline
Driver
Trip
Matching
Payment
RabbitMQ
Redis projection
OCC
UNKNOWN
Outbox
dedup
Saga
fault proof
invariant auditor
```

## Cut first

```text
extra dashboard panels
complex Notification UX
additional benchmarks
extra candidate ranking component
gRPC comparison
TripReport/telemetry extension
```

## Cut next if severe delay

```text
some secondary FUN cases beyond rubric
performance level N=500/1000
optional security polish beyond baseline
```

Do not cut mandatory course categories.

---

# 33. Risk register

| Risk | Probability | Impact | Owner | Mitigation |
|---|---:|---:|---|---|
| OCC bug creates double reservation | M | Critical | B | W6 early proof + DB constraint |
| timeout handled as failure | M | Critical | B/A | W7 UNKNOWN contract |
| duplicate messaging causes double effect | M | Critical | C | W8–9 dedup/ACK proof |
| Outbox implementation wrong | M | Critical | A/C | shared review + REL-04/05 |
| Saga stuck after restart | M | High | A | persisted state + REL-07 |
| stale release frees new reservation | M | Critical | B/A | fencing + CON-04 |
| Redis projection regresses | M | High | B | version/freshness function |
| retry multiplication | M | High | C | one owner per boundary |
| Rabbit HA overclaim | L | Medium | C | narrow REL-16 wording |
| tests flaky | H | High | all | deterministic faultpoints |
| performance week becomes tuning rabbit hole | M | Medium | B | question-first experiments |
| documentation drifts | M | Medium | all | owner hierarchy + PR doc check |
| member bottleneck | M | High | lead | second reviewer + handoff docs |
| scope creep | H | High | all | G4/P1 gate |
| report delayed | M | High | all | evidence/report snippets from W11 |

---

# 34. Bus-factor rule

Every critical mechanism needs a second person who can:

```text
explain
run test
inspect final state
review code
```

Minimum backup:

| Mechanism | Primary | Backup |
|---|---|---|
| Saga | A | C |
| OCC | B | A |
| UNKNOWN reserve | B | A |
| RabbitMQ ACK/confirm | C | B |
| Outbox | A/C | B |
| Payment idempotency | C | A |
| performance | B | C |
| fault harness | C | B |
| auditor | shared | all |

---

# 35. Review checklist by boundary

## Domain

```text
[ ] owning aggregate?
[ ] legal transition?
[ ] terminal guard?
[ ] stable identity?
```

## DB/concurrency

```text
[ ] guarded write?
[ ] DB constraint?
[ ] concurrent test?
```

## HTTP

```text
[ ] timeout classification?
[ ] retry owner?
[ ] UNKNOWN possible?
```

## Messaging

```text
[ ] command/event owner?
[ ] stable messageId?
[ ] Outbox if required?
[ ] ACK after commit?
[ ] dedup?
[ ] stale-state guard?
```

## Recovery

```text
[ ] process restart?
[ ] pending state durable?
[ ] finite retry/review?
```

## Evidence

```text
[ ] final authority asserted?
[ ] artifact saved?
```

---

# 36. Team blocker escalation

## Blocked >2 focused hours

Write:

```text
exact question
observed behavior
expected contract
logs/state
```

Ask owner/reviewer.

---

## Blocked >1 working day

15-minute design sync.

Choose one:

```text
clarify contract
reduce scope
build spike
change sequence
```

---

## Blocked >2 working days

Mark:

```text
Blocked
project health YELLOW/RED
```

Leader updates critical path and cuts lower priority.

---

## Immediate escalation

- two active reservations observed;
- stale release frees new reservation;
- Matching advances after UNKNOWN;
- ACK-before-commit proposed;
- state publish without Outbox on required path;
- Payment retry generates new logical operation;
- Redis proposed as assignment authority;
- critical test only passes with large sleep;
- performance test disables correctness checks.

---

# 37. Issue template

```text
Title:
<member>-W<week>-<n> <imperative task>

Why:
Course/architecture reason.

Docs:
00 §...
01 DSI/DSL...
02 §...
03 scenario...

Authority:
...

Stable identities:
...

Failure/race:
...

Inputs:
...

Implementation:
...

Tests:
...

Evidence:
...

Reviewer:
...

Estimate:
...

Blocked by:
...

Definition of Done:
...
```

---

# 38. PR template

```text
## Intent
What distributed-system problem is this PR closing?

## Contract
DSI/DSL/architecture rule:

## Mechanism
What changed?

## Failure window
What can go wrong?

## Tests
Unit:
Integration:
Concurrency/Fault:

## Final state
What authoritative assertion proves correctness?

## Evidence
Link/path:

## Reviewer focus
...

## Scope intentionally not included
...
```

---

# 39. Weekly status template

```text
Week:
Health: GREEN / YELLOW / RED

Completed:
- ...

Evidence:
- ...

Red/flaky:
- ...

Invariant risk:
- ...

Blocked:
- ...

Hours planned / actual:
A:
B:
C:

Next week top 3:
1.
2.
3.

Scope cut if delayed:
...
```

---

# 40. Evidence ownership

## A

Owns primary curation for:

```text
Trip FSM
Outbox producer crash
Saga restart
compensation
terminal monotonicity
```

## B

Owns:

```text
contention
GoOffline/Reserve
stale compensation
UNKNOWN reserve
Redis reorder
performance
```

## C

Owns:

```text
publisher confirms
commit-before-ACK
DLQ
Payment same-key
broker reconnect
Rabbit quorum
security/platform
```

---

# 41. Report ownership

## A sections

```text
Domain model
Trip state machine
Saga
Outbox business intent
compensation
```

## B

```text
Driver/Matching
OCC
Redis GEO
UNKNOWN
performance/scalability
```

## C

```text
communication/messaging
delivery semantics
Payment
fault tolerance
observability
security/deployment
```

## Shared

```text
abstract
requirements
architecture
testing summary
limitations
conclusion
references
```

No section is considered final without one peer review.

---

# 42. Report writing cadence

Do not start from zero in W15.

## W6

Save first concurrency figure/table.

## W9

Save Outbox/ACK diagrams.

## W10

Save Saga/compensation timeline.

## W11–12

Save fault traces.

## W13–14

Save performance plots/tables.

## W15

Assemble and edit.

---

# 43. Demo preparation cadence

## W6

CON-01 command/script usable.

## W9

Rabbit failure scripts usable.

## W10

happy ride path.

## W11

deterministic live failure.

## W12

auditor/trace view.

## W14

quorum/backlog evidence.

## W15

full 9-minute rehearsal.

## W16

final rehearsal/release.

---

# 44. Contribution evidence

Course requires each member to show contribution.

Use:

```text
issues
PRs
commits
reviews
evidence folders
report sections
demo ownership
```

Avoid artificially splitting commits purely for count.

Quality/ownership matters more.

---

# 45. Architecture-change procedure

If implementation reveals the frozen design is wrong:

1. open ADR/architecture issue;
2. describe observed contradiction;
3. identify affected DSI/DSL;
4. compare options;
5. all affected owners review;
6. update Docs 00–03;
7. update 04/05 schedule;
8. implement;
9. add regression proof.

Do not quietly change semantics in code.

---

# 46. Current GitHub workflow validation

The execution mechanics above align with current GitHub capabilities:

- Issues support sub-issues and explicit blocking dependencies;
- Projects support custom fields, iterations and parent/sub-issue progress;
- branch protection can require PR review and required status checks;
- workflow artifacts can retain test logs, failures, screenshots and performance output.

These tools are optional implementation conveniences; project correctness does not depend on GitHub itself.

---

# 47. References for execution mechanics

## Project authority

- `00-project-scope-architecture.md`
- `01-domain-correctness-contract.md`
- `02-distributed-workflow-engineering.md`
- `03-verification-experiments-demo.md`
- official `COURSE PROJECT SPECIFICATION.pdf`

## GitHub

1. About Issues  
   https://docs.github.com/en/issues/tracking-your-work-with-issues/learning-about-issues/about-issues

2. Best practices for Projects  
   https://docs.github.com/en/issues/planning-and-tracking-with-projects/learning-about-projects/best-practices-for-projects

3. Understanding Project fields  
   https://docs.github.com/en/issues/planning-and-tracking-with-projects/understanding-fields

4. About protected branches  
   https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/managing-protected-branches/about-protected-branches

5. Workflow artifacts  
   https://docs.github.com/en/actions/concepts/workflows-and-actions/workflow-artifacts

These references validate the tracking/review mechanics only. They do not override the distributed-system semantics in Docs 00–03.

---

# 48. Freeze checklist

Before promoting 04 to **ACCEPTED/FROZEN**:

## Ownership

- [ ] A/B/C primary ownership accepted;
- [ ] every critical mechanism has second reviewer;
- [ ] no service has two ambiguous authorities.

## Schedule

- [ ] W1–W16 accepted;
- [ ] W6–W12 critical path understood;
- [ ] W15–W16 protected from new architecture.

## Gates

- [ ] G0–G5 accepted;
- [ ] P1 forbidden before G4;
- [ ] S0/S1 defects block release.

## Tracking

- [ ] Project fields created;
- [ ] issue naming agreed;
- [ ] PR template agreed;
- [ ] branch protection/CI policy selected.

## Evidence

- [ ] scenario ownership assigned;
- [ ] evidence directory convention agreed;
- [ ] report figures generated continuously rather than W15-only.

## Workload

- [ ] team weekly capacity realistic;
- [ ] heavy weeks acknowledged;
- [ ] scope-cut ladder accepted;
- [ ] no hidden fourth role.

## Defense

- [ ] each member owns demo segment;
- [ ] each critical mechanism has backup explainer;
- [ ] oral question bank in Doc 03 assigned/rehearsed.

---

# 49. Team operating rule

> **Do not optimize for the number of features completed. Optimize for the number of distributed-system claims that are implemented, intentionally broken, recovered, independently verified and explainable by more than one team member.**

> **A feature without a correctness oracle is unfinished. A reliability mechanism without a deterministic failure proof is unverified. A benchmark without final-state invariants is incomplete. A subsystem understood by only one member is a release risk.**
