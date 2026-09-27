# DS03 DISTRIBUTED RIDE-HAILING SYSTEM — MEMBER A EXECUTION PLAYBOOK
## Trip Authority · FareSnapshot · Transactional Outbox · Persisted Saga · Compensation · Workflow Recovery

> **Document:** `docs/05A-member-a-trip-workflow.md`  
> **Course:** Distributed Systems  
> **Topic:** DS03 — Ride-hailing Microservices System simulating Grab/Uber  
> **Type:** Individual Execution Playbook / 16-week chronological delivery guide  
> **Status:** **PROPOSED FREEZE v2.0 — REVIEW CANDIDATE**  
> **Baseline date:** **2026-09-26**  
> **Member:** **A**  
> **Primary role:** Trip Service · Trip FSM · FareSnapshot · Trip assignment state · Rider create/cancel · Driver accept/start/complete integration · Trip Outbox · persisted Ride Saga · compensation · Trip-side idempotency · workflow recovery  
> **Secondary role:** reviewer for Driver/Matching assignment semantics, Payment operation identity, cross-service invariant auditor and architecture/report consistency  
> **Planning horizon:** 16 weeks  
> **Physical planning envelope:** **~151 focused hours**  
> **Average:** ~9.4 focused h/week  
> **Heavy weeks:** W9–W11  
> **Normative hierarchy:** `00 → 01 → 02 → 03 → 04 → 05A`  
> **Execution rule:** this playbook refines task order, theory, implementation details, tests, review and evidence. It MUST NOT redefine domain semantics, invariants, delivery guarantees or team gates owned by Docs 00–04.

> **Member A north star.**  
> **Trip Service is correct only if the ride lifecycle remains legal after duplicate events, stale assignment results, process restart, Payment failure and delayed compensation. A's responsibility is not merely to make the ride reach COMPLETED; it is to make the workflow's state and outgoing intent durable enough that the system can always explain what happened and continue safely.**

---

# 0. How A should use this playbook

At the beginning of every week, A should be able to answer:

1. Which Trip/Saga invariant is this week's work protecting?
2. What is authoritative local state?
3. What distributed side effect can fail after the local transaction?
4. Which stable identity allows safe replay or compensation?
5. Which exact test/fault scenario proves the mechanism?
6. Which teammate must review the boundary?
7. What final authoritative state closes the issue?

Each week is structured as:

```text
objective
→ theory/knowledge
→ issue pack
→ recommended implementation order
→ failure windows
→ tests/evidence
→ review/handoff
→ exit criteria
```

Do not pull a future feature forward while the current correctness gate is red.

---

# 1. Member A role contract

## 1.1 What A owns

| Area | A owns | A must not silently own |
|---|---|---|
| Trip aggregate | Trip state, legal transitions, terminal monotonicity | Driver availability |
| Ride creation | request idempotency, FareSnapshot, active-trip guard | Identity implementation internals |
| Assignment reference | `assignedDriverId`, `reservationId`, `matchingRound` stored in Trip | deciding Driver reservation success |
| Rider cancellation | Trip terminal cancellation semantics, cleanup intent | direct Driver DB mutation |
| Driver lifecycle intake | accepted/rejected/start/complete Trip-side transitions | Driver FSM authority |
| Saga | persisted ride workflow, current step, compensation sequence, pivot/forward recovery | 2PC/global rollback |
| Trip Outbox | local state + required outgoing intent atomicity | RabbitMQ topology details |
| Payment coordination | operation-key creation/lineage, Trip-side ordering | provider/Payment operation authority |
| Workflow recovery | Saga/Outbox restart state | log-based/manual reconstruction |
| Trip observability | Trip/Saga/Outbox domain metrics/spans | broker platform metrics |
| Evidence | Trip/Saga/Outbox fault proofs and report material | primary load harness ownership |

---

## 1.2 A's canonical correctness contract

A is primary owner or co-owner of:

```text
DSI-008  Single Current Trip Assignment
DSI-009  Assignment Identity Coherence
DSI-010  Terminal Monotonicity
DSI-011  Start Requires Current Assignment
DSI-012  Completion Requires In-Progress
DSI-013  One Active Trip per Rider
DSI-014  Fare Snapshot Stability

DSI-026  Authorization Before Start
DSI-027  Capture Ordering
DSI-028  Completed Trip Does Not Regress on Capture Failure

DSI-030  Driver–Trip Assignment Agreement
DSI-032  Cancellation Does Not Regress
DSI-033  Exact Cleanup Identity
DSI-034  One Logical Payment Effect per Saga Step
DSI-035  Durable Terminal Matching Result (consumer side)
```

A owns liveness for:

```text
DSL-002  Pre-start Reservation Cleanup
DSL-005  Saga Workflow Resolution
DSL-006  Outgoing Intent Is Not Silently Lost
```

A must review B/C mechanisms protecting:

```text
DSI-001  Exclusive Driver Reservation
DSI-004  Guarded Reservation Release
DSI-017  Unresolved Attempt Blocks Next Candidate
DSI-024  Payment Operation Idempotency
DSI-031  No New Driver Before UNKNOWN Resolved
```

---

## 1.3 A's four defense anchors

A must be able to explain without opening source code:

### 1. Transactional Outbox

Why this is unsafe:

```text
COMMIT Trip
→ process crashes
→ publish never happens
```

and why:

```text
Trip/Saga state + Outbox row
```

must commit atomically.

### 2. Saga versus rollback

A Saga is:

```text
sequence of local transactions
+ durable coordination
+ compensation/forward recovery
```

not:

```text
global database rollback
```

### 3. Terminal monotonicity

Why:

```text
CANCELLED / MATCH_FAILED / COMPLETED
```

cannot be reactivated by late Driver/Payment events.

### 4. Exact compensation

Why:

```text
ReleaseDriver(driverId)
```

is unsafe and:

```text
ReleaseDriver(tripId, reservationId)
```

is required.

---

# 2. Theory A must master

## 2.1 Aggregate root as consistency guardian

Microsoft's current .NET microservice DDD guidance describes the aggregate root as the consistency guardian and normal entry point for updates inside an aggregate.

For Trip:

```text
Trip
├─ current state
├─ FareSnapshot
├─ assignment reference
└─ lifecycle metadata
```

must not become a bag of public setters.

Preferred:

```text
trip.StartMatching(...)
trip.ApplyDriverReserved(...)
trip.RecordDriverAccepted(...)
trip.MarkPaymentAuthorized(...)
trip.Start(...)
trip.Complete(...)
trip.Cancel(...)
```

