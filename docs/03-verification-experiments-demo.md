# DS03 DISTRIBUTED RIDE-HAILING SYSTEM
## Verification · Experiments · Fault Campaign · Evidence · Demo & Defense Contract

> **Document:** `docs/03-verification-experiments-demo.md`  
> **Course:** Distributed Systems  
> **Topic:** DS03 — Ride-hailing Microservices System simulating Grab/Uber  
> **Type:** Verification Strategy + Experimental Methodology + Fault Campaign + Evidence Contract + Demonstration/Defense Runbook  
> **Status:** **PROPOSED FREEZE v2.0 — REVIEW CANDIDATE**  
> **Baseline date:** **2026-09-26**  
> **Parents:**  
> `00-project-scope-architecture.md` — scope, architecture, failure model, technology baseline  
> `01-domain-correctness-contract.md` — state machines, DSI safety invariants, DSL liveness obligations, DSR races  
> `02-distributed-workflow-engineering.md` — message/retry/Outbox/Saga/RabbitMQ/Redis/recovery semantics  
> **Children:** `04-team-execution-plan.md`, `05A/05B/05C`  
> **Normative role:** source of truth for test taxonomy, exact proof families, scenario IDs, concurrency/fault methodology, invariant-auditor expectations, performance experiments, evidence bundles, test environments, acceptance gates, demo sequence and claim discipline.  
> **Not source of truth for:** domain semantics (`01`), distributed mechanism semantics (`02`), task ownership/calendar (`04/05`).  
> **Change policy:** weakening a test oracle, removing a proof for a DSI/DSL, replacing a real dependency with a mock where dependency behavior is the subject of the claim, lowering mandatory fault/concurrency coverage, or changing experiment methodology requires review against Docs 00–02.

> **Verification thesis.**  
> **A distributed-systems claim is not accepted because the happy path works, because logs look plausible, or because a library promises a property. It is accepted only when the team can create the relevant race/failure deterministically, observe the intended recovery mechanism, and assert the final authoritative business state from reproducible evidence.**

---

# 0. Executive verification baseline

## 0.1 Course minimum versus project target

The course requires at least:

```text
8 functional tests
2 concurrency/conflict tests
2 fault/recovery tests
2 security/invalid-data tests
1 performance/scalability experiment
```

The DS03 target is intentionally higher:

```text
Functional/system            12+
Concurrency/conflict          6+
Fault/recovery               15+
Security/invalid-data         8+
Performance/recovery          4 core experiments
```

The goal is not “more tests for a larger number.”

The higher target is necessary because the project claims correctness under:

- races;
- stale derived state;
- duplicate delivery;
- unknown remote outcomes;
- consumer crash;
- publisher crash;
- Saga restart;
- broker interruption;
- service reconnect.

Those claims cannot be credibly covered by two fault tests.

---

## 0.2 The five evidence classes

Every major project claim should produce at least one of:

### E1 — Domain proof

Pure/unit/property-like state-machine/invariant proof.

### E2 — Real-boundary integration proof

Real PostgreSQL/RabbitMQ/Redis/HTTP behavior.

### E3 — Concurrency proof

Synchronized competing operations with final-state assertions.

### E4 — Fault/recovery proof

Deterministic failure at a named crash window followed by convergence/recovery.

### E5 — Experiment/measurement proof

Controlled workload, repeated runs, raw metrics, configuration and limitations.

A screenshot alone belongs to none of these.

---

## 0.3 Required proof principle

For every high-value scenario:

```text
SETUP
→ CONTROLLED ACTION
→ CONTROLLED RACE/FAULT
→ OBSERVE INTERMEDIATE EVIDENCE
→ WAIT FOR QUIESCENCE/TERMINAL CONDITION
→ ASSERT AUTHORITATIVE FINAL STATE
→ RUN INVARIANT AUDITOR
→ SAVE RAW ARTIFACTS
```

---

## 0.4 Evidence hierarchy

Strongest:

```text
authoritative DB state
+
deterministic execution record
+
test assertion
```

Then:

```text
broker state
metrics
traces
structured logs
```

Then:

```text
screenshots/video
```

Screenshots and traces explain.

They do not replace correctness assertions.

---

# 1. Verification philosophy

## 1.1 Test the property, not the code path

Bad test objective:

> “Call ReserveDriver and expect HTTP 200.”

Better:

> “Under 100 synchronized contenders, exactly one current Driver reservation exists in authoritative storage after quiescence.”

The second test remains meaningful even if controller/service implementation changes.

---

## 1.2 Assertion must match authority

If the claim is:

```text
one Driver has one active reservation
```

assert:

```text
Driver authoritative DB
```

not:

```text
number of HTTP 200 responses
```

If the claim is:

```text
one logical Payment operation
```

assert:

```text
Payment operation table/business state
```

not only message counts.

---

## 1.3 Final-state proof plus temporal evidence

Distributed bugs can be temporal.

Example:

```text
Driver temporarily double-assigned
then one row overwritten
```

Final state alone may hide the violation.

Therefore critical tests should combine:

1. transition/event records or controlled transactional outcomes;
2. final invariant audit.

Where possible, database constraints make illegal transient committed states impossible.

---

## 1.4 Determinism before chaos

Random fault injection is not the baseline proof method.

Required correctness evidence uses deterministic faultpoints:

```text
Consumer.AfterCommitBeforeAck
Driver.AfterReserveCommitBeforeResponse
Outbox.AfterPublishBeforeMark
Saga.AfterPersistBeforeDispatch
```

Why:

- repeatable;
- debuggable;
- defense-friendly;
- identifies exact crash window.

Random chaos can be an optional robustness layer after deterministic cases pass.

---

## 1.5 No arbitrary sleeps as race proof

Forbidden:

```text
Task.Delay(100)
then start second request
```

unless sleep itself is the studied timing mechanism.

Use:

- Barrier;
- CountdownEvent;
- explicit faultpoint;
- server-side test hook;
- state polling with deadline.

A race test must actively create overlap.

---

# 2. Test pyramid adapted for distributed systems

## 2.1 Domain/unit tests

Fast.

Use for:

- FSM transition tables;
- idempotent replay decisions;
- ranking determinism;
- error classification;
- Saga transition logic;
- state mapping.

Mocks acceptable when infrastructure semantics are not the claim.

---

## 2.2 Component integration tests

Use:

- `WebApplicationFactory`;
- real PostgreSQL via Testcontainers;
- real Redis;
- real RabbitMQ where appropriate.

Microsoft's ASP.NET Core guidance supports `WebApplicationFactory<TEntryPoint>` for integration testing the SUT, while Testcontainers provides disposable real infrastructure instead of in-memory substitutes.

---

## 2.3 Cross-service integration tests

Run multiple real service processes/hosts.

Use for:

- Trip→Matching;
- Matching→Driver;
- Trip↔Payment;
- Notification delivery;
- Outbox relay;
- consumer redelivery.