Each method checks legal current state and identity.

---

## 2.2 Local atomicity versus distributed consistency

A can make this atomic:

```text
Trip row
Saga row
Outbox row
```

because they live in one Trip database transaction.

A cannot make this one PostgreSQL transaction:

```text
Trip DB
Driver DB
Payment DB
RabbitMQ
```

Therefore:

```text
local strong consistency
+
distributed Saga/eventual convergence
```

is intentional.

---

## 2.3 Why Outbox is needed

Dual-write failure:

```text
Trip commits CANCELLED
→ crash
→ ReleaseDriver message never published
```

Without durable outgoing intent, Driver may remain leaked.

Outbox changes this into:

```text
BEGIN
  Trip CANCELLED
  Saga COMPENSATING
  Outbox ReleaseDriver(R)
COMMIT
```

Now crash only delays publication.

---

## 2.4 Why Outbox can publish duplicates

Suppose:

```text
relay publishes M
broker accepts
relay crashes before marking M PUBLISHED
```

After restart:

```text
M publishes again
```

Therefore Outbox gives:

```text
durable intent / no silent loss
```

not:

```text
exactly one broker publication
```

Consumers need dedup/state guards.

RabbitMQ's current documentation similarly separates publisher confirms from consumer acknowledgements and treats retransmission/redelivery as part of reliable messaging. 

---

## 2.5 Saga, compensation and pivot

Microsoft's current Saga guidance distinguishes:

```text
compensable transactions
pivot / point of no return
retryable forward transactions
```

For this project:

Before physical ride begins:

```text
Driver reservation
Payment authorization
```

can be compensated.

After:

```text
IN_PROGRESS
```

the physical service has begun.

After:

```text
COMPLETED
```

capture failure cannot make the ride “not happen.”

Therefore:

```text
COMPLETED
+
CAPTURE_PENDING/REQUIRES_REVIEW
```

is legal.

---

## 2.6 Stable business identities

A must never confuse:

```text
tripId
matchingRound
reservationId
operationKey
messageId
correlationId
traceId
```

Key rules:

```text
reservationId
→ exact Driver reservation/release identity

operationKey
→ same logical Payment operation

messageId
→ one logical integration message

correlationId/traceId
→ diagnostics, not business correctness
```

---

## 2.7 State guard plus dedup

Dedup alone protects:

```text
same messageId replay
```

It does not protect:

```text
different old messageId arriving late
```

Therefore Trip consumers use:

```text
processed-message dedup
+
current Trip state
+
matchingRound/reservationId/operationKey guard
```

---

# 3. A's recommended code shape

Exact folders may vary, but responsibilities should remain recognizable.

```text
src/
└─ TripService/
   ├─ Api/
   │  ├─ CreateRide/
   │  ├─ CancelRide/
   │  ├─ DriverActions/
   │  ├─ StartTrip/
   │  └─ CompleteTrip/
   │
   ├─ Domain/
   │  └─ Trips/
   │     ├─ Trip.cs
   │     ├─ TripState.cs
   │     ├─ FareSnapshot.cs
   │     ├─ TripAssignment.cs
   │     ├─ TripErrors.cs
   │     └─ ...
   │
   ├─ Application/
   │  ├─ Commands/
   │  ├─ Consumers/
   │  ├─ Sagas/
   │  └─ Queries/
   │
   ├─ Infrastructure/
   │  ├─ Persistence/
   │  ├─ Idempotency/
   │  ├─ Outbox/
   │  └─ Messaging/
   │
   └─ Observability/

tests/
├─ TripService.UnitTests/
├─ TripService.IntegrationTests/
└─ SystemTests/
```

Domain layer should not depend directly on:

- RabbitMQ client;
- EF Core types where avoidable;
- HTTP request models;
- OpenTelemetry.

---

# 4. A's persistence model

## 4.1 `trips`

Conceptual:

```text
trip_id
rider_id
state
version

pickup
dropoff

fare_amount
fare_currency
fare_policy_version

matching_round

assigned_driver_id?
reservation_id?

created_at
updated_at
completed_at?
cancelled_at?
```

---

## 4.2 Request idempotency

Conceptual:

```text
request_idempotency
- scope
- key
- request_fingerprint
- resource_id
- created_at
```

Constraint:

```text
UNIQUE(scope, key)
```

---

## 4.3 `ride_sagas`

Conceptual:

```text
saga_id
trip_id UNIQUE
status
current_step
step_version

reservation_id?
authorize_operation_key?
capture_operation_key?
void_operation_key?

last_error_code?
requires_review_reason?
created_at
updated_at
```

---

## 4.4 `outbox_messages`

A shares relay mechanics with C, but Trip owns semantic creation of its Outbox records.

Important:

```text
messageId generated once
payload serialized from committed logical intent
```

---

# 5. A's contract map

## 5.1 Trip-produced commands/events

A's domain/workflow causes:

```text
RideRequestedV1
CancelMatchingRoundV1

AuthorizePaymentV1
VoidPaymentAuthorizationV1
CapturePaymentV1

ReleaseDriverReservationV1
MarkDriverTripStartedV1
MarkDriverTripCompletedV1
```

Exact owner/topology remains Doc 02.

---

## 5.2 Trip-consumed facts

A consumes:

```text
DriverReservedV1
MatchingFailedV1
DriverTripStartedV1
DriverTripCompletedV1

PaymentAuthorizedV1
PaymentAuthorizationFailedV1
PaymentAuthorizationVoidedV1
PaymentCapturedV1
PaymentCapturePendingV1
PaymentRequiresReviewV1
```

Some Driver accept/reject inputs may be public Driver actions routed through Trip depending on final API contract; the semantic rule remains that Trip decides only Trip lifecycle.

---

# 6. Personal evidence map

A's evidence directories:

```text
evidence/member-a/
├─ w03-trip-fsm/
├─ w04-create-idempotency/
├─ w06-assignment/
├─ w07-cancel-races/
├─ w09-outbox/
├─ w10-saga/
├─ w11-fault-proofs/
├─ w12-core-gate/
├─ w13-perf-support/
├─ w14-ha-recovery/
└─ final/
```

Each important scenario:

```text
README.md
manifest.json
test-output.txt
final-state/
trace-reference.txt
metrics/
```

---

# 7. A's test ownership

## Primary

```text
FUN-04  Create Ride idempotency
FUN-06  Driver accept → Trip legal transition
FUN-07  Payment authorization → READY_TO_START
FUN-08  Start Trip guard
FUN-09  Complete Trip
FUN-10  Capture ordering/result
FUN-12  Pre-start cancellation

REL-03  cancel + unknown reservation cleanup (shared B)
REL-04  state+Outbox commit then crash
REL-05  duplicate publication/lost mark (shared C)
REL-07  Saga restart
REL-09  Payment failure compensation
REL-10  capture failure after completion
REL-17  late stale event after terminal Trip

AUD-03  one current Trip assignment
AUD-04  Driver–Trip agreement
AUD-07  no leaked pre-start reservation
AUD-08  workflow resolution
```

## Reviewer

```text
CON-01 Driver contention
CON-04 stale compensation
REL-01 reserve response loss
REL-02 Matching crash/reconcile
CON-03 Payment idempotency
REL-08 Payment timeout-after-effect
```

---

# 8. Capacity map

| Week | Planned A hours | Primary focus |
|---:|---:|---|
| W1 | 8 | architecture/correctness comprehension |
| W2 | 9 | service foundation/contracts |
| W3 | 9 | Trip FSM/domain |
| W4 | 9 | ride creation/idempotency/FareSnapshot |
| W5 | 9 | matching-round Trip integration |
| W6 | 9 | assignment/current-round guards |
| W7 | 10 | cancellation/fencing/races |
| W8 | 10 | RabbitMQ Trip integration |
| W9 | 11 | Trip Outbox/dedup |
| W10 | 13 | persisted Saga/Payment/compensation |
| W11 | 11 | deterministic fault proofs |
| W12 | 9 | observability/security/Core Gate |
| W13 | 8 | performance correctness support |
| W14 | 7 | backlog/HA application review |
| W15 | 9 | freeze/report/evidence |
| W16 | 10 | release/demo/defense |
| **Total** | **151 h** | — |

---

# 9. Week 1 — Understand Trip authority before coding
**Planned: 8 h**

## 9.1 Objective

A can explain:

```text
Trip authority
Trip FSM
FareSnapshot
assignment identity
Outbox
Saga
compensation
terminal monotonicity
```

without implementation.

---

## 9.2 Required reading

Read project:

```text
00:
scope
service ownership
distributed-systems foundations
Trip/Matching/Payment boundaries

01:
Trip state machine
DSI-008..014
DSI-026..035
DSL-002/005/006
DSR cancel/reserve/payment races

02:
communication matrix
Outbox
Saga
message identities
ordering/stale events

03:
FUN-04..12
REL-03..10/17
auditor
release gates

04:
A ownership
W1–W16 dependencies
```

External:

- Microsoft aggregate-root guidance;
- Saga pattern introduction.

---

## 9.3 Issue pack

### A-W01-01 Extract Trip correctness map · 1.5 h

Create table:

| Domain fact | Authority | Invariant | Stable IDs | Illegal outcome |
|---|---|---|---|---|
| Trip lifecycle | Trip | DSI-010 | tripId/version | terminal regression |
| assignment | Trip reference + Driver authority | DSI-008/030 | tripId/reservationId/round | two current Drivers |
| payment readiness | Payment + Trip guard | DSI-026 | operationKey | start without auth |
| cancellation cleanup | Trip Saga + Driver | DSL-002 | reservationId | leaked Driver |

### A-W01-02 Build full Trip transition matrix · 2 h

Rows:

```text
CREATED
MATCHING
DRIVER_RESERVED
DRIVER_ACCEPTED
READY_TO_START
IN_PROGRESS
COMPLETED
CANCELLED
MATCH_FAILED
```

Columns:

```text
StartMatching
DriverReserved
MatchingFailed
DriverAccepted
PaymentAuthorized
PaymentFailed
Start
Complete
Cancel
late/stale events
```

Every cell:

```text
ALLOW
IDEMPOTENT/CANONICAL
STALE NO-OP
REJECT
```

### A-W01-03 Draw Saga phases · 1 h

Mark:

```text
compensable
pivot
forward recovery
```

### A-W01-04 Build personal GitHub/evidence structure · 1 h

### A-W01-05 Team authority walkthrough · 1.5 h

Ask B:

- what exactly does DriverReserved prove?
- why `reservationId`?

Ask C:

- `operationKey` vs `messageId`;
- Payment timeout behavior.

### A-W01-06 Write 90-second defense note · 1 h

---

## 9.4 Theory checkpoint

A answers:

1. Why Trip does not own Driver availability?
2. Why terminal states are monotonic?
3. Why capture failure after COMPLETED is forward recovery?
4. Why compensation needs reservationId?
5. Why Saga state must be persisted?
6. Why Outbox does not guarantee exactly-one publication?

---

## 9.5 Exit

GREEN when B/C accept A's transition matrix and identity interpretation.

---

# 10. Week 2 — Reproducible Trip Service foundation
**Planned: 9 h**

## 10.1 Objective

Trip Service independently builds, migrates and integration-tests against its own PostgreSQL boundary.

---

## 10.2 Issue pack

### A-W02-01 Create TripService projects/folders · 1.25 h

### A-W02-02 Create Trip PostgreSQL configuration/migration baseline · 1.5 h

Only Trip DB credentials.

### A-W02-03 Create domain/unit test project · 0.75 h

### A-W02-04 Create integration-test host · 1 h

Use `WebApplicationFactory<Program>` where HTTP integration helps. Microsoft documents it as the standard ASP.NET Core TestServer factory for integration tests. 

### A-W02-05 Add real PostgreSQL disposable fixture · 1 h

Do not rely on in-memory DB for transactional/constraint claims.

### A-W02-06 Add health endpoints with team convention · 0.5 h

Liveness not coupled to every remote dependency.

### A-W02-07 Freeze initial Trip HTTP contracts · 1 h

```text
CreateRide
GetTrip
CancelRide
```

### A-W02-08 Freeze stable identity shape with B/C · 1 h

```text
matchingRound
reservationId
operationKey
messageId
```

### A-W02-09 CI smoke/review · 1 h

---

## 10.3 Implementation guidance

Do not add:

- MediatR just because tutorials use it;
- CQRS framework;
- Event Sourcing;
- generic Saga framework.

Use plain application handlers if they are clear.

---

## 10.4 Exit

```text
dotnet test
migration from empty DB
Trip health
Trip DB isolation
```

green from clean clone.

---

# 11. Week 3 — Trip aggregate and legal lifecycle
**Planned: 9 h**

## 11.1 Objective

Create authoritative Trip aggregate before asynchronous workflow integration.

---

## 11.2 Theory