---

## 2.4 System/fault tests

Run Compose profile or equivalent real topology.

Use for:

- process kill;
- broker interruption;
- reconnect;
- backlog drain;
- RabbitMQ quorum node loss.

---

## 2.5 Performance experiments

Dedicated reproducible profile.

Never run performance claims from:

- random developer machine background load;
- debugger;
- mixed test data from previous runs;
- changing configuration across repeats.

---

# 3. Test environment matrix

| Environment | Purpose | Real dependencies | Faults | Performance claims |
|---|---|---|---|---|
| `unit` | domain logic | none/minimal | no | no |
| `integration` | service + DB/broker semantics | Testcontainers PostgreSQL/Redis/RabbitMQ | selected | no |
| `core` | end-to-end development | full single-node broker topology | basic | no |
| `fault` | deterministic crash/recovery | full core + faultpoints | yes | recovery only |
| `obs` | traces/metrics evidence | core + OTel stack | yes | diagnostic |
| `perf` | controlled experiments | fixed topology/config | only specified | yes |
| `ha` | RabbitMQ quorum validation | 3 Rabbit nodes | broker-node failure | HA claim only |

---

# 4. Reproducibility contract

Every high-value run must record:

```text
runId
UTC timestamp
git commit SHA
git dirty/clean state
branch/tag
OS
CPU model / vCPU allocation
RAM
Docker version
container image versions
.NET runtime
PostgreSQL version
RabbitMQ version
Redis version

profile
service replica counts
database pool settings
RabbitMQ queue type/prefetch
retry/timeouts
dataset seed
dataset size
faultpoint/config
load-generator version
```

For performance:

```text
warm-up duration
measurement duration
offered load
VUs/preallocated VUs
run number
```

Without configuration metadata, the result is not report-grade evidence.

---

# 5. Deterministic dataset design

## 5.1 Seeded Driver geography

Use fixed seed.

Datasets:

```text
D050
D100
D300
D500
```

Optional:

```text
D1000
```

only if hardware supports useful comparison.

---

## 5.2 Candidate fixtures

Create known patterns:

```text
near fresh AVAILABLE
near stale AVAILABLE
near RESERVED
medium fresh AVAILABLE
far fresh AVAILABLE
equal-distance tie
OFFLINE
```

This makes expected ranking/eligibility deterministic.

---

## 5.3 Stable identity fixture

Every scenario knows:

```text
tripId
driverId
matchingRound
reservationId
operationKey
messageId
correlationId
```

or records them in scenario manifest.

---

# 6. Test naming and severity

## 6.1 Canonical namespaces

```text
FUN-*   functional/system
CON-*   concurrency/conflict
REL-*   reliability/fault/recovery
SEC-*   security/invalid-data
PERF-*  performance/scalability
AUD-*   invariant auditor
```

---

## 6.2 Severity

### S0 — correctness blocker

Violation of:

- exclusive reservation;
- Trip terminal monotonicity;
- Payment idempotency;
- stale compensation fencing;
- durable workflow recovery.

Release forbidden.

### S1 — major distributed behavior

- queue retry broken;
- Saga restart broken;
- stale Redis event regresses projection;
- authz violation.

Release forbidden.

### S2 — degraded quality

- noncritical metrics missing;
- less helpful error message;
- optional dashboard issue.

Can ship course release with documented limitation if gate allows.

---

# 7. Functional/system test catalog

## FUN-01 — Identity registration/login

**Purpose**

Prove basic authenticated distributed entry.

**Steps**

```text
register Rider
login
obtain authenticated session/token
query own profile
```

**Assert**

- password is not returned/stored plaintext;
- role correct;
- wrong credentials rejected.

---

## FUN-02 — Driver online/offline lifecycle

```text
OFFLINE
→ valid GoOnline
→ AVAILABLE
→ GoOffline
→ OFFLINE
```

Assert Driver FSM.

---

## FUN-03 — Location accepted and discoverable

```text
Driver AVAILABLE
send valid location
projection applies
Matching GEO query finds Driver
```

Assert:

- coordinate accepted;
- Redis projection current;
- Driver DB remains authority.

---

## FUN-04 — Create Ride idempotency

Call CreateRide twice with same idempotency key/request.

Expected:

```text
one tripId
one logical Trip
```

Conflicting payload under same key:

```text
idempotency conflict
```

---

## FUN-05 — Matching happy path

```text
Trip MATCHING
→ deterministic candidate
→ Driver reserve
→ MatchingRound RESERVED
→ DriverReserved event
→ Trip DRIVER_RESERVED
```

---

## FUN-06 — Driver accepts ride

Correct Driver/current reservation accepts.

Trip reaches legal accepted state.

Wrong Driver/current identity is rejected.

---

## FUN-07 — Payment authorization success

```text
Driver accepted
→ AuthorizePayment
→ Payment AUTHORIZED
→ Trip READY_TO_START
```

Assert same `operationKey`.

---

## FUN-08 — Start Trip

Only legal current Driver and authorized Trip can start.

Final:

```text
Trip IN_PROGRESS
Driver ON_TRIP
```

eventual cross-service agreement.

---

## FUN-09 — Complete Trip

```text
IN_PROGRESS
→ COMPLETE
→ Trip COMPLETED
→ Driver AVAILABLE after closure protocol
```

---

## FUN-10 — Payment capture success

Capture occurs after completion.

One logical capture.

---

## FUN-11 — Matching no Driver

All bounded candidates conclusively reject/exhaust.

Assert:

```text
MatchingRound NO_DRIVER
MatchingFailed Outbox
Trip MATCH_FAILED
```

---

## FUN-12 — Pre-start cancellation

Cancel active pre-start Trip.

Assert:

```text
Trip CANCELLED
exact matching cancellation/Driver cleanup intent
eventual no leaked reservation
```

---

# 8. Concurrency/conflict campaign

## CON-01 — N Trips contend for one Driver

### Goal

Primary proof for DSI-001.

### Setup

```text
Driver D = AVAILABLE
N independent Trips/reservation requests
barrier before authoritative reserve attempt
```

Run:

```text
N = 2
10
50
100
```

### Required oracle

From Driver DB:

```text
current active reservation count for D = 1
```

and:

```text
exactly one winning reservationId
```

### Additional metrics

```text
success
domain conflicts
latency
DB concurrency exceptions/zero-row updates
```

### Important interpretation

For N=100:

```text
~99 conflicts
```

may be exactly correct.

Conflict rate is not failure rate in this workload.

---

## CON-02 — GoOffline vs ReserveDriver

### Setup

```text
Driver AVAILABLE
same starting version
```

Barrier both commands.

### Allowed finals

```text
OFFLINE
or
RESERVED
```

### Forbidden

```text
both transitions considered committed
stale overwrite
```

Maps to DSR-004.

---

## CON-03 — Concurrent duplicate Payment operation

N concurrent:

```text
Authorize(operationKey=K)
```

Use:

```text
N = 10
50
100
```

Assert:

```text
logical Payment effect count(K) = 1
```

Responses may all resolve to same canonical result.

---

## CON-04 — Stale compensation vs newer reservation

Controlled sequence:

```text
reserve R1
release R1
reserve R2
release R1 arrives late
```

Assert:

```text
R2 survives
```

Primary proof DSI-004/033.

---

## CON-05 — Cancel vs Reserve in-flight

Barrier:

```text
Matching reserve crosses boundary
Rider cancellation races
```

Exercise both schedules:

### Schedule A

cancel commits first.

### Schedule B

Driver reserve commits first.

Final after recovery:

```text
Trip CANCELLED
no leaked reservation
no Trip reactivation
```

---

## CON-06 — Duplicate Matching execution/redelivery

Deliver same `RideRequestedV1` concurrently or near-concurrently to multiple Matching consumer instances.

Assert:

```text
one MatchingRound
one attempt ledger
no contradictory result
no double Driver assignment
```

---

# 9. Reliability/fault campaign

## REL-01 — Reserve response lost after Driver commit

### Faultpoint

```text
Driver.AfterReserveCommitBeforeResponse
```

### Sequence

```text
Matching calls Reserve(D,R)
Driver commits
faultpoint suppresses response
Matching timeout
```

### Required

```text
attempt UNKNOWN
no D2 attempt
reconcile same R
round → RESERVED
```

### Final oracle

Driver + Matching + Trip consistency.

---

## REL-02 — Matching process crash after remote reserve commit

### Sequence

```text
attempt IN_FLIGHT durable
Driver commits R
kill Matching before local RESERVED
restart/redelivery
```

### Required

```text
load same attempt
reconcile same R
no next Driver
```

---

## REL-03 — Cancel vs unknown reservation cleanup

Combine cancellation with REL-01.

Required:

```text
Trip stays CANCELLED
unknown R reconciled
if R exists → exact release
```

---

## REL-04 — Producer commits state+Outbox then crashes before publish

### Faultpoint

```text
Saga.AfterPersistBeforeDispatch
```

or process kill before relay.

### Required

```text
business state + pending Outbox exist
restart relay
message publishes
downstream converges
```

---

## REL-05 — Publish accepted but local mark/confirm completion lost

### Faultpoint

```text
Outbox.AfterPublishBeforeMark
```

### Required

- broker may receive same message more than once;
- `messageId` remains same;
- consumer business effect count = 1.

---

## REL-06 — Consumer commits then crashes before ACK

### Faultpoint

```text
Consumer.AfterCommitBeforeAck
```

### Required

```text
broker redelivery
dedup hit
no second business effect
ACK later
```

RabbitMQ documentation explicitly supports this ownership model: acknowledgement should occur after the consumer has performed the responsibility it accepts for the message.

---

## REL-07 — Saga restart while Payment pending

```text
Trip Saga persists PAYMENT_AUTH_PENDING
kill Trip Service
Payment result arrives/backlogs
restart Trip
```

Required:

```text
same Saga
same operationKey
legal continuation
```

---

## REL-08 — Payment timeout after effect

Provider mode:

```text
TIMEOUT_AFTER_EFFECT
```

Required:

```text
one operationKey
one Payment effect
reconcile existing outcome
```

---

## REL-09 — Payment terminal authorization failure

```text
Driver reserved/accepted
Payment declines
```

Required:

```text
Trip terminal cancellation/failure
exact Driver release
no leaked Driver
```

---

## REL-10 — Capture failure after Trip completion

```text
Trip COMPLETED
Capture transient/fails
```

Required:

```text
Trip remains COMPLETED
Payment CAPTURE_PENDING/REQUIRES_REVIEW
```

Primary proof DSI-028.

---

## REL-11 — Poison message

Inject:

- unsupported schema;
- deterministic invalid message.

Required:

```text
bounded attempts
DLQ/error visibility
healthy later message still processes
```

---

## REL-12 — Redis stale Driver-state event

Apply:

```text
v10 RESERVED
then v9 AVAILABLE
```

Expected:

```text
projection remains v10 RESERVED
```

---

## REL-13 — Redis stale location

Apply:

```text
session S seq20
then S seq19
```

Expected:

```text
seq20 remains latest
```

---

## REL-14 — Redis unavailable

Stop/partition Redis.

Expected:

```text
Matching degraded
no fabricated reservation
existing Driver/Trip authority remains legal
```

After recovery:

```text
projection rebuild/recovery according to implementation
```

---

## REL-15 — RabbitMQ service/network interruption and backlog recovery

Controlled:

```text
establish arrival rate
stop/isolate consumer/broker boundary for T
observe backlog/outbox accumulation
restore
measure drain
```

Assert:

- no committed intent silently lost;
- no duplicate logical effect;
- invariant auditor green.

---

## REL-16 — RabbitMQ one-node loss in 3-node quorum profile

### Environment

`ha`.

### Procedure

```text
publish/consume baseline
kill one Rabbit node
continue workload
restore
```

Record:

- queue leader/members;
- confirms;
- consumer behavior;
- recovery.

Claim only tested broker topology behavior.

---

## REL-17 — Late stale event after terminal Trip

Examples:

```text
Trip CANCELLED
late DriverReserved
late PaymentAuthorized
```

Required:

```text
no state regression
cleanup/no-op where appropriate
```

---

## REL-18 — Outbox relay process restart

Pending/processing rows exist.

Kill relay.

Restart.

Expected:

```text
no silent loss
possible safe duplicate only
```

---

# 10. Security and invalid-data campaign

Security is not the main thesis, but a distributed system whose authority can be trivially forged has meaningless correctness claims.

OWASP API Security Top 10 identifies Broken Object Level Authorization, Broken Authentication, Broken Function Level Authorization and Unrestricted Resource Consumption among major API risks. The P0 security tests focus directly on these categories.

---

## SEC-01 — Authentication failures

Cases:

```text
missing token
invalid signature
expired token
malformed token
```

Expected:

```text
rejected
no business mutation
```

---

## SEC-02 — Role/function authorization

Examples:

```text
Rider calls Driver GoOnline
Rider calls internal ReleaseDriver
Driver calls another Driver's privileged operation
```

Expected rejection.

---

## SEC-03 — Object-level authorization

Rider A requests:

```text
Trip of Rider B
```

or mutating action on it.

Expected:

```text
forbidden/not disclosed according to API policy
```

---

## SEC-04 — Actor-ID spoofing

Client body includes:

```text
driverId = another Driver
```

Server must use authenticated actor identity where command is actor-bound.

---

## SEC-05 — Invalid coordinates/data

Cases:

```text
lat 91
lon 181
NaN/Infinity where parser permits
malformed UUID
unsupported enum/state
```