Aggregate invariant rule:

> Entity should not be persisted in an invalid combination of state and assignment.

Microsoft's DDD guidance explicitly says aggregate roots enforce invariants across state changes. 

---

## 11.3 Issue pack

### A-W03-01 Implement `Trip` aggregate · 2 h

State transition methods only.

### A-W03-02 Implement `TripState` + errors/outcomes · 0.75 h

Avoid generic exceptions as normal domain conflicts.

### A-W03-03 Implement `FareSnapshot` value object · 1 h

### A-W03-04 Implement assignment value/reference model · 1 h

Contains:

```text
driverId
reservationId
matchingRound
```

### A-W03-05 Table-driven legal transition tests · 1.5 h

### A-W03-06 Illegal/terminal transition tests · 1 h

Primary DSI-010.

### A-W03-07 Persistence mapping + optimistic concurrency/version · 1 h

EF Core supports optimistic concurrency through concurrency tokens, causing a save to fail when the database value changed since query; however A must convert this into domain conflict semantics rather than blindly retrying. 

### A-W03-08 B review assignment semantics · 0.75 h

---

## 11.4 Required tests

```text
Trip_TerminalCompleted_RejectsCancel
Trip_Cancelled_RejectsDriverReservedAsCurrent
Trip_MatchFailed_RejectsDriverAccepted
Trip_Complete_OnlyFromInProgress
Trip_AssignmentRequiresReservationIdentity
```

---

## 11.5 Exit

Trip aggregate can be tested without RabbitMQ/Redis/Payment.

---

# 12. Week 4 — Create Ride idempotency + FareSnapshot
**Planned: 9 h**

## 12.1 Objective

Deliver a stable logical Trip creation contract.

---

## 12.2 Issue pack

### A-W04-01 Implement CreateRide handler/API · 1.5 h

### A-W04-02 Implement request idempotency record · 1.25 h

### A-W04-03 Add request fingerprint · 0.75 h

Same key + different semantic request:

```text
IDEMPOTENCY_CONFLICT
```

### A-W04-04 Enforce P0 one-active-Trip-per-Rider · 1 h

DB defense where possible.

### A-W04-05 Freeze FareSnapshot in creation transaction · 1 h

### A-W04-06 Implement FUN-04 integration test · 1.25 h

Same request/key:

```text
same tripId
one logical Trip
```

### A-W04-07 Concurrent same-key create test · 1 h

### A-W04-08 Evidence bundle · 0.5 h

### A-W04-09 Review with B/C · 0.75 h

---

## 12.3 Correct transaction shape

```text
BEGIN
  validate active-trip rule
  resolve/create idempotency record
  create Trip
  freeze FareSnapshot
COMMIT
```

No broker needed yet.

---

## 12.4 Exit

`FUN-04`, DSI-013 and DSI-014 green.

---

# 13. Week 5 — Matching-round integration from Trip side
**Planned: 9 h**

## 13.1 Objective

Trip can begin matching with stable round identity and reject stale results.

---

## 13.2 Issue pack

### A-W05-01 Implement `StartMatching()` · 1 h

```text
CREATED → MATCHING
matchingRound initialized/incremented according to contract
```

### A-W05-02 Model current matching round · 0.75 h

### A-W05-03 Implement DriverReserved application guard · 1.5 h

Accept only:

```text
same tripId
same current matchingRound
Trip currently eligible
reservationId present
```

### A-W05-04 Implement MatchingFailed guard · 1 h

No unresolved/current assignment contradiction.

### A-W05-05 Stale-round tests · 1 h

### A-W05-06 Duplicate DriverReserved tests · 0.75 h

Same logical current assignment:

```text
canonical no-op/success
```

### A-W05-07 Coordinate `RideRequestedV1` payload with B/C · 1 h

### A-W05-08 Build JSON contract fixtures · 0.75 h

### A-W05-09 Evidence/review · 1.25 h

---

## 13.3 Failure rule

Late:

```text
DriverReserved(round=n)
```

while:

```text
Trip.currentRound=n+1
```

must not become current assignment.

---

## 13.4 Exit

A can consume deterministic B fixture outputs without Trip invariant violations.

---

# 14. Week 6 — Assignment and authoritative concurrency integration
**Planned: 9 h**

## 14.1 Objective

Integrate B's Driver OCC winner into Trip without turning Trip into reservation authority.

---

## 14.2 Issue pack

### A-W06-01 Integrate current Driver reservation reference · 1.25 h

### A-W06-02 Add one-current-assignment persistence defense · 1 h

### A-W06-03 Review B `ReserveDriver` outcome contract · 1 h

Check:

```text
success
same-reservation idempotent success
conflict
UNKNOWN
```

### A-W06-04 Participate CON-01 · 1.5 h

After B's race:

Assert Trip side cannot persist conflicting current assignment.

### A-W06-05 Implement stale/duplicate assignment event behavior · 1 h

### A-W06-06 Add AUD-03 Trip checks · 1 h

### A-W06-07 Add Driver–Trip agreement fixture support · 0.75 h

### A-W06-08 First assignment evidence bundle · 0.75 h

### A-W06-09 Peer review · 0.75 h

---

## 14.3 A must understand B's linearization point

Trip receiving `DriverReservedV1` is **not** the reservation linearization point.

The authority point already happened in Driver PostgreSQL.

Trip only records a reference to that authoritative fact if it remains current/legal.

---

## 14.4 Exit

`DSI-008/009/030` have executable Trip-side defenses.

---

# 15. Week 7 — Cancellation, exact cleanup and race semantics
**Planned: 10 h**

## 15.1 Objective

Make cancellation terminal while allowing delayed cleanup to converge safely.

---

## 15.2 Issue pack

### A-W07-01 Implement pre-start `Cancel()` semantics · 1.5 h

Transition:

```text
active pre-start → CANCELLED
```

according to frozen states.

### A-W07-02 Generate exact cleanup intent model · 1 h

Payload:

```text
tripId
driverId if required
reservationId
```

No Driver-ID-only release.

### A-W07-03 Implement late DriverReserved-after-cancel handler · 1 h

Trip stays cancelled.

If reservation belongs to cancelled Trip:

```text
schedule exact release
```

### A-W07-04 Implement late PaymentAuthorized-after-cancel behavior · 0.75 h

Trip stays terminal.

Void/cleanup according to Saga state.

### A-W07-05 Participate CON-05 Cancel vs Reserve · 1.5 h

Exercise schedules:

```text
cancel first
reserve first
```

### A-W07-06 Participate CON-04 stale compensation review · 1 h