No business mutation.

---

## SEC-06 — Idempotency-key conflict abuse

Same key with different CreateRide/Payment semantics.

Expected explicit conflict.

---

## SEC-07 — Oversized/resource-consumption request

Bounded payload/body/list size.

System rejects before expensive processing where practical.

This supports OWASP API4-style resource-consumption defense.

---

## SEC-08 — DB credential isolation

Attempt from service role:

```text
Trip → Driver DB
Matching → Driver DB
```

Expected database authorization failure.

This is architecture enforcement evidence.

---

## SEC-09 — Secret/config scan

Assert repository excludes:

- real JWT signing secret;
- DB passwords;
- RabbitMQ credentials;
- tokens.

`.env.example` contains placeholders only.

---

# 11. Invariant auditor

## 11.1 Purpose

The invariant auditor is independent verification tooling.

It is **not** application business code.

It may use read-only access to multiple service databases in test/fault environments because its purpose is to verify the architecture from outside service boundaries.

---

## 11.2 When to run

Mandatory after:

- concurrency scenarios;
- fault/recovery scenarios;
- performance tests involving reservation/payment;
- final demo rehearsal.

---

## 11.3 Core auditor checks

### AUD-01 — Exclusive Driver reservation

For each Driver:

```text
≤1 current active reservation/trip
```

### AUD-02 — Driver state coherence

No:

```text
AVAILABLE + reservationId
RESERVED + null reservationId
```

### AUD-03 — One current Trip assignment

No Trip holds multiple active assignment references.

### AUD-04 — Driver–Trip agreement

Final stable state cross-check:

```text
Trip assignment ↔ Driver current reservation
```

or recognized pending cleanup state only during test observation.

### AUD-05 — Payment logical uniqueness

```text
operationKey unique
one logical effect
```

### AUD-06 — Matching terminal uniqueness

No same `(tripId,round)`:

```text
RESERVED and NO_DRIVER
```

### AUD-07 — Leaked pre-start reservation

After scenario quiescence:

```text
terminal cancelled/match-failed Trip
→ no unintended active Driver reservation
```

### AUD-08 — Outbox/Saga unresolved age

No silently stuck row beyond scenario's accepted terminal/review policy.

---

## 11.4 Auditor result format

Example:

```text
Run: REL-03-20260926-001
Commit: abcdef1

AUD-01 Exclusive Driver Reservation ........ PASS
AUD-02 Driver State Coherence ............... PASS
AUD-03 Single Trip Assignment ............... PASS
AUD-04 Driver/Trip Agreement ................ PASS
AUD-05 Payment Logical Uniqueness ........... PASS
AUD-06 Matching Terminal Uniqueness ......... PASS
AUD-07 No Reservation Leak .................. PASS
AUD-08 Workflow Resolution .................. PASS

Overall: PASS
```

---

# 12. DSI/DSL coverage matrix

Every critical invariant must have an executable proof family.

| Contract | Main tests |
|---|---|
| DSI-001 | CON-01, CON-05, AUD-01 |
| DSI-002 | FUN-02, AUD-02 |
| DSI-003 | FUN-02, CON-02 |
| DSI-004 | CON-04, REL-09 |
| DSI-005 | CON-01 same-ID branch, dedicated integration |
| DSI-006 | REL-13 |
| DSI-007 | REL-12 |
| DSI-008 | CON-01/06, AUD-03 |
| DSI-009 | FUN-05, REL-17 |
| DSI-010 | REL-03/17 |
| DSI-011 | FUN-08, SEC-02 |
| DSI-012 | FUN-09 |
| DSI-013 | FUN-04 concurrent active-trip test |
| DSI-014 | FUN-04/09 fare snapshot assertion |
| DSI-015 | CON-06, FUN-11 |
| DSI-016 | REL-17 / stale-round integration |
| DSI-017 | REL-01/02 |
| DSI-018 | REL-01/02 |
| DSI-019 | deterministic Matching unit/integration fixture |
| DSI-020 | FUN-03/05 stale/ineligible fixture |
| DSI-021 | Progressive-stage unit/integration |
| DSI-022 | REL-01 staged variant |
| DSI-023 | matching-round bounded-attempt test |
| DSI-024 | CON-03, REL-08, AUD-05 |
| DSI-025 | SEC-06 |
| DSI-026 | FUN-08 negative authorization case |
| DSI-027 | FUN-10 / ordering test |
| DSI-028 | REL-10 |
| DSI-029 | Notification failure test |
| DSI-030 | CON-05, AUD-04 |
| DSI-031 | REL-01/02 |
| DSI-032 | REL-03/17 |
| DSI-033 | CON-04, REL-03/09 |
| DSI-034 | CON-03, REL-07/08 |
| DSI-035 | FUN-11, Outbox fault variant |

Liveness:

| Contract | Main tests |
|---|---|
| DSL-001 | REL-01/02 |
| DSL-002 | REL-03/09/15 |
| DSL-003 | FUN-05/11, REL-02 |
| DSL-004 | REL-08/10 |
| DSL-005 | REL-07/09/10 |
| DSL-006 | REL-04/05/18 |
| DSL-007 | notification outage test |

---

# 13. Test oracle design

## 13.1 Avoid response-only oracle

Bad:

```text
Assert one request returned 200
```

This cannot prove no hidden second reservation exists.

---

## 13.2 Multi-source oracle

High-value scenario may assert:

```text
Driver DB
Trip DB
Matching DB
Payment DB
Outbox/dedup rows
RabbitMQ queue state
```

Then telemetry explains the sequence.

---

## 13.3 Quiescence condition

Do not assert “eventual” final state immediately.

Use bounded wait:

```text
await until:
  pending Outbox = 0
  relevant Saga terminal
  queue ready/unacked for scenario = 0
  compensation resolved
or timeout
```

If timeout occurs:

```text
test fails with unresolved-state dump
```

No infinite wait.

---

# 14. Real dependency rule

## 14.1 PostgreSQL

Use real PostgreSQL for claims involving:

- transaction isolation;
- conditional update;
- unique constraint;
- concurrency;
- Outbox atomicity.

An in-memory provider cannot prove PostgreSQL concurrency semantics.

---

## 14.2 RabbitMQ

Use real RabbitMQ for:

- ACK/redelivery;
- publisher confirm;
- quorum behavior;
- DLQ;
- reconnect.

A mocked `IMessageBus.Publish()` cannot prove these.

---

## 14.3 Redis

Use real Redis for:

- GEOSEARCH;
- Redis Function atomicity;
- projection reorder.

---

## 14.4 Testcontainers

Testcontainers is preferred for disposable integration infrastructure because it starts the same classes of real services used by the system while reducing manual port/readiness setup.

Use Compose for multi-service/system/fault topology.

---

# 15. CI execution tiers

## Tier 1 — PR fast

Target:

```text
<5–10 min where practical
```

Runs:

- unit;
- selected WebApplicationFactory;
- selected Testcontainers DB tests;
- deterministic fast invariant tests.

---

## Tier 2 — merge/core integration

Runs:

- full service integration;
- RabbitMQ/Redis;
- core cross-service workflows;
- CON-01 at smaller N;
- critical REL fast cases.

---

## Tier 3 — scheduled/manual reliability

Runs:

- process kills;
- full fault profile;
- N=100 contention;
- backlog recovery;
- 3-node RabbitMQ HA.

---

## Tier 4 — performance campaign

Never on every PR.

Runs on:

- frozen commit;
- stable hardware/environment;
- explicit experiment plan.

---

# 16. Flaky-test policy

A distributed test that passes “most of the time” is not acceptable evidence.

If flaky:

1. reproduce;
2. determine whether product race or test race;
3. remove arbitrary timing assumptions;
4. add barriers/state polling/faultpoints;
5. record failure seed/run ID;
6. do not simply increase sleep.

A flaky S0/S1 test blocks release until understood.

---

# 17. Performance methodology

## 17.1 Performance is not correctness

A system that violates invariant at 1,000 req/s is not better than a correct system at 100 req/s.

Therefore every performance experiment involving business writes runs:

```text
invariant auditor afterward
```

---

## 17.2 Define question before workload

Each experiment begins:

```text
Question:
Hypothesis:
Independent variable:
Controlled variables:
Dataset:
Workload model:
Metrics:
Correctness assertions:
Repetition count:
Stop condition:
Limitations:
```

---

## 17.3 Closed vs open workload

k6 distinguishes:

### Closed model

Next iteration depends on previous iteration finishing.

As SUT slows:

```text
offered load naturally drops
```

This can hide saturation.

### Open model

Arrival-rate executors start iterations independently of response duration.

Use when question is:

> How does SUT behave under a specified offered request rate?

k6 explicitly notes closed models can suffer coordinated omission for this kind of question.

---

## 17.4 `constant-arrival-rate`

For controlled API offered load:

```text
constant-arrival-rate
```

is preferred.

Record:

```text
configured rate
achieved rate
dropped_iterations
```

If dropped iterations rise:

- load generator may have insufficient VUs;
- SUT may be slowing so badly that VUs remain occupied.

Interpret before drawing conclusions.

---

## 17.5 Warm-up

Before measured interval:

```text
warm service/JIT
warm DB connections/pools
warm broker consumers
warm representative caches
```

unless cold-start behavior is specifically being studied.

---

## 17.6 Repeat count

For report-grade main experiment:

```text
3–5 measured independent runs
```

under same frozen configuration.

Report:

- each run;
- median/aggregate summary;
- variance/range where useful.

One run is demonstration, not strong experimental evidence.

---

## 17.7 Percentiles

Report at least:

```text
P50
P95
```

P99 only when sample size is sufficient.

Reasoning:

With:

```text
n = 100
```

the top 1 sample roughly determines the 99th percentile, making it extremely unstable.

With:

```text
n = 1,000
```

the upper 1% contains roughly 10 samples—still sensitive but more interpretable.

Therefore P99 is optional and sample count must accompany it.

Never report only average latency.

---

## 17.8 Thresholds

k6 thresholds are explicit pass/fail criteria over metrics.

Do not invent “industry SOTA” thresholds before baseline.

Recommended workflow:

```text
baseline measurements
→ understand environment
→ choose provisional project threshold
→ rerun
→ report target vs measured result separately
```

---

# 18. PERF/E01 — Driver contention curve

## 18.1 Research question

How does authoritative Driver reservation behave as simultaneous contention rises?

---

## 18.2 Workload

Same Driver:

```text
N = 1
10
25
50
100
```

All contenders released through barrier as simultaneously as practical.

---

## 18.3 Metrics

```text
winner count
conflict count
system-error count
P50/P95 request latency
DB transaction duration if observable
CPU
DB connection utilization
```

---

## 18.4 Correctness oracle

For every run:

```text
winner = 1
DSI-001 violations = 0
AUD-01 = PASS
```

---

## 18.5 Interpretation

Expected:

```text
conflict count increases with N
```

This demonstrates synchronization working, not failure.

Question is not:

> How do we eliminate conflicts?

Question:

> Can the authority resolve them correctly and at what cost?

---

# 19. PERF/E02 — Controlled offered-load API baseline

## 19.1 Research question

At fixed arrival rates, how do latency, errors, dropped iterations and correctness change?

---

## 19.2 Workload

After baseline:

```text
10 req/s
25 req/s
50 req/s
100 req/s
```

Higher only if environment still yields useful data.

Use one stable representative API mix or one focused endpoint per experiment.

---

## 19.3 Executor

Prefer:

```text
constant-arrival-rate
```

for offered-load question.

---

## 19.4 Metrics

```text
configured arrival rate
achieved iterations
dropped_iterations

P50
P95
optional P99

HTTP/system failure rate
domain conflict rate separately
CPU
memory
DB connections
queue depth if async path
```

Do not merge:

```text
409 expected conflict
```

into:

```text
500 system error
```

---

## 19.5 Saturation signal

Possible saturation indicators:

- P95 rising sharply;
- dropped iterations;
- growing queue;
- DB pool exhaustion;
- CPU ceiling;
- system errors.

Do not declare one universal “max throughput” without stating bottleneck and environment.

---

# 20. PERF/E03 — Async consumer scaling

## 20.1 Research question

How does async throughput/recovery change with consumer replica count?

---

## 20.2 Independent variable

```text
consumers = 1
2
4
```

Optional:

```text
8
```

only if useful.

---

## 20.3 Controlled variables

Keep fixed:

```text
prefetch
DB pool
message size
dataset
broker topology
arrival rate
retry config
hardware allocation
```

Do not change prefetch and consumer count simultaneously.

---

## 20.4 Metrics

```text
messages/s
processing latency
queue ready
queue unacked
redelivery
dedup
DB load
CPU
```

---

## 20.5 Correctness

After run:

```text
business duplicate count = 0
auditor green
```

---

## 20.6 Expected nonlinearity

Doubling consumers does not imply doubling throughput.

Possible limiting factors:

- PostgreSQL;
- broker;
- CPU;
- connection pool;
- lock/contention.

The experiment identifies the observed bottleneck rather than assuming scaling.

---

# 21. PERF/E04 — Outage/backlog/recovery experiment

## 21.1 Research question

Can the system recover accumulated durable work after a controlled outage, and how long does it take?

---

## 21.2 Model

Let:

```text
λ = incoming messages/s
T = outage duration
B ≈ λT
```

After recovery:

```text
μ = effective processing rate
```

If:

```text
μ > λ
```

approximate drain time:

```text
T_drain ≈ B / (μ - λ)
```

---

## 21.3 Example planning calculation

Suppose:

```text
λ = 20 msg/s
T = 60 s
```

Expected backlog:

```text
B ≈ 1,200
```

If after recovery:

```text
μ = 50 msg/s
```

net drain:

```text
30 msg/s
```

approximate:

```text
T_drain ≈ 40 s
```

Measured result will differ due to:

- reconnect;
- warm-up;
- prefetch;
- DB latency;
- retry;
- service scheduling.

The difference itself is useful analysis.

---

## 21.4 Procedure

1. warm stable system;
2. establish λ;
3. stop/isolate target consumer;
4. maintain producer load for controlled T;
5. record peak backlog;
6. restore;
7. measure queue drain/recovery;
8. wait for quiescence;
9. run invariant auditor.

---

## 21.5 Metrics

```text
actual λ
outage duration
peak B
μ during recovery
time-to-first-success
time-to-zero/steady backlog
redelivery
dedup
system errors
```

---

# 22. Optional experiments only after Core Gate

Possible P1:

- gRPC vs REST one narrow path;
- different prefetch values;
- Redis candidate-count/radius effect;
- richer fairness ranking comparison;
- Outbox batch-size tuning.

Not allowed to displace P0 proofs.

---

# 23. Experimental validity

## 23.1 Internal validity

Ask:

> Did the independent variable cause the measured difference?

Control:

- code commit;
- config;
- dataset;
- hardware;
- background processes;
- warm-up.

---

## 23.2 External validity

Course hardware is not production fleet scale.

Correct statement:

> “Under the recorded local/container environment…”

Incorrect:

> “The system can handle Uber-scale traffic.”

---

## 23.3 Construct validity

Ensure metric measures intended concept.

Example:

```text
HTTP 200 count
```

is not the correct measure for:

```text
exclusive Driver reservation safety
```

Use invariant audit.

---

## 23.4 Repeatability

Another team member should be able to run:

```text
script
→ same setup
→ same expected invariant
```

Exact latency can vary.

Correctness outcome should not.

---

# 24. Statistical/reporting discipline

## 24.1 Always report sample count

A percentile without:

```text
n
```

is incomplete.

---

## 24.2 Separate semantic outcomes

For contention:

```text
success
domain conflict
system error
timeout/unknown
```

must be separate.

---

## 24.3 Avoid fake precision

Do not report:

```text
P95 = 83.472819 ms
```

as meaningful precision if environment jitter is milliseconds.

Use sensible significant figures.

---

## 24.4 Do not cherry-pick best run

Store all planned measured runs.

If one is discarded:

- record reason;
- distinguish invalid setup from bad system result.

---

# 25. Evidence bundle specification

Every high-value scenario folder:

```text
evidence/<scenario>/<runId>/
├─ manifest.json
├─ README.md
├─ test-output.txt
├─ final-state/
├─ metrics/
├─ traces/
├─ logs/
└─ screenshots/        # optional/supporting
```

---

## 25.1 `manifest.json`

Conceptual:

```json
{
  "scenario": "REL-06",
  "runId": "REL-06-20260926-001",
  "commit": "abcdef1",
  "profile": "fault",
  "startedAtUtc": "...",
  "environment": {
    "cpu": "...",
    "ram": "...",
    "docker": "...",
    "dotnet": "10.0.x",
    "postgres": "18.x",
    "rabbitmq": "4.3.x",
    "redis": "8.10.x"
  },
  "config": {
    "prefetch": 16,
    "faultPoint": "Consumer.AfterCommitBeforeAck"
  },
  "result": "PASS"
}
```

---

## 25.2 README

Explain:

```text
Claim:
Setup:
Fault/Race:
Expected:
Observed:
Final-state assertion:
Limitations:
Files:
```

---

# 26. Trace evidence

## 26.1 Happy trace

Should show a representative chain:

```text
HTTP CreateRide
→ Trip DB
→ Outbox
→ Rabbit publish
→ Matching consumer
→ Driver HTTP reserve
→ Driver DB/Outbox
→ Rabbit
→ Trip consumer
```

---

## 26.2 Failure trace

Preferred:

```text
Payment failure
→ Saga compensation
→ Driver release
```

or:

```text
Reserve timeout
→ reconcile same reservation
```

---

## 26.3 Trace limitations

OpenTelemetry messaging conventions are currently in Development state.

Therefore report focuses on:

- causal path;
- business IDs;
- spans/latency;

not the permanence of one exact attribute-name version.

---

# 27. Security evidence contract

Each security case records:

```text
actor
token/role condition
resource
attempted action
expected HTTP/result class
final authoritative state unchanged?
```

For invalid data:

```text
payload category
validation result
business mutation count
```

---

# 28. RabbitMQ HA validation

## 28.1 Setup

Three RabbitMQ nodes.

Critical queues as quorum queues.

Verify membership before fault.

---

## 28.2 Baseline

Before killing node:

- publish confirmed messages;
- consume/ACK;
- record queue state.

---

## 28.3 Failure

Kill one node.

Continue controlled publication/consumption.

Observe:

- confirm behavior;
- leader changes/recovery;
- connection recovery;
- queue availability.

RabbitMQ documents quorum queues as replicated queues based on Raft and confirms publication only after quorum replication sufficient for queue safety.

---

## 28.4 Recovery

Restore node.

Record:

- membership;
- synchronization;
- workload continuation.

---

## 28.5 Claim boundary

The experiment proves only:

```text
tested RabbitMQ quorum behavior
under one-node loss while majority remains
```

It does not prove:

- PostgreSQL HA;
- Redis HA;
- multi-region resilience;
- full application zero downtime.

---

# 29. Demo design principles

Course presentation suggests approximately:

```text
3–5 min problem/architecture
7–10 min demo
~5 min oral examination
```

The demo must:

- involve at least three nodes/clients/services;
- show network communication;
- include concurrency;
- include live failure;
- show logs/traces;
- involve all members.

The runbook below optimizes for maximum distributed-systems evidence per minute.

---

# 30. Recommended final demo — 9-minute core

## Segment 0 — topology / 45 s

Show:

```text
Gateway
Trip
Matching
Driver
Payment
RabbitMQ
PostgreSQL
Redis
OTel
```

State thesis:

> One Driver cannot be double-assigned; uncertain/duplicated work is reconciled using durable identity/state.

---

## Segment 1 — happy ride / 2 min

Run one ride quickly:

```text
Driver AVAILABLE
Rider CreateRide
Matching
Driver reserved
accept
Payment authorize
start
complete
capture
```

Show one trace.

Purpose:

- prove integrated system;
- establish entities before fault demo.

---

## Segment 2 — live concurrency / 2 min

Run:

```text
10 or 50 contenders → one Driver
```

Show:

```text
one winner
N-1 conflicts
AUD-01 PASS
```

Do not use N=100 if it risks demo timing; N=100 remains recorded evidence.