A verifies exact identity generated.

### A-W07-07 Add terminal monotonicity property/table tests · 1 h

### A-W07-08 Add cleanup-liveness scenario state assertions · 1 h

### A-W07-09 Evidence/review · 1.25 h

---

## 15.3 Key theory

Cancellation can become authoritative before Driver cleanup finishes.

Temporary state:

```text
Trip CANCELLED
Driver still RESERVED for same Trip
```

can exist during compensation.

It is legal only if:

- cleanup identity is durable;
- Trip cannot reactivate;
- Driver remains protected from other assignment until released.

This is:

```text
safe temporary inconsistency
+
liveness obligation DSL-002
```

---

## 15.4 Exit

`DSI-010/032/033` and cancellation side of `DSL-002` green.

---

# 16. Week 8 — RabbitMQ integration from Trip
**Planned: 10 h**

## 16.1 Objective

Trip begins real asynchronous integration without sacrificing state guards.

---

## 16.2 Issue pack

### A-W08-01 Produce `RideRequestedV1` via canonical abstraction · 1 h

### A-W08-02 Consume Driver/Matching outcomes · 1.5 h

### A-W08-03 Consume Payment outcome skeleton · 1 h

### A-W08-04 Integrate message dedup contract with C · 1 h

### A-W08-05 Ensure domain state checked after dedup · 0.75 h

### A-W08-06 Propagate `correlationId` / trace headers · 0.75 h

### A-W08-07 Real RabbitMQ happy integration · 1 h

### A-W08-08 Duplicate/stale delivery integration tests · 1 h

### A-W08-09 Crash-window walkthrough with B/C · 1 h

### A-W08-10 Evidence · 1 h

---

## 16.3 RabbitMQ mental model

RabbitMQ confirms and consumer ACKs solve different boundaries; RabbitMQ explicitly documents them as orthogonal. citeturn893412search0

A must therefore not write logic like:

```text
publisher confirm
→ downstream business effect happened
```

Confirm only covers broker responsibility.

---

## 16.4 Exit

One real Trip→RabbitMQ→Matching/Payment-style path works with manual ACK consumers and stable identities.

---

# 17. Week 9 — Transactional Outbox
**Planned: 11 h**

## 17.1 Objective

Eliminate the Trip DB commit / Rabbit publish dual-write gap.

---

## 17.2 Issue pack

### A-W09-01 Implement Trip Outbox entity/schema · 1 h

### A-W09-02 Implement state + Outbox same-transaction writer · 1.5 h

Examples:

```text
StartMatching + RideRequested
Cancel + CancelMatchingRound/Release
Saga next step + Payment command
```

### A-W09-03 Generate `messageId` once at intent creation · 0.75 h

### A-W09-04 Integrate shared Outbox relay with C · 1 h

A owns semantic correctness.

C owns platform mechanics.

### A-W09-05 Implement REL-04 · 1.5 h

```text
Trip/Saga + Outbox COMMIT
kill before publication
restart
publish
```

### A-W09-06 Review REL-05 duplicate publish · 1 h

Same Outbox `messageId`.

### A-W09-07 Add Outbox pending/age metrics · 0.75 h

### A-W09-08 Build dual-write crash matrix · 1 h

### A-W09-09 Peer rerun by C/B · 1 h

### A-W09-10 Evidence/report diagram · 0.5 h

---

## 17.3 Crash matrix A must know

### Before local COMMIT

```text
no business state
no Outbox
```

safe to retry.

### After local COMMIT before publish

```text
business state + pending Outbox
```

relay recovers.

### After publish before mark

```text
possible duplicate publish
```

same `messageId` + consumer dedup.

### After mark published

normal completed relay.

---

## 17.4 Exit G2-A

No required Trip workflow message is best-effort after commit.

---

# 18. Week 10 — Persisted Saga + Payment + compensation
**Planned: 13 h**

## 18.1 Objective

Implement A's deepest distributed-systems responsibility.

---

## 18.2 Saga theory

Current Microsoft Saga guidance describes:

- each service completing a local transaction;
- events/messages triggering later steps;
- compensable transactions;
- a pivot/point of no return;
- orchestration as a controller storing/interpreting workflow state. citeturn739993search0

Trip Saga uses orchestration because:

- workflow is nontrivial;
- restart state should be explicit;
- compensation ordering should be inspectable.

---

## 18.3 Issue pack

### A-W10-01 Implement `RideSaga` persistence · 1.5 h

Fields:

```text
sagaId
tripId
status
step
stepVersion
reservationId
authorizeOperationKey
captureOperationKey
voidOperationKey
lastError/review
```

### A-W10-02 Implement DriverAccepted→Authorize step · 1.25 h

Transaction:

```text
Trip update
Saga PAYMENT_AUTH_PENDING
Outbox AuthorizePayment(K)
```

### A-W10-03 Implement PaymentAuthorized→READY_TO_START · 1 h

Guard:

- correct Trip;
- current Saga step;
- matching `operationKey`.

### A-W10-04 Implement Payment terminal-failure compensation · 1.5 h

```text
Trip CANCELLED
Saga COMPENSATING
Outbox ReleaseDriver(exact R)
```

### A-W10-05 Implement StartTrip · 1 h

Requires:

```text
READY_TO_START
current assignment
authoritative auth state recorded
```

### A-W10-06 Implement CompleteTrip · 1 h

```text
IN_PROGRESS → COMPLETED
```

### A-W10-07 Emit CapturePayment after completion · 1 h

Same transaction:

```text
Trip COMPLETED
Saga CAPTURE_PENDING
Outbox Capture(K_capture)
```

### A-W10-08 Implement capture-pending/review outcome · 0.75 h

Never regress Trip.

### A-W10-09 Implement REL-09 compensation test · 1.25 h

### A-W10-10 Implement REL-10 forward recovery test · 1 h

### A-W10-11 Full happy ride system test · 1 h

### A-W10-12 Evidence + B/C review · 0.75 h

---

## 18.4 Operation-key discipline

A creates/fixes logical lineage:

```text
{tripId}:authorize:v1
{tripId}:capture:v1
{tripId}:void:v1
```

Same Saga step retry:

```text
same operationKey
```

No:

```text
timeout → generate K2
```

---

## 18.5 Exit G3 foundation

Happy ride + payment failure compensation + post-completion capture failure are all legal.

---

# 19. Week 11 — Deterministic fault proofs and restart
**Planned: 11 h**

## 19.1 Objective

Prove A's code survives exact crash windows without manual DB repair.