Member B explains linearization/OCC.

---

## Segment 3 — live failure / 2.5 min

Preferred deterministic scenario:

### Option A — consumer commit-before-ACK

```text
message delivered
consumer commits
faultpoint pause
kill process
broker redelivers
dedup hit
effect count remains one
```

Excellent RabbitMQ proof.

### Option B — reserve response loss

```text
Driver commits
response suppressed
Matching UNKNOWN
same reservation reconciled
no second Driver
```

Excellent DS reasoning proof.

Choose one live.

Keep the other as evidence/backup.

---

## Segment 4 — Saga compensation / 1.5 min

Force Payment decline:

```text
reservation exists
Payment fails
Trip terminal
exact release
Driver available
```

Show final auditor.

Member A/C explain Saga and operation identity.

---

## Segment 5 — conclusion / 15–30 s

Show:

```text
all invariant checks green
```

State claim narrowly.

---

# 31. Demo role split

## Member A

Explains:

- Trip FSM;
- Saga;
- Outbox;
- compensation;
- terminal monotonicity.

Runs/owns compensation segment.

---

## Member B

Explains:

- Driver authority;
- OCC;
- Redis derived state;
- UNKNOWN reservation;
- performance.

Runs contention/reservation fault segment.

---

## Member C

Explains:

- RabbitMQ confirm/ACK;
- dedup/DLQ;
- Payment idempotency;
- observability;
- broker recovery.

Runs messaging failure segment or Payment path.

---

# 32. Demo fallback strategy

Live demo can fail for non-semantic reasons.

Prepare:

```text
live primary path
+ one-click rerun script
+ raw recorded evidence
+ screenshots/trace export
```

Fallback evidence must be from release commit or clearly labeled earlier run.

Never fake output.

---

# 33. Oral-defense question bank

## Architecture

1. Why five business services?
2. Why isn't Notification authoritative?
3. Why one physical PostgreSQL server does not mean one logical database?
4. Why not Kubernetes?
5. Why RabbitMQ rather than Kafka?

## Consistency

6. What is strong locally vs eventual globally?
7. Why may Redis be stale safely?
8. What is the linearization point for Driver reservation?
9. Why not use Redis lock?
10. What is fencing?

## Unknown outcome

11. Why does timeout not mean failure?
12. What if Driver committed but response was lost?
13. Why cannot Matching immediately try the next Driver?
14. Why same reservationId?

## Messaging

15. Publisher confirm vs consumer ACK?
16. Why are they orthogonal?
17. Why ACK after commit?
18. Why can Outbox publish duplicates?
19. Why consumer dedup + FSM guard both needed?
20. Why not claim exactly once?

## Saga

21. Why no distributed ACID?
22. What is compensation?
23. Why compensation itself must be idempotent?
24. What is the pivot?
25. Why capture failure does not un-complete Trip?

## Fault tolerance

26. What state survives process crash?
27. What does automatic RabbitMQ recovery not solve?
28. Why faultpoints instead of sleep?
29. What does DLQ solve?
30. What if compensation keeps failing?

## Performance

31. Why open arrival-rate model?
32. What is coordinated omission?
33. What are dropped iterations?
34. Why P95?
35. Why conflict ≠ error?
36. Why benchmark must run invariant auditor?
37. Why 3–5 repetitions?
38. What does E04 formula approximate?

## Security

39. What is object-level authorization?
40. How is Driver actor identity protected?
41. Why service DB credentials differ?

## Limitations

42. What happens if PostgreSQL host dies?
43. Is system highly available?
44. What production features are intentionally excluded?
45. Which claims are course-scale only?

---

# 34. Claim discipline

## Allowed claim

> Under synchronized contention tests with up to N=100 on the recorded environment, one Driver reservation remained authoritative and the invariant auditor reported zero exclusive-reservation violations across the recorded runs.

## Not allowed

> Our Driver allocation is proven safe at arbitrary scale.

---

## Allowed

> RabbitMQ delivery is treated as at-least-once; duplicate publication/redelivery is handled through stable message identity, consumer deduplication and domain guards.

## Not allowed

> RabbitMQ gives us exactly once.

---

## Allowed

> The three-node quorum profile was validated under loss of one RabbitMQ node while a majority remained.

## Not allowed

> The system is fully highly available.

---

## Allowed

> E02 measured P95 latency under 50 req/s on the specified course environment.

## Not allowed

> The architecture supports 50 req/s in production universally.

---

# 35. Release gates

## G0 — Foundation

Required:

```text
clean clone boots
service DNS
health endpoints
DB roles/isolation
real integration fixtures
```

---

## G1 — Domain/core correctness

```text
FUN base ride path
CON-01
CON-02
CON-03
CON-04
core DSI auditor
```

No messaging sophistication can excuse red core invariants.

---

## G2 — Messaging reliability

```text
REL-04 Outbox crash
REL-05 duplicate publish
REL-06 commit-before-ACK
REL-11 poison/DLQ
```

---

## G3 — Workflow recovery

```text
REL-01/02 UNKNOWN reserve
REL-07 Saga restart
REL-08 Payment unknown
REL-09 compensation
REL-10 forward recovery
```

---

## G4 — Final Core Release Gate

All mandatory:

```text
[ ] happy ride path
[ ] CON-01 exclusive Driver
[ ] CON-02 GoOffline/Reserve
[ ] CON-03 Payment idempotency
[ ] CON-04 stale compensation
[ ] REL-01 reserve unknown
[ ] REL-02 Matching crash/reconcile
[ ] REL-04 Outbox crash
[ ] REL-05 duplicate publish
[ ] REL-06 commit-before-ACK
[ ] REL-07 Saga restart
[ ] REL-09 compensation
[ ] REL-11 poison/DLQ
[ ] SEC minimum + ownership isolation
[ ] zero known S0/S1 correctness defect
[ ] invariant auditor green
```

Only after G4:

- optional P1;
- extensive tuning;
- additional experiments.

---

## G5 — Final Validation Gate

```text
[ ] PERF/E01
[ ] PERF/E02
[ ] PERF/E03
[ ] PERF/E04
[ ] REL-15 reconnect/backlog
[ ] REL-16 Rabbit quorum node loss
[ ] release-commit evidence bundle
[ ] clean-clone demo rehearsal
```

---

# 36. Report integration

The final report testing/results section should not be a giant table of “Pass.”

Use structure:

## 36.1 Verification strategy

Explain:

- authority-based oracle;
- deterministic faults;
- real dependencies;
- auditor.

## 36.2 Core correctness results

Select strongest:

- CON-01;
- REL-01;
- REL-06;
- REL-09.

## 36.3 Failure/recovery results

Timeline diagrams + final state.

## 36.4 Performance

E01–E04.

## 36.5 Security

Compact summary.

## 36.6 Threats to validity

Explicit limitations.

Appendix contains full test matrix/raw links.

---

# 37. Threats to validity

Required final report discussion.

## Environment

- course hardware;
- Docker overhead;
- shared physical PostgreSQL.

## Workload realism

- synthetic ride/Driver locations;
- simulated Payment;
- bounded candidate counts.

## Failure coverage

Not tested:

- Byzantine services;
- disk corruption;
- majority RabbitMQ loss;
- PostgreSQL HA failover;
- Redis cluster failover.

## Timing

Container scheduling/network differs from real cloud environment.

## Scale

Hundreds of simulated Drivers ≠ production marketplace.

## Observability

Trace sampling/export can lose data; evidence must not depend solely on traces.

---

# 38. Current-source validation notes

The methodology in this document is cross-checked against current primary sources as of 2026-09-26.

## RabbitMQ

RabbitMQ 4.3 documentation states:

- publisher confirms and consumer acknowledgements are orthogonal;
- acknowledgements are essential to reliable ownership transfer;
- quorum queues are replicated using Raft;
- quorum publishers should use confirms;
- quorum consumers should use manual ACK.

These directly support REL-05/06/16.

## k6

Current k6 documentation states:

- closed models couple arrival to iteration completion;
- arrival-rate executors use an open model;
- `constant-arrival-rate` schedules iterations independently of response duration;
- `dropped_iterations` records scheduled iterations that could not begin;
- thresholds provide explicit pass/fail criteria.

These support E02 methodology.

## Testcontainers / ASP.NET

Testcontainers for .NET supports disposable real infrastructure such as PostgreSQL for integration tests.

ASP.NET Core supports application integration tests with `WebApplicationFactory`.

Use mocks for logic tests, not for proving external technology semantics.

## OWASP

API Security Top 10 2023 explicitly highlights:

- Broken Object Level Authorization;
- Broken Authentication;
- Broken Function Level Authorization;
- Unrestricted Resource Consumption.

The security campaign maps to those high-value risks rather than trying to perform a full penetration-testing program.

## OpenTelemetry

Messaging semantic conventions remain Development.

Telemetry is used for explanation and measurements, while authoritative final-state checks remain independent.

---

# 39. External authoritative references

## Course

- `COURSE PROJECT SPECIFICATION.pdf`

## RabbitMQ

1. Consumer Acknowledgements and Publisher Confirms  
   https://www.rabbitmq.com/docs/confirms

2. Reliability Guide  
   https://www.rabbitmq.com/docs/reliability

3. Quorum Queues  
   https://www.rabbitmq.com/docs/quorum-queues

## Grafana k6

4. Open and Closed Models  
   https://grafana.com/docs/k6/latest/using-k6/scenarios/concepts/open-vs-closed/

5. Constant Arrival Rate  
   https://grafana.com/docs/k6/latest/using-k6/scenarios/executors/constant-arrival-rate/

6. Dropped Iterations  
   https://grafana.com/docs/k6/latest/using-k6/scenarios/concepts/dropped-iterations/

7. Thresholds  
   https://grafana.com/docs/k6/latest/using-k6/thresholds/

## Testing

8. Testcontainers for .NET  
   https://testcontainers.com/guides/getting-started-with-testcontainers-for-dotnet/

9. ASP.NET Core Integration Tests  
   https://learn.microsoft.com/en-us/aspnet/core/test/integration-tests?view=aspnetcore-10.0

## PostgreSQL

10. PostgreSQL 18 Unique Indexes  
    https://www.postgresql.org/docs/18/indexes-unique.html

11. PostgreSQL 18 Transaction Isolation  
    https://www.postgresql.org/docs/18/transaction-iso.html

## Security

12. OWASP API Security Project  
    https://owasp.org/projects/api-security-project

13. OWASP API Security Top 10 — 2023  
    https://api-security.owasp.org/editions/2023/en/0x11-t10/

## OpenTelemetry

14. Messaging semantic conventions  
    https://opentelemetry.io/docs/specs/semconv/messaging/

15. Messaging metrics  
    https://opentelemetry.io/docs/specs/semconv/messaging/messaging-metrics/

---

# 40. Freeze checklist

Before promoting Doc 03 to **ACCEPTED/FROZEN**:

## Course compliance

- [ ] ≥8 functional tests;
- [ ] ≥2 concurrency;
- [ ] ≥2 fault/recovery;
- [ ] ≥2 security;
- [ ] ≥1 performance experiment;
- [ ] concurrent scenario planned;
- [ ] live failure planned;
- [ ] logs/traces included;
- [ ] all team members have demo roles.

## Correctness coverage

- [ ] DSI-001 mapped;
- [ ] DSI-004 mapped;
- [ ] DSI-008 mapped;
- [ ] DSI-010 mapped;
- [ ] DSI-017 mapped;
- [ ] DSI-024 mapped;
- [ ] DSI-028 mapped;
- [ ] DSI-031 mapped;
- [ ] DSI-033 mapped;
- [ ] DSI-035 mapped;
- [ ] all DSLs mapped.

## Fault determinism

- [ ] commit-before-ACK exact faultpoint;
- [ ] reserve commit-before-response exact faultpoint;
- [ ] Outbox publish/mark window;
- [ ] Saga restart;
- [ ] no critical test relies on arbitrary sleep.

## Infrastructure realism

- [ ] real PostgreSQL for DB concurrency;
- [ ] real RabbitMQ for delivery semantics;
- [ ] real Redis for GEO/function behavior;
- [ ] 3-node RabbitMQ for HA validation.

## Performance validity

- [ ] question first;
- [ ] workload model justified;
- [ ] fixed environment;
- [ ] warm-up defined;
- [ ] run count defined;
- [ ] sample count reported;
- [ ] P50/P95;
- [ ] conflicts separate from system errors;
- [ ] dropped iterations reported for arrival-rate tests;
- [ ] invariant audit after load;
- [ ] raw output retained.

## Security

- [ ] authentication negative cases;
- [ ] role/function authorization;
- [ ] object-level authorization;
- [ ] actor spoofing;
- [ ] malformed coordinates/IDs;
- [ ] resource-bound test;
- [ ] DB role isolation;
- [ ] secret scan.

## Evidence

- [ ] run manifest;
- [ ] commit SHA;
- [ ] environment;
- [ ] config;
- [ ] raw logs/metrics;
- [ ] final-state assertion;
- [ ] auditor;
- [ ] no result represented only by screenshot.

## Claims

- [ ] no exactly-once transport claim;
- [ ] no full-system HA claim;
- [ ] no production-scale claim;
- [ ] targets separated from measurements;
- [ ] limitations documented.

---

# 41. North star

> **Every important distributed-systems claim must survive one adversarial question: “Can you make that failure or race happen on demand, and can you prove from authoritative final state—not from hope, timing, or screenshots—that the invariant still holds?”**

> **If the answer is no, the claim is research/design intent, not yet verified engineering evidence.**