---

## 19.2 Issue pack

### A-W11-01 Integrate `Saga.AfterPersistBeforeDispatch` faultpoint · 1 h

### A-W11-02 Automate REL-04 Outbox producer crash · 1.5 h

### A-W11-03 Automate REL-07 Saga restart · 1.5 h

Case:

```text
Saga PAYMENT_AUTH_PENDING
kill Trip
Payment result/backlog
restart
same Saga continues
```

### A-W11-04 Automate REL-17 late terminal events · 1 h

### A-W11-05 Automate cancellation compensation outage/recovery · 1.25 h

### A-W11-06 Remove timing sleeps from A-owned tests · 1 h

Use:

- faultpoint;
- DB state barrier;
- bounded poll.

### A-W11-07 Run critical tests 5 consecutive controlled passes · 1.25 h

### A-W11-08 Peer rerun by C · 0.75 h

### A-W11-09 Curate final-state artifacts · 0.75 h

### A-W11-10 Review invariant auditor with team · 1 h

---

## 19.3 Important restart rule

On service start:

```text
do not replay workflow from human logs
```

Load:

```text
Trip
Saga
Outbox
consumer dedup
```

and let durable state decide.

---

## 19.4 Exit

Another teammate can kill/restart Trip and explain final state without A's debugger.

---

# 20. Week 12 — Observability, security and Core Release Gate
**Planned: 9 h**

## 20.1 Objective

Make Trip/Saga behavior explainable and close A's P0 defects.

---

## 20.2 Issue pack

### A-W12-01 Instrument Trip domain spans/logs · 1 h

Safe fields:

```text
tripId
tripState
matchingRound
reservationId
sagaId
sagaStep
operationKey
messageId
correlationId
outcome
```

### A-W12-02 Add Trip/Saga/Outbox metrics · 1 h

```text
trip_created_total
trip_transition_rejected_total
trip_cancelled_total
saga_active{step}
saga_compensation_total
saga_requires_review_total
outbox_pending
outbox_oldest_pending_seconds
```

### A-W12-03 Validate trace propagation across async path · 0.75 h

OpenTelemetry messaging conventions currently remain Development and require message creation context propagation for direct producer/consumer correlation. citeturn893412search1

### A-W12-04 Trip authz/invalid transition negative tests · 1 h

### A-W12-05 Run A full regression · 1.5 h

### A-W12-06 Run invariant auditor · 1 h

### A-W12-07 Review G4 checklist · 1 h

### A-W12-08 Cross-explain A mechanisms to B/C · 0.75 h

### A-W12-09 Evidence gap repair · 1 h

---

## 20.3 A's G4 checklist

```text
[ ] FUN-04/06/07/08/09/10/12 green
[ ] CON-05 green
[ ] REL-03/04/05/07/09/10/17 green where applicable
[ ] DSI-008..014 evidence
[ ] DSI-026..028 evidence
[ ] DSI-030/032/033/034 evidence
[ ] DSL-002/005/006 evidence
[ ] no direct required publish outside Outbox
[ ] no S0/S1 A-owned defect
[ ] B and C can explain Saga/Outbox path
```

---

# 21. Week 13 — Support performance without sacrificing correctness
**Planned: 8 h**

## 21.1 Objective

B owns experiments; A ensures Trip/Saga/Outbox are not black boxes under load.

---

## 21.2 Issue pack

### A-W13-01 Add Trip final-state assertions to load fixtures · 1 h

### A-W13-02 Expose Trip/Saga/Outbox metrics for E02/E04 · 1 h

### A-W13-03 Support E02 controlled offered-load runs · 1 h

Check:

```text
duplicate create count
Trip system errors
terminal regressions
```

### A-W13-04 Support E04 backlog/recovery · 1 h

Monitor:

```text
outbox pending/age
Saga pending/age
```

### A-W13-05 Run auditor after selected performance runs · 1 h

### A-W13-06 Review bottleneck interpretation · 1 h

Do not propose optimization without evidence.

### A-W13-07 Preserve run metadata/raw results · 1 h

### A-W13-08 Report-note draft · 1 h

---

## 21.3 A must not do

- disable invariant assertions because load is slower;
- change architecture mid-campaign for cosmetic throughput;
- call a configured timeout an observed SLA;
- merge expected domain conflict with system failure.

---

## 21.4 Exit

Performance results do not hide Trip/Saga correctness violations.

---

# 22. Week 14 — Backlog/HA application-level validation
**Planned: 7 h**

## 22.1 Objective

Help validate RabbitMQ recovery/HA claims from Trip Outbox/Saga perspective.

---

## 22.2 Issue pack

### A-W14-01 Review REL-15 backlog recovery Trip state · 1 h

### A-W14-02 Review REL-16 quorum node-loss Outbox behavior · 1 h

### A-W14-03 Verify no false `PUBLISHED` state without confirm · 1 h

### A-W14-04 Run auditor after recovery · 1 h

### A-W14-05 Validate Saga pending work resumes after broker restoration · 1 h

### A-W14-06 Document application-level claim boundary · 1 h

Allowed:

```text
Trip Outbox/Saga recovered under tested broker scenario
```

Not:

```text
entire system HA
```

### A-W14-07 Optional P1 review only if gates green · 1 h

Default:

```text
no optional implementation
```

---

## 22.3 Exit

A signs off final P0 reliability evidence.

---

# 23. Week 15 — Feature freeze, report and evidence
**Planned: 9 h**

## 23.1 Objective

Turn A's work into compact report-ready academic material.

---

## 23.2 Issue pack

### A-W15-01 Freeze Trip/Saga/Outbox feature surface · 0.5 h

Remove dead experimental code.

### A-W15-02 Clean-clone Trip migration/regression · 1 h

### A-W15-03 Write Trip FSM section · 1 h

Include:

- states;
- authority;
- terminal monotonicity.

### A-W15-04 Write Outbox section · 1.25 h

Include dual-write crash windows.

### A-W15-05 Write Saga section · 1.5 h

Include:

```text
local transactions
compensation
pivot
forward recovery
```

### A-W15-06 Write failure evidence REL-04/07/09/10 · 1 h

### A-W15-07 Curate diagrams · 0.75 h

### A-W15-08 Review report language against measured evidence · 1 h

### A-W15-09 Final A regression/evidence manifest · 1 h

---

## 23.3 Report wording rule

Distinguish:

```text
architecture target
observed result
limitation
future work
```

Never:

```text
“Outbox guarantees exactly once”
```

Use:

> Outbox prevents silent loss of committed outgoing intent; duplicate publication remains possible and is handled through stable message identity and idempotent consumers.

---

# 24. Week 16 — Release, live demo and oral defense
**Planned: 10 h**

## 24.1 Objective

A independently operates and explains Trip/Saga failure behavior.

---

## 24.2 Issue pack

### A-W16-01 Re-run A release evidence · 1 h

### A-W16-02 Rehearse Payment failure→compensation segment 3x · 1.5 h

Recommended live sequence:

```text
Trip DRIVER_ACCEPTED
Driver reservation exists
Payment DECLINE
Trip CANCELLED
Outbox Release exact reservation
Driver eventually AVAILABLE
Saga terminal compensated
```

### A-W16-03 Rehearse Outbox crash backup demo · 1 h

### A-W16-04 Rehearse Saga restart demo · 0.75 h

### A-W16-05 Oral defense drill · 2 h

### A-W16-06 Review B/C demo handoffs · 0.75 h

### A-W16-07 Final README/setup verification · 0.75 h

### A-W16-08 Final tag/evidence/checksum contribution verification · 0.75 h

### A-W16-09 Backup trace/screenshots/raw state · 0.5 h

### A-W16-10 Clean release smoke · 1 h

---

## 24.3 Questions A must answer from memory

1. What does Trip Service own?
2. Why doesn't Trip own Driver availability?
3. What is a Trip aggregate?
4. Why no public state setter?
5. What is terminal monotonicity?
6. Why can CANCELLED receive a late DriverReserved event without reactivating?
7. Why FareSnapshot is frozen?
8. What is `matchingRound`?
9. What is `reservationId`?
10. Why is release fenced by reservationId?
11. What is `operationKey`?
12. What is `messageId`?
13. Why aren't they interchangeable?
14. What is the DB/message dual-write problem?
15. Why does Outbox close it?
16. Why can Outbox republish duplicates?
17. What protects consumers from duplicate effect?
18. Why state + Outbox must be one transaction?
19. Why not hold a DB transaction open while calling another service?
20. What is a Saga?
21. Why is Saga not rollback?
22. What is compensation?
23. Why must compensation be idempotent?
24. What is the pivot?
25. Why doesn't capture failure un-complete the ride?
26. What survives a Trip Service restart?
27. What does Saga step version protect?
28. Why can a temporary Driver/Trip inconsistency during compensation be legal?
29. What liveness property requires eventual cleanup?
30. What exactly does REL-04 prove?
31. What exactly does REL-07 prove?
32. What exactly does REL-09 prove?
33. Which DB final state proves cancellation cleanup?
34. Why isn't a Jaeger trace enough to prove correctness?
35. Which project claims remain limitations?

---

# 25. A ↔ B interface checklist

Before approving Driver/Matching boundary:

```text
[ ] Driver Service is reservation authority
[ ] Redis remains derived
[ ] reservationId stable
[ ] reserve timeout can be UNKNOWN
[ ] Matching persists attempt before dangerous remote call
[ ] UNKNOWN blocks next candidate
[ ] DriverReserved event only from Driver authority
[ ] MatchingFailed only after no unresolved attempt
[ ] stale matchingRound rejected
[ ] exact Release requires reservationId
[ ] stale release cannot free newer reservation
[ ] CON-01/04 final-state proof exists
```

A's Trip handlers must remain safe if:

- Driver event duplicates;
- event is late;
- event is from old round;
- Trip is already terminal.

---

# 26. A ↔ C Payment checklist

```text
[ ] one logical Saga Payment step → one operationKey
[ ] same key retry returns same logical operation
[ ] conflicting same key rejected
[ ] Payment timeout may be UNKNOWN
[ ] duplicate Payment event is harmless
[ ] Payment result authority remains Payment
[ ] READY_TO_START requires recorded authorization success
[ ] Capture only after completion pivot
[ ] capture failure does not regress Trip
[ ] retry ownership explicit
```

---

# 27. A ↔ C messaging/Outbox checklist

```text
[ ] Outbox intent is same transaction as required state
[ ] messageId generated once
[ ] relay publishes same messageId on retry
[ ] publisher confirm before PUBLISHED mark
[ ] consumer manual ACK after commit
[ ] dedup durable
[ ] retry finite
[ ] DLQ visible
[ ] trace context diagnostic only
```

RabbitMQ's reliability guide says consumers should acknowledge only after taking responsibility for processing, such as recording or forwarding the message; that ownership-transfer model matches the project consumer contract. citeturn893412search8

---

# 28. A's Definition of Done

## Trip domain feature

```text
[ ] authority clear
[ ] aggregate behavior, no public setter bypass
[ ] legal state tested
[ ] illegal state tested
[ ] duplicate/stale behavior tested
[ ] migration/persistence mapping
[ ] reviewer
```

## Distributed Trip feature

Additionally:

```text
[ ] stable business identity
[ ] retry owner
[ ] state + Outbox atomic if required
[ ] crash-before/after commit understood
[ ] restart path
[ ] final authoritative-state assertion
[ ] current-commit evidence
```

## Saga step

```text
[ ] persisted step
[ ] stable operation/compensation identity
[ ] legal predecessor/current state
[ ] resulting Outbox intent atomic
[ ] duplicate result safe
[ ] stale result safe
[ ] process restart safe
[ ] compensation/forward recovery defined
```

---

# 29. Common mistakes A must prevent

## Mistake 1 — Saga as rollback

Fix:

```text
local transactions + new compensating actions
```

## Mistake 2 — publish directly after `SaveChanges()`

Fix:

```text
Trip/Saga + Outbox in same transaction
```

## Mistake 3 — same message/event sets Trip state unconditionally

Fix:

```text
load current Trip
check state + identity
```

## Mistake 4 — `ReleaseDriver(driverId)`

Fix:

```text
ReleaseDriver(tripId,reservationId)
```

## Mistake 5 — Payment retry creates a new operation key

Fix:

```text
same Saga step → same operationKey
```

## Mistake 6 — capture failure cancels Trip

Fix:

```text
COMPLETED is physical terminal
Payment forward-recovery state
```

## Mistake 7 — logs used to resume workflow

Fix:

```text
Saga/Outbox are recovery state
```

## Mistake 8 — giant `TripServiceManager`

Fix:

- domain aggregate;
- application handler;
- persistence;
- messaging separated by responsibility.

## Mistake 9 — generic distributed abstraction too early

Fix:

Implement concrete Trip behavior first.

## Mistake 10 — increasing sleeps in failing fault tests

Fix:

Add barriers/faultpoints.

---

# 30. Blocker escalation

Escalate immediately if:

- Trip receives assignment without `reservationId`;
- B wants Trip to infer Driver success from Redis;
- C Payment retry changes logical operation key;
- required outgoing command cannot be persisted atomically with Saga state;
- terminal Trip must “reopen” to handle late event;
- compensation has no exact reservation identity;
- Outbox marks PUBLISHED before confirm;
- test cannot identify authoritative final state.

Same day if:

- message ownership disagrees;
- matchingRound meaning differs;
- current Saga step unclear;
- Start/Complete ordering differs across services.

Blocker template:

```text
Observed:
Expected Doc 01/02 contract:
Trip current state:
Saga current step:
Stable identities:
Remote dependency:
Failure window:
Affected DSI/DSL:
Smallest reproduction:
Decision required:
Owner:
Deadline:
```

---

# 31. Personal risk register

| Risk | Impact | Control |
|---|---|---|
| Trip FSM becomes permissive | critical | table-driven legal/illegal matrix |
| stale event reactivates terminal Trip | critical | terminal guard + REL-17 |
| create idempotency races | high | DB uniqueness + concurrent test |
| assignment identity drift | critical | matchingRound + reservationId |
| Outbox only “best effort” | critical | REL-04/05 |
| Saga in memory | critical | persisted state + REL-07 |
| compensation not fenced | critical | exact reservationId |
| Payment operation lineage changes | high | stable operationKey |
| report claims exactly-once | medium | claim review W15 |
| A is only Saga expert | high | C backup/review/rerun |

---

# 32. Personal report/defense pack

By W16:

1. Trip state-machine diagram.
2. DSI-008/009/010/011/012/013/014 table.
3. cross-service DSI-026/028/030/032/033/034 mapping.
4. CreateRide idempotency proof.
5. FareSnapshot rationale.
6. current matching-round stale-event proof.
7. Outbox dual-write timeline.
8. REL-04 evidence.
9. REL-05 duplicate-publication evidence.
10. Saga state diagram.
11. compensation matrix.
12. pivot/forward-recovery diagram.
13. REL-07 restart evidence.
14. REL-09 Payment-failure compensation evidence.
15. REL-10 capture-pending evidence.
16. one happy trace.
17. one compensation trace.
18. auditor final results.
19. known limitations.
20. release-commit evidence manifest.

---

# 33. A's strongest 90-second explanation

A should be able to deliver conceptually:

> Trip Service owns the ride lifecycle, but the workflow crosses Driver, Matching and Payment services, so there is no global ACID transaction. Every critical local Trip/Saga transition and its required outgoing integration intent are persisted atomically with a Transactional Outbox. The Outbox relay publishes with a stable message identity and publisher confirms; duplicate publication is possible, so consumers deduplicate and also validate current domain state. Saga state is persisted, so a Trip Service restart resumes from durable workflow state rather than memory. Before the physical ride begins, failure can be compensated through exact fenced commands such as `ReleaseDriver(tripId,reservationId)`. After the pivot, especially once the ride is completed, Payment failure is handled by forward recovery and never moves the Trip backward.

A should then be able to map each sentence to:

```text
code
DSI/DSL
fault window
test ID
final DB assertion
```

---

# 34. Authoritative implementation-learning references

These are external implementation/theory references only. Project Docs 00–04 remain canonical.

## DDD / Trip aggregate

### Microsoft — Design a microservice domain model

https://learn.microsoft.com/en-us/dotnet/architecture/microservices/microservice-ddd-cqrs-patterns/microservice-domain-model

Use for:

- aggregate boundary;
- aggregate root as consistency guardian;
- behavior through root methods.

### Microsoft — Domain-model validation

https://learn.microsoft.com/en-us/dotnet/architecture/microservices/microservice-ddd-cqrs-patterns/domain-model-layer-validations

Use for:

- invariants enforced during state changes.

## Saga

### Azure Architecture Center — Saga

https://learn.microsoft.com/en-us/azure/architecture/patterns/saga

Use for:

- local transaction sequence;
- compensation;
- orchestration;
- pivot/point of no return;
- retryable forward steps.

## ASP.NET integration

### ASP.NET Core integration tests

https://learn.microsoft.com/en-us/aspnet/core/test/integration-tests?view=aspnetcore-10.0

Use for:

- `WebApplicationFactory<TEntryPoint>`;
- real application host in integration tests.

## EF Core concurrency

### EF Core — Handling Concurrency Conflicts

https://learn.microsoft.com/en-us/ef/core/saving/concurrency

Use for:

- optimistic concurrency token behavior;
- conflict detection;
- never turning conflict into blind overwrite.

## RabbitMQ

### Consumer ACKs and publisher confirms

https://www.rabbitmq.com/docs/confirms

Use for:

- publisher/broker vs consumer/broker responsibility;
- redelivery reasoning.

### Reliability guide

https://www.rabbitmq.com/docs/reliability

Use for:

- acknowledgement ownership;
- retransmission/redelivery.

## OpenTelemetry

### Messaging spans/context

https://opentelemetry.io/docs/specs/semconv/messaging/messaging-spans/

Use for:

- async producer/consumer correlation;
- diagnostics only.

---

# 35. Weekly operating checklist

Every Monday:

```text
[ ] previous gate green?
[ ] max 2 In Progress issues?
[ ] Trip authoritative state?
[ ] Saga current step?
[ ] stable reservation/payment/message identities?
[ ] any terminal-state risk?
[ ] any remote side-effect ambiguity?
[ ] exact proof/evidence planned?
```

During implementation:

```text
[ ] domain method protects invariant
[ ] no direct cross-service DB
[ ] no required best-effort publish
[ ] same operation identity on retry
[ ] stale event guard
[ ] exact compensation identity
[ ] no logs as recovery
```

Before issue close:

```text
[ ] happy path
[ ] illegal path
[ ] duplicate path
[ ] stale path
[ ] crash/restart path if distributed
[ ] final DB state
[ ] reviewer rerun
[ ] evidence from current commit
[ ] actual hours recorded
```

---

# 36. Final north star

> **Whenever A is unsure whether a Trip/Saga implementation is complete, ask: if the process dies immediately after this local commit, if the outgoing message is published twice, if the result arrives late after cancellation, and if the compensation itself is delayed, does durable state still tell the system exactly what the ride is, what work remains, which reservation/payment operation is current, and which legal state it must eventually reach?**

If not, the Trip workflow is not complete.
