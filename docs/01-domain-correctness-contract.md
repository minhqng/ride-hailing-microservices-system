# DS03 DISTRIBUTED RIDE-HAILING SYSTEM
## Domain Correctness Contract · State Machines · Invariants · Race Semantics

> **Document:** `docs/01-domain-correctness-contract.md`  
> **Course:** Distributed Systems  
> **Topic:** DS03 — Ride-hailing Microservices System simulating Grab/Uber  
> **Type:** Domain Model + Correctness Contract + Formal Invariant Specification  
> **Status:** **PROPOSED FREEZE v2.0 — REVIEW CANDIDATE**  
> **Baseline date:** **2026-09-26**  
> **Parent:** `00-project-scope-architecture.md`  
> **Children:** `02-distributed-workflow-engineering.md`, `03-verification-experiments-demo.md`, `04-team-execution-plan.md`, `05A/05B/05C`  
> **Normative role:** source of truth for domain vocabulary, aggregate ownership, entity/value-object semantics, state machines, legal commands, stable identities, local preconditions/postconditions, safety invariants, liveness obligations, race outcomes and consistency contracts.  
> **Not source of truth for:** broker topology, queue/routing names, retry delays, ACK/confirm mechanics, Outbox relay implementation, exact test IDs, deployment profiles, weekly scheduling. Those belong to Docs 02–05.  
> **Change policy:** any change to a domain state, legal transition, authority boundary, invariant, stable business identity or race outcome requires updating this document before or together with implementation.

> **Correctness thesis.**  
> **The system is correct when every business action can be interpreted as a legal transition at exactly one authoritative domain boundary, concurrent/duplicate/stale inputs cannot violate safety invariants, and unresolved distributed work eventually becomes either a legal committed outcome, a compensated state, or an explicit visible unresolved/review state.**

---

# 0. Why this document exists

A distributed architecture can be technically impressive and still be wrong.

Examples:

```text
RabbitMQ works
but the same Driver is reserved twice.

Outbox works
but a late release frees a newer reservation.

Tracing works
but Trip moves from COMPLETED back to IN_PROGRESS.

Redis GEO is fast
but stale state decides assignment.

Payment is retried
but two logical charges are created.

Matching restarts
but silently skips an unresolved reservation attempt.
```

The purpose of this document is therefore not to describe infrastructure.

It defines:

```text
what must always be true
what transitions are legal
which service has authority to decide them
which identities distinguish old work from current work
which concurrent outcomes are acceptable
which unresolved states are allowed temporarily
which states must eventually converge
```

Everything in Doc 02 exists to preserve the contracts defined here.

---

# 1. Correctness vocabulary

## 1.1 Safety

A **safety property** states that something bad never occurs.

Informally:

> Nothing illegal ever becomes committed authoritative state.

Examples:

```text
one Driver never has two current reservations
one Trip never has two current assigned Drivers
a terminal Trip never becomes active again
a stale ReleaseDriver never releases a newer reservation
one Payment operationKey never creates two logical effects
```

Safety must hold:

- during race;
- during duplicate delivery;
- during stale message arrival;
- during retries;
- after process restart.

If a safety property is violated even briefly in committed authoritative state, the architecture is incorrect.

## 1.2 Liveness

A **liveness property** states that, under explicit recovery assumptions, useful progress eventually happens.

Examples:

```text
UNKNOWN reservation eventually becomes conclusive
cancelled Trip eventually cleans exact Driver reservation
pending Saga eventually advances, compensates, or becomes visible review state
pending Outbox eventually publishes after dependency recovery
```

Liveness always has assumptions.

For example:

```text
if PostgreSQL remains permanently unavailable
```

the system cannot promise workflow completion.

Therefore liveness is written as:

```text
IF required dependencies eventually recover
AND retry/recovery workers continue executing
THEN pending durable work eventually converges or becomes visible terminal/review state.
```

## 1.3 Invariant

An invariant is a condition that must hold in every valid committed state reachable through legal transitions.

Formally, if:

```text
S0
```

is valid, and every legal transition:

```text
T(S) → S'
```

preserves property `I`, then:

```text
I(S0) ∧ ∀ legal T: I(S) ⇒ I(T(S))
```

implies `I` is preserved inductively.

This project uses that reasoning style for core domain safety.

## 1.4 Precondition

A precondition is what must be true before a command is allowed to commit.

Example:

```text
ReserveDriver(D, T, R)
```

requires:

```text
Driver.status = AVAILABLE
```

and, where OCC/version is used:

```text
Driver.version = expectedVersion
```

## 1.5 Postcondition

A postcondition is what must be true after successful commit.

Example:

```text
ReserveDriver(D,T,R)
```

successful postcondition:

```text
Driver.status = RESERVED
Driver.reservedTripId = T
Driver.reservationId = R
Driver.version = oldVersion + 1
```

## 1.6 Authority

An authority is the service/aggregate whose committed state decides a business fact.

Examples:

```text
Driver Service
→ whether Driver is AVAILABLE/RESERVED/ON_TRIP

Trip Service
→ whether ride is MATCHING/READY_TO_START/IN_PROGRESS/COMPLETED

Payment Service
→ whether one logical authorization/capture/void operation succeeded

Matching Service
→ which matching round/attempt is current and how it resolved
```

Authority is not:

- the caller;
- the UI;
- Redis;
- a notification;
- a trace.

## 1.7 Derived state

Derived state is a projection/copy computed from authority.

Example:

```text
Redis:
driver D appears AVAILABLE near location X
```

may lag behind:

```text
Driver DB:
D is now RESERVED
```

A derived state may become stale without violating safety **if final correctness is checked at authority**.

## 1.8 Stable logical identity

A stable logical identity lets the system recognize:

> This is the same business work being retried/replayed.

Examples:

```text
tripId
matchingRound
reservationId
operationKey
messageId
```

A random new ID during retry can accidentally convert one logical operation into two.

---

# 2. Domain modeling principles

## 2.1 Rich domain model where correctness is complex

Microsoft's DDD guidance describes aggregate roots as consistency guardians responsible for invariants across state changes. The project follows this principle for:

- Trip;
- Driver;
- Payment;
- MatchingRound.

Business transitions should be methods/commands such as:

```text
ReserveDriver(...)
ReleaseDriver(...)
RecordDriverAccepted(...)
Authorize(...)
CompleteTrip(...)
```

rather than public state setters.

Bad:

```csharp
driver.Status = DriverStatus.Reserved;
```

Preferred conceptual model:

```csharp
driver.Reserve(tripId, reservationId, expectedVersion);
```

The method validates invariants.

## 2.2 Aggregate boundary follows local consistency boundary

An aggregate groups state that must be atomically consistent inside one service transaction.

The project does **not** define:

```text
Trip + Driver + Payment
```

as one aggregate because they belong to independent services/databases.

Therefore:

```text
Trip Aggregate
Driver Aggregate
Payment Aggregate
MatchingRound Aggregate
```

are separate local consistency boundaries.

Cross-aggregate/service consistency is achieved by workflow protocol, not object navigation.

## 2.3 Domain layer is infrastructure-independent

Domain semantics must not require:

- EF Core API;
- RabbitMQ types;
- Redis client types;
- HTTP request types;
- OpenTelemetry types.

The domain model answers:

```text
is this transition legal?
what state results?
what domain outcome occurred?
```

Infrastructure answers:

```text
how is it persisted?
how is it transported?
how is it traced?
```

---

# 3. Canonical domain vocabulary

## 3.1 Actor vocabulary

### Rider

Authenticated user requesting a Trip.

### Driver

Authenticated user/operational actor associated with a Driver aggregate and optionally a Vehicle.

### System/Internal Workflow

Trusted service-to-service action generated by durable workflow logic.

Not every internal message is automatically authorized; receiver still validates state/identity.

## 3.2 Main entities/aggregates

```text
User
Driver
Vehicle
Trip
FareSnapshot
MatchingRound
MatchingAttempt
PaymentOperation
NotificationDelivery
```

## 3.3 Stable identities

| Identity | Scope | Creator | Retry rule | Primary purpose |
|---|---|---|---|---|
| `userId` | account | Identity | stable | actor identity |
| `driverId` | Driver aggregate | Driver domain | stable | Driver authority |
| `vehicleId` | Vehicle | Driver domain | stable | vehicle association |
| `tripId` | logical ride | Trip | stable for ride lifetime | ride identity |
| `matchingRound` | one matching generation | Trip/Matching contract | increments only for new logical round | stale-round fencing |
| `reservationId` | one Driver reservation logical effect | Matching before reserve | reused for same attempt | reserve idempotency/fencing/reconciliation |
| `operationKey` | one Payment operation | Saga/Payment contract | reused for same logical Payment effect | Payment idempotency |
| `messageId` | one integration message | producer/outbox | stable on republish | transport/application dedup |
| `correlationId` | business flow diagnostics | edge/workflow | propagated | observability correlation |
| `traceId` | one observed execution trace | tracing SDK | not business retry key | observability |

---

# 4. Driver aggregate

## 4.1 Driver authority

Driver Service is authoritative for:

```text
operational status
current reservation
current Trip binding
vehicle eligibility baseline
accepted operational location state
```

It is the **only** authority that may successfully decide:

```text
ReserveDriver
ReleaseDriver
GoOffline
MarkTripStarted
MarkTripCompleted
```

Matching may request reservation.

Trip may request release/start/complete.

Neither writes Driver state directly.

## 4.2 Driver states

Canonical P0 states:

```text
OFFLINE
AVAILABLE
RESERVED
ON_TRIP
```

Conceptual state graph:

```text
          GoOnline
OFFLINE ------------> AVAILABLE
   ^                    |
   |                    | Reserve
   | GoOffline          v
   +---------------- RESERVED
                          |
                          | StartTrip
                          v
                       ON_TRIP
                          |
                          | Complete
                          v
                       AVAILABLE
```

Pre-start reservation can also return:

```text
RESERVED → AVAILABLE
```

through:

- rejection;
- cancellation compensation;
- payment failure compensation;
- matching cleanup;

but only via an exact current reservation guard.

## 4.3 Driver state meanings

### OFFLINE

Driver is not eligible for Matching.

No current active reservation.

### AVAILABLE

Driver may be discovered by Matching and may be authoritatively reserved.

### RESERVED

Driver is held for exactly one current `tripId + reservationId`.

Not available to another Trip.

### ON_TRIP

Ride has started for current Trip.

Driver cannot become candidate for a new Trip.

## 4.4 Driver legal transition table

| Current | Command | Condition | Result |
|---|---|---|---|
| OFFLINE | GoOnline | operational prerequisites valid | AVAILABLE |
| AVAILABLE | GoOnline retry | already current online operation semantics | idempotent/current success or conflict per command identity |
| AVAILABLE | GoOffline | no current reservation | OFFLINE |
| AVAILABLE | ReserveDriver | legal expected state/version | RESERVED |
| RESERVED | ReserveDriver same reservation | same `tripId + reservationId` | idempotent current success |
| RESERVED | ReserveDriver different reservation | current reservation differs | conflict |
| RESERVED | GoOffline ordinary | reservation active | reject/conflict |
| RESERVED | ReleaseDriver exact token | same current `tripId + reservationId` | AVAILABLE |
| RESERVED | ReleaseDriver stale token | different reservation | no-op/conflict; state unchanged |
| RESERVED | MarkTripStarted exact assignment | current identity matches | ON_TRIP |
| RESERVED | MarkTripStarted stale assignment | mismatch | reject |
| ON_TRIP | MarkTripCompleted current Trip | current Trip matches | AVAILABLE |
| ON_TRIP | ReleaseDriver | physical ride in progress | reject |
| OFFLINE | ReserveDriver | not AVAILABLE | conflict |
| ON_TRIP | ReserveDriver | not AVAILABLE | conflict |

Exact API status/error codes belong to Doc 02.

## 4.5 Driver local invariants

### DSI-001 — Exclusive Driver Reservation

For every Driver `d`:

```text
ActiveReservationCount(d) ≤ 1
```

Equivalent authoritative representation:

```text
Driver.status ∈ {RESERVED, ON_TRIP}
⇒ exactly one current trip binding
```

No two Trips can both own the same Driver concurrently.

### DSI-002 — Driver State/Reservation Coherence

```text
Driver.status = RESERVED
⇒
reservedTripId != null
∧ reservationId != null
```

```text
Driver.status = AVAILABLE
⇒
reservedTripId = null
∧ reservationId = null
```

```text
Driver.status = ON_TRIP
⇒
activeTripId != null
```

The project must never commit contradictory combinations such as:

```text
AVAILABLE + reservationId=R
```

### DSI-003 — Legal Driver Transition

Every committed Driver state transition must belong to the frozen state machine.

Stale concurrent writes cannot overwrite a newer legal state.

### DSI-004 — Guarded Reservation Release

A release for reservation `r_old` cannot release a Driver currently owned by `r_new`.

Formally:

```text
CurrentReservation(d) = r_new
∧ r_old ≠ r_new
⇒
Release(d, r_old) does not change CurrentReservation(d)
```

This is fencing.

### DSI-005 — Reserve Idempotency

For identical current logical reservation:

```text
Reserve(d, t, r)
```

replayed with the same `r` cannot create a second reservation.

It returns:

- current canonical success;
- or equivalent idempotent result.

## 4.6 Driver concurrency semantics

### Race A — Reserve vs Reserve

Initial:

```text
D = AVAILABLE
```

Two calls:

```text
Reserve(D,T1,R1)
Reserve(D,T2,R2)
```

Acceptable final outcomes:

```text
D RESERVED by T1/R1
or
D RESERVED by T2/R2
```

Forbidden:

```text
both T1 and T2 believe authoritative success
```

There is one linearization winner.

### Race B — GoOffline vs Reserve

Initial:

```text
D = AVAILABLE
```

Acceptable:

```text
GoOffline wins → D OFFLINE, reserve conflicts
```

or:

```text
Reserve wins → D RESERVED, GoOffline conflicts
```

Forbidden:

```text
reserve succeeds, then stale GoOffline blindly overwrites RESERVED
```

### Race C — Release old vs new reservation

Sequence:

```text
RA current
→ RA released
→ RB becomes current
→ delayed Release(RA)
```

Expected:

```text
RB survives
```

---

# 5. Location domain contract

## 5.1 Location is not assignment authority

A location sample answers:

```text
Where did Driver/device report being at time X?
```

It does not answer:

```text
Is Driver still AVAILABLE?
Is Driver assigned?
```

Those require Driver authority state.

## 5.2 Location sample conceptual fields

```text
messageId
driverId from authenticated context
deviceSessionId
sequenceNo
capturedAt
latitude
longitude
accuracy? optional
```

## 5.3 Location validity

Basic coordinate invariant:

```text
-90 ≤ latitude ≤ 90
-180 ≤ longitude ≤ 180
```

Values must be finite.

Freshness/accuracy policy is configuration/domain policy, not hard-coded universal truth.

## 5.4 DSI-006 — Monotonic Accepted Location per Session

For one Driver/device session:

```text
accepted sequenceNo = n
then sequenceNo < n
cannot become newest accepted location
```

If timestamp is used:

```text
older capturedAt
```

cannot overwrite a newer accepted sample under the same comparison policy.

## 5.5 DSI-007 — Projection Cannot Regress Authoritative Driver Version

If Redis projection has applied Driver aggregate version:

```text
v = 20
```

then delayed state event:

```text
v = 19
```

cannot change the projected operational state.

This is a projection correctness property.

---

# 6. Trip aggregate

## 6.1 Trip authority

Trip Service is authoritative for:

- ride request identity;
- pickup/dropoff;
- FareSnapshot;
- ride lifecycle state;
- current assignment reference;
- cancellation;
- Saga/workflow status.

Trip does not own:

- Driver reservation truth;
- Payment operation truth;
- Redis candidate state.

## 6.2 Trip P0 states

Canonical baseline:

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

A workflow-internal state such as:

```text
PAYMENT_PENDING
COMPENSATING
REQUIRES_REVIEW
```

may belong to Saga state rather than Trip business state depending on final implementation.

This distinction should remain explicit.

## 6.3 Trip state graph

Conceptual:

```text
CREATED
   |
   | StartMatching
   v
MATCHING
   |
   | DriverReserved
   v
DRIVER_RESERVED
   |
   | DriverAccepts
   v
DRIVER_ACCEPTED
   |
   | PaymentAuthorized
   v
READY_TO_START
   |
   | StartTrip
   v
IN_PROGRESS
   |
   | CompleteTrip
   v
COMPLETED
```

Failure/alternate branches:

```text
MATCHING
  └→ MATCH_FAILED

pre-start states
  └→ CANCELLED

DRIVER_RESERVED / DRIVER_ACCEPTED
  └→ compensation/release → CANCELLED
```

## 6.4 Terminal Trip states

P0 terminal states:

```text
COMPLETED
CANCELLED
MATCH_FAILED
```

A terminal Trip does not regress into an active state.

## 6.5 Trip transition table

| Current | Input/Command | Condition | Result |
|---|---|---|---|
| CREATED | StartMatching | valid Trip | MATCHING |
| MATCHING | DriverReserved | same current round, legal assignment | DRIVER_RESERVED |
| MATCHING | MatchingFailed | same current round, no unresolved reservation | MATCH_FAILED |
| MATCHING | Cancel | not terminal | CANCELLED + cleanup protocol if needed |
| DRIVER_RESERVED | DriverAccepted | current Driver/reservation | DRIVER_ACCEPTED |
| DRIVER_RESERVED | DriverRejected | current Driver/reservation | matching/cleanup workflow according to policy |
| DRIVER_RESERVED | Cancel | pre-start | CANCELLED + exact release |
| DRIVER_ACCEPTED | PaymentAuthorized | current Payment op | READY_TO_START |
| DRIVER_ACCEPTED | PaymentFailed terminal | current Payment op | CANCELLED + exact release/void |
| DRIVER_ACCEPTED | Cancel | pre-start policy allows | CANCELLED + compensation |
| READY_TO_START | StartTrip | current Driver + authorization satisfied | IN_PROGRESS |
| READY_TO_START | Cancel | only if frozen product policy allows pre-start cancellation | CANCELLED + cleanup |
| IN_PROGRESS | CompleteTrip | current Trip/Driver | COMPLETED |
| IN_PROGRESS | Cancel | not ordinary P0 cancel | reject |
| COMPLETED | any active transition | — | reject/no-op |
| CANCELLED | late assignment/payment events | stale/late | remain CANCELLED; may trigger cleanup evidence |
| MATCH_FAILED | late matching event | stale/late | remain terminal |

## 6.6 Trip invariants

### DSI-008 — Single Current Trip Assignment

For one Trip:

```text
CurrentAssignmentCount(trip) ≤ 1
```

The Trip cannot simultaneously identify two Drivers/reservations as current.

### DSI-009 — Assignment Identity Coherence

If Trip has current assignment:

```text
assignedDriverId != null
reservationId != null
matchingRound = current round
```

A late result from an older round cannot overwrite current assignment.

### DSI-010 — Terminal Monotonicity

If Trip reaches:

```text
COMPLETED
CANCELLED
MATCH_FAILED
```

then it never transitions to an active non-terminal state.

Formally:

```text
Terminal(S_t)
⇒
∀ t' > t : TripState(t') ∈ TerminalStates
```

Business side effects may still be pending after terminal business state, but the Trip state does not regress.

### DSI-011 — Start Requires Current Assignment

Trip may enter:

```text
IN_PROGRESS
```

only if:

```text
current Driver assignment exists
AND current assignment identity matches request
AND required Payment authorization state is satisfied
```

No stale Driver may start a Trip.

### DSI-012 — Completion Requires In-Progress

```text
CompleteTrip
```

is legal only from:

```text
IN_PROGRESS
```

and for the current Trip/Driver relationship.

### DSI-013 — One Active Trip per Rider in P0

P0 may enforce:

```text
ActiveTripCount(riderId) ≤ 1
```

for states considered active.

This reduces ambiguous recovery and duplicate booking.

The exact active-state set is frozen in implementation/domain policy.

---

# 7. FareSnapshot

## 7.1 Why snapshot instead of mutable price reference

Fare is calculated before/around ride creation.

Once frozen for the Trip, the authoritative ride should not silently change because:

- pricing config changes;
- retry re-runs calculation;
- stale event arrives.

Therefore Trip stores a snapshot.

## 7.2 FareSnapshot conceptual fields

```text
currency
amount
pricingPolicyVersion
baseFare?
distanceComponent?
timeComponent?
createdAt
```

P0 can use a simplified deterministic formula.

## 7.3 DSI-014 — Fare Snapshot Stability

Once FareSnapshot is frozen:

```text
same tripId
⇒
same authoritative FareSnapshot
```

unless an explicit future domain operation to reprice is introduced.

No retry/stale message implicitly recalculates and mutates the fare.

---

# 8. MatchingRound aggregate

## 8.1 Matching authority

Matching owns:

- matching round lifecycle;
- stage;
- candidate snapshot/ordering;
- attempt states;
- result resolution.

Matching does **not** own Driver reservation state.

## 8.2 MatchingRound states

Baseline:

```text
PROCESSING
RESERVED
NO_DRIVER
CANCELLED
FAILED
```

`RESERVED` means:

> this round has resolved to one authoritative Driver reservation result.

It does not mean Matching itself created Driver state.

## 8.3 MatchingAttempt states

Baseline:

```text
READY
IN_FLIGHT
UNKNOWN
REJECTED
RESERVED
```

Possible extended terminal:

```text
CANCELLED/SKIPPED
```

if useful.

## 8.4 Matching attempt lifecycle

```text
READY
  |
  | persist attempt + send Reserve
  v
IN_FLIGHT
  | \
  |  \ timeout/connection uncertainty
  |   v
  | UNKNOWN
  |   |
  |   | reconcile same reservationId
  |   |
  |   +→ RESERVED
  |   +→ REJECTED
  |
  +→ RESERVED
  +→ REJECTED
```

## 8.5 DSI-015 — One Terminal Matching Outcome per Round

For one:

```text
(tripId, matchingRound)
```

the round cannot resolve to contradictory terminal outcomes.

Forbidden:

```text
RESERVED + NO_DRIVER
```

for the same round.

## 8.6 DSI-016 — Current-Round Guard

Trip/Matching integration must reject stale round results.

If:

```text
Trip.currentMatchingRound = n+1
```

then result:

```text
round = n
```

cannot become the current assignment or terminal result.

## 8.7 DSI-017 — Unresolved Attempt Blocks Next Candidate

If current attempt is:

```text
IN_FLIGHT
or
UNKNOWN
```

Matching cannot safely move to another Driver candidate for the same serial baseline round/stage until the current attempt becomes conclusive.

Formally:

```text
∃ attempt a: state(a) ∈ {IN_FLIGHT, UNKNOWN}
⇒
NoNewReserveAttempt(round)
```

This property prevents double reservation after lost response.

## 8.8 DSI-018 — Candidate Attempt Identity Stability

Retry/reconciliation of the same logical reservation attempt must reuse:

```text
reservationId
```

A new `reservationId` means a new logical reservation attempt.

## 8.9 DSI-019 — Deterministic Candidate Ordering

For fixed:

```text
candidate snapshot
ranking config/version
eligibility policy
```

the ordered candidate list must be deterministic.

Formally:

```text
Rank(snapshot, config) = same ordered list
```

across replays.

This is not required for absolute business correctness but is required for reproducibility and defensible experiments.

## 8.10 DSI-020 — Eligibility Before Ranking

A candidate failing eligibility cannot become reservable merely because it has a high score.

```text
Eligible(d) = false
⇒
d ∉ AttemptableCandidates
```

---

# 9. Progressive Matching stages

## 9.1 Purpose

Progressive search expands candidate space in bounded stages:

```text
S0 → S1 → S2
```

The exact radius/budget values are configuration, not correctness constants.

## 9.2 Stage semantics

A stage may advance only when:

```text
all attempts considered by the stage are conclusive
AND no Driver reservation succeeded
```

## 9.3 DSI-021 — Stage Monotonicity

For one matching round:

```text
S0 → S1 → S2
```

No ordinary transition goes backward.

Restart reloads current stage rather than reinterpreting the round as a fresh S0.

## 9.4 DSI-022 — No Stage Advance on UNKNOWN

```text
∃ UNKNOWN attempt in stage S
⇒
stage remains S
```

until reconciliation makes the attempt conclusive.

## 9.5 DSI-023 — No Conclusive Candidate Retry with Fresh Identity in Same Round

If Driver D was conclusively rejected for one attempt under unchanged relevant state in the same round, Matching should not create endless fresh attempts for D.

This is primarily a termination/liveness guard.

---

# 10. Payment aggregate

## 10.1 Payment authority

Payment Service owns:

- logical Payment operations;
- operation result;
- simulated provider outcome;
- idempotency by `operationKey`.

Trip does not decide:

```text
PaymentAuthorized
```

by itself.

It consumes authoritative Payment outcome.

## 10.2 Payment operation types

P0:

```text
AUTHORIZE
CAPTURE
VOID
```

## 10.3 Payment states

One possible normalized operation model:

```text
PENDING
SUCCEEDED
DECLINED
UNKNOWN
REQUIRES_REVIEW
```

Aggregate-level state may expose:

```text
NOT_STARTED
AUTHORIZED
VOIDED
CAPTURED
CAPTURE_PENDING
REQUIRES_REVIEW
```

Exact persistence representation may differ as long as invariants remain.

## 10.4 DSI-024 — Payment Operation Idempotency

For one `operationKey`:

```text
LogicalEffectCount(operationKey) ≤ 1
```

Repeated delivery/request with same key cannot create another logical authorization/capture/void.

## 10.5 DSI-025 — Payment Key Semantic Stability

Same `operationKey` cannot silently refer to a different logical request.

If request fingerprint/meaning differs:

```text
same operationKey
+ conflicting payload
⇒ reject conflict
```

not overwrite existing operation.

## 10.6 DSI-026 — Authorization Before Start

If P0 policy requires authorization:

```text
Trip = READY_TO_START or IN_PROGRESS
⇒
current required authorization has authoritative success
```

## 10.7 DSI-027 — Capture After Completion Ordering

A capture command must not be emitted/treated as valid before the Trip reaches the required completion/pivot state.

Conceptually:

```text
CapturePayment
⇒
Trip has reached COMPLETED/allowed capture point
```

## 10.8 DSI-028 — Completed Trip Does Not Regress on Capture Failure

If physical ride is already:

```text
COMPLETED
```

and Payment capture fails/transiently remains unresolved:

```text
Trip remains COMPLETED
```

Payment can become:

```text
CAPTURE_PENDING
REQUIRES_REVIEW
```

This is forward recovery.

---

# 11. Notification domain

## 11.1 Notification is downstream

Notification may consume:

- Trip updates;
- Driver events;
- Payment outcomes.

It does not mutate their authority.

## 11.2 DSI-029 — Notification Independence

Notification delivery failure cannot change authoritative:

```text
Trip
Driver
Payment
Matching
```

business state.

## 11.3 Notification idempotency

If same logical notification event is redelivered, Notification worker may:

- deduplicate;
- suppress duplicate delivery where identity supports it;
- record repeated attempts.

The exact UX delivery guarantee is not a core safety invariant.

---

# 12. Cross-service invariants

Local invariants alone are insufficient because many failures arise at service boundaries.

## 12.1 DSI-030 — Driver–Trip Assignment Agreement

If a Trip claims current assignment:

```text
trip.assignedDriverId = D
trip.reservationId = R
```

then Driver authority must either:

```text
currently own R for that Trip
```

or the system must be in a recognized temporary cleanup/reconciliation state caused by asynchronous propagation.

A stable final state may not retain contradictory claims.

## 12.2 DSI-031 — No New Driver Before UNKNOWN Is Resolved

If reservation outcome for current candidate is unknown:

```text
Matching cannot authoritatively move to another candidate
```

until same `reservationId` is reconciled.

This is the most important cross-service unknown-outcome invariant.

## 12.3 DSI-032 — Cancellation Does Not Regress

Once Trip commits:

```text
CANCELLED
```

late:

- DriverReserved;
- DriverAccepted;
- PaymentAuthorized;

cannot reactivate the Trip.

They may trigger:

- cleanup;
- compensation;
- no-op;
- metrics.

## 12.4 DSI-033 — Exact Cleanup Identity

Any Driver cleanup triggered by:

- cancellation;
- Payment failure;
- matching cancellation;
- stale event;

must identify the exact reservation:

```text
tripId + reservationId
```

rather than only Driver ID.

## 12.5 DSI-034 — One Logical Payment Effect per Saga Step

Saga retry, broker redelivery and Payment retry may repeat transport execution, but:

```text
one Saga logical Payment step
⇒
one operationKey
⇒
≤ 1 logical Payment effect
```

## 12.6 DSI-035 — Terminal Matching Result Must Be Durable

Matching cannot ACK/forget a RideRequested after deciding:

```text
NO_DRIVER
```

unless the terminal state/result intent is durably represented.

Otherwise restart/redelivery could recompute a contradictory result.

---

# 13. Liveness obligations

Safety says what cannot happen.

These obligations say what should eventually happen under recovery assumptions.

## DSL-001 — Reservation Reconciliation Terminates

If:

```text
Reserve attempt = UNKNOWN
```

and Driver Service/database eventually become reachable, Matching eventually determines one conclusive result:

```text
RESERVED by same reservation
or
NOT RESERVED / definitive rejection
or
visible REQUIRES_REVIEW/terminal error if policy cannot resolve
```

It must not remain silently unknown forever.

## DSL-002 — Pre-start Reservation Cleanup

If Trip reaches a pre-start cancelled/failed terminal state and an exact Driver reservation still exists:

```text
dependencies recover
⇒
reservation eventually released
or visible unresolved/review state exists
```

No permanent hidden Driver leak.

## DSL-003 — Matching Round Terminates

A MatchingRound eventually reaches:

```text
RESERVED
NO_DRIVER
CANCELLED
FAILED/REVIEW
```

assuming:

- bounded candidate set;
- finite retry/reconcile policy;
- dependencies eventually recover or produce explicit terminal failure.

## DSL-004 — Payment Operation Resolution

A durable Payment operation eventually reaches:

```text
SUCCEEDED
DECLINED
VOIDED
REQUIRES_REVIEW
```

rather than being forgotten in memory.

## DSL-005 — Saga Workflow Resolution

A persisted Saga eventually:

```text
completes
or
compensates
or
becomes explicit review/error
```

under recovery assumptions.

## DSL-006 — Outgoing Intent Is Not Silently Lost

If a local authoritative transaction commits an outgoing durable intent:

```text
dependency recovery
⇒
intent eventually publishes
or becomes visible terminal publication failure
```

Exact mechanics in Doc 02.

## DSL-007 — Notification Failure Is Non-blocking

Notification outage may delay/suppress notification delivery but must not block authoritative ride completion indefinitely.

---

# 14. Race taxonomy

This section freezes acceptable outcomes for key concurrent scenarios.

## DSR-001 — Many Trips contend for one Driver

Initial:

```text
Driver D = AVAILABLE
```

Concurrent:

```text
T1 → Reserve(D,R1)
T2 → Reserve(D,R2)
...
TN → Reserve(D,RN)
```

Required:

```text
success count = 1
conflict/no-success count = N-1
Driver has exactly one current reservation
```

If N=100:

```text
expected legitimate conflict count can be 99
```

High conflict count is not an error in this intentionally adversarial scenario.

## DSR-002 — Duplicate same reservation request

```text
Reserve(D,T,R)
Reserve(D,T,R)
```

May race/retry.

Required:

```text
one reservation effect
both callers may observe canonical same logical success/current state
```

## DSR-003 — Different reservation while already reserved

Initial:

```text
D RESERVED T1/R1
```

Input:

```text
Reserve(D,T2,R2)
```

Required:

```text
conflict
state remains T1/R1
```

## DSR-004 — GoOffline vs Reserve

Initial:

```text
D AVAILABLE
```

Race.

Allowed:

```text
OFFLINE
or
RESERVED
```

depending on which guarded transition commits first.

Forbidden:

```text
both succeed semantically
```

## DSR-005 — Stale Release

Initial chronology:

```text
R1 existed
R1 released
R2 current
late Release(R1)
```

Required:

```text
R2 survives
```

## DSR-006 — Cancel vs Reserve request in flight

Chronology:

```text
Trip MATCHING
Matching sends Reserve(R)
Rider Cancel races
```

Possible:

### Case A — cancel commits before reservation

```text
Trip CANCELLED
Reserve conflicts/is rejected or later cleaned if it commits unexpectedly under race boundary
```

### Case B — Driver reservation commits first

```text
Trip cancellation remains terminal
late/current DriverReserved becomes cleanup evidence
exact Release(R)
```

Required:

```text
Trip never reactivates
Driver reservation does not leak permanently
```

## DSR-007 — Reserve success, response lost

```text
Driver commits R
response suppressed/lost
Matching sees timeout
```

Required:

```text
attempt = UNKNOWN
same R reconciled
no next candidate before conclusive result
```

## DSR-008 — Matching crash after remote reserve commit

```text
attempt persisted
Driver commits R
Matching crashes before local success
```

After restart:

```text
load same unresolved attempt
reconcile R
converge round
```

Forbidden:

```text
skip D1 and reserve D2
```

## DSR-009 — Duplicate RideRequested

Same logical input/round is delivered more than once.

Required:

```text
one durable MatchingRound
no contradictory terminal result
no duplicate independent reservation sequence
```

## DSR-010 — Matching terminal commit, result publication uncertainty

```text
Matching records NO_DRIVER + result intent
publish may duplicate
```

Required:

```text
same round remains NO_DRIVER
same logical result identity can be replayed
Trip cannot process duplicate as multiple logical terminal effects
```

## DSR-011 — Duplicate Payment command

```text
Authorize(K)
Authorize(K)
```

Required:

```text
one logical authorization
same canonical result
```

## DSR-012 — Payment timeout after effect

```text
provider simulation commits effect
response lost
```

Required:

```text
same operationKey K
reconcile/retry K
no K2 for same logical step
```

## DSR-013 — Payment failure after Driver reservation

```text
Driver reservation committed
Driver accepted
Payment authorization terminal failure
```

Required:

```text
Trip reaches legal cancelled/failure terminal
exact Driver reservation is eventually released
no leaked Driver
```

## DSR-014 — Capture failure after completed ride

```text
Trip COMPLETED
Capture fails/transient
```

Required:

```text
Trip remains COMPLETED
Payment unresolved/pending/review
```

## DSR-015 — Stale Driver state projection event

```text
Redis has v10 RESERVED
late v9 AVAILABLE arrives
```

Required:

```text
projection remains at v10
```

## DSR-016 — Stale location

```text
latest accepted seq=20
late seq=19
```

Required:

```text
seq20 remains newest
```

---

# 15. State-machine correctness reasoning

## 15.1 Why state machines are stronger than scattered if-statements

Without explicit state machines, handlers often become:

```text
if current != completed
    maybe update
```

This creates hidden paths.

A finite state machine provides:

```text
finite states
finite inputs
explicit legal edges
explicit rejected edges
```

Then tests can cover:

```text
legal transition table
illegal transition table
idempotent replay cases
```

## 15.2 Transition completeness

For each `(state, command)` pair, the design should classify outcome as exactly one of:

```text
ALLOW
IDEMPOTENT/CANONICAL SUCCESS
CONFLICT/REJECT
NO-OP STALE
```

Avoid:

```text
undefined
```

for a production command.

## 15.3 Terminal monotonicity proof sketch

Let terminal set:

```text
F = {COMPLETED, CANCELLED, MATCH_FAILED}
```

For every public transition handler:

```text
if current ∈ F
then next ∈ F
```

If this holds for all transitions and initial Trip is valid, then by induction terminal state cannot regress.

Doc 03 will turn this into table-driven tests.

---

# 16. Consistency model by domain fact

Not every fact requires the same consistency strength.

| Fact | Required consistency | Authority |
|---|---|---|
| Driver current reservation | strong/local | Driver PostgreSQL transaction |
| Driver availability | strong/local authoritative; projection eventual | Driver |
| Trip current state | strong/local | Trip |
| Trip current assignment reference | strong/local in Trip; cross-service eventual reconciliation | Trip |
| Matching candidate list | eventual/stale tolerated | Redis projection |
| Matching attempt ledger | strong/local | Matching DB |
| Payment operation result | strong/local | Payment |
| Notification delivered | eventual | Notification |
| trace/log visibility | best-effort diagnostic | observability stack |

The architecture does not label the entire application simply:

```text
"eventually consistent"
```

It specifies consistency per fact.

---

# 17. CAP trade-off by operation

## 17.1 ReserveDriver

During uncertainty/partition:

```text
prefer safety/consistency
over successful availability
```

If Driver authority cannot be reached:

```text
no fake reservation success
```

## 17.2 Candidate discovery

Redis can remain available with slightly stale data.

A stale candidate may produce conflict later.

Therefore candidate discovery can favor:

```text
availability/latency
```

because final authority protects safety.

## 17.3 Notification

Notification can be delayed/lost without changing business truth.

High availability of notification is lower correctness priority than Driver/Trip authority.

---

# 18. Idempotency algebra

## 18.1 Conceptual property

For operation keyed by `k`:

```text
Effect(k, request)
```

replay should satisfy:

```text
EffectCount(k) ≤ 1
```

and:

```text
Replay(k, equivalentRequest)
→ canonical prior/current result
```

## 18.2 Conflicting key reuse

Idempotency does not mean:

```text
same key accepts any new payload
```

For:

```text
Fingerprint(request1) ≠ Fingerprint(request2)
```

same key reuse should produce:

```text
conflict
```

because otherwise the key loses semantic meaning.

## 18.3 Different idempotency scopes

### HTTP create

```text
Idempotency-Key
```

guards one logical create request.

### Payment

```text
operationKey
```

guards one business payment effect.

### Consumer

```text
messageId
```

guards one local message effect.

### Driver reservation

```text
reservationId
```

guards one reservation effect and fences stale release.

These are not interchangeable.

---

# 19. UNKNOWN outcome as first-class state

## 19.1 Why UNKNOWN must be modeled

If the application has only:

```text
SUCCESS
FAILURE
```

then a timeout is usually forced into FAILURE.

That causes unsafe retry/switch behavior.

A correct distributed state machine must permit:

```text
UNKNOWN
```

when caller lacks sufficient evidence.

## 19.2 UNKNOWN is not permanent indecision

UNKNOWN must trigger:

```text
reconciliation
```

using same identity.

Otherwise liveness fails.

## 19.3 Reservation reconciliation decision table

For:

```text
tripId=T
reservationId=R
driverId=D
```

query current Driver authority.

Possible result:

### OWNED_BY_SAME_RESERVATION

```text
treat reserve as successful
```

### AVAILABLE / NOT_COMMITTED

```text
conclusive only with fencing: the Driver version Vx returned with the query
must exceed every dispatched fence for R (Vx > max_dispatched_version).
Otherwise an earlier Reserve(R) may still commit later (pre-lock: it has not
yet acquired the row lock; pre-commit: it holds the lock uncommitted while
the query sees last-committed AVAILABLE) and the attempt must remain UNKNOWN;
next candidate must NOT be considered until fenced proof or visible review
(DSI-017/031). Legacy rows without fence provenance stay UNKNOWN.
```

### RESERVED_BY_OTHER

```text
current attempt failed/conflicted
```

### ON_TRIP/OFFLINE

```text
conclusive incompatible state
```

### DEPENDENCY_UNAVAILABLE

```text
remain UNKNOWN
```

---

# 20. Compensation semantics

## 20.1 Compensation is not rollback

Suppose:

```text
Driver reserve committed at 10:00
Payment fails at 10:01
```

A database rollback cannot span two independent services and time.

The system issues a new business command:

```text
ReleaseDriver(exact reservation)
```

That is compensation.

## 20.2 Compensation must be idempotent

If the same compensation is delivered twice:

```text
Release(R)
Release(R)
```

the second must not create a new effect.

## 20.3 Compensation must be fenced

If state has moved to a newer reservation:

```text
Release(old R)
```

cannot damage it.

## 20.4 Compensation can fail

If Driver Service is unavailable:

```text
Trip may already be terminal CANCELLED
while cleanup remains pending
```

This is legal temporary distributed inconsistency if:

- exact cleanup identity is durable;
- liveness mechanism continues;
- Driver is not incorrectly made available by another authority.

---

# 21. Derived projection correctness

## 21.1 Safety rule

Redis can be wrong temporarily.

But:

```text
stale Redis
⇒ maybe wasted reserve attempt
```

not:

```text
stale Redis
⇒ double assignment
```

Formally:

```text
R(t) ≠ A(t)
does not imply
SafetyInvariantViolation
```

where:

```text
R(t) = Redis projection
A(t) = Driver authority
```

## 21.2 Projection monotonicity

For Driver state version:

```text
apply event v only if v > projectedVersion
```

For location:

```text
apply only if freshness/session order is newer
```

A short atomic Redis function can update metadata and GEO membership consistently, but the exact implementation belongs to Doc 02.

---

# 22. Domain error taxonomy

A distributed application needs semantic error classes.

Suggested domain/application classes:

```text
VALIDATION_ERROR
AUTHENTICATION_REQUIRED
FORBIDDEN
NOT_FOUND
DOMAIN_CONFLICT
STALE_VERSION
STALE_ASSIGNMENT
IDEMPOTENCY_CONFLICT
DEPENDENCY_UNAVAILABLE
UNKNOWN_OUTCOME
TERMINAL_STATE
SCHEMA_UNSUPPORTED
REQUIRES_REVIEW
```

These are more meaningful than:

```text
500 Internal Server Error
```

for every non-happy case.

Exact HTTP mapping belongs to Doc 02.

---

# 23. Legal duplicates and stale inputs

Not every duplicate is an error.

## 23.1 Duplicate current success

Examples:

```text
Reserve same reservation
Authorize same operationKey
consume same messageId
```

may be legal idempotent replay.

## 23.2 Stale old operation

Examples:

```text
Release old reservation
DriverReserved old matchingRound
old aggregateVersion event
old location sequence
```

should not be processed as current success.

They become:

```text
no-op
conflict
ignored stale
cleanup evidence
```

according to context.

---

# 24. Formal invariant catalog

This is the canonical quick-reference catalog.

| ID | Name | Type | Authority |
|---|---|---|---|
| DSI-001 | Exclusive Driver Reservation | safety | Driver |
| DSI-002 | Driver State/Reservation Coherence | safety | Driver |
| DSI-003 | Legal Driver Transition | safety | Driver |
| DSI-004 | Guarded Reservation Release | safety | Driver |
| DSI-005 | Reserve Idempotency | safety | Driver |
| DSI-006 | Monotonic Accepted Location | safety/projection | Driver |
| DSI-007 | Projection Version Non-regression | safety/projection | Matching/Redis |
| DSI-008 | Single Current Trip Assignment | safety | Trip |
| DSI-009 | Assignment Identity Coherence | safety | Trip |
| DSI-010 | Terminal Monotonicity | safety | Trip |
| DSI-011 | Start Requires Current Assignment | safety | Trip |
| DSI-012 | Completion Requires In-Progress | safety | Trip |
| DSI-013 | One Active Trip per Rider | safety/product | Trip |
| DSI-014 | Fare Snapshot Stability | safety | Trip |
| DSI-015 | One Terminal Matching Outcome | safety | Matching |
| DSI-016 | Current-Round Guard | safety | Trip/Matching |
| DSI-017 | Unresolved Attempt Blocks Next Candidate | safety | Matching |
| DSI-018 | Attempt Identity Stability | safety | Matching |
| DSI-019 | Deterministic Candidate Ordering | reproducibility | Matching |
| DSI-020 | Eligibility Before Ranking | safety | Matching |
| DSI-021 | Stage Monotonicity | safety | Matching |
| DSI-022 | No Stage Advance on UNKNOWN | safety | Matching |
| DSI-023 | No Endless Fresh Retry of Conclusive Candidate | liveness/support | Matching |
| DSI-024 | Payment Operation Idempotency | safety | Payment |
| DSI-025 | Payment Key Semantic Stability | safety | Payment |
| DSI-026 | Authorization Before Start | safety | Trip+Payment |
| DSI-027 | Capture Ordering | safety | Saga/Payment |
| DSI-028 | Completion Does Not Regress on Capture Failure | safety | Trip |
| DSI-029 | Notification Independence | safety | all/Notification |
| DSI-030 | Driver–Trip Assignment Agreement | cross-service safety | Driver+Trip |
| DSI-031 | No New Driver Before UNKNOWN Resolved | safety | Matching |
| DSI-032 | Cancellation Does Not Regress | safety | Trip |
| DSI-033 | Exact Cleanup Identity | safety | Trip+Driver |
| DSI-034 | One Payment Effect per Saga Step | safety | Saga+Payment |
| DSI-035 | Durable Terminal Matching Result | safety/recovery | Matching |

Liveness catalog:

| ID | Name |
|---|---|
| DSL-001 | Reservation Reconciliation Terminates |
| DSL-002 | Pre-start Reservation Cleanup |
| DSL-003 | Matching Round Terminates |
| DSL-004 | Payment Operation Resolution |
| DSL-005 | Saga Workflow Resolution |
| DSL-006 | Outgoing Intent Not Silently Lost |
| DSL-007 | Notification Failure Is Non-blocking |

---

# 25. Invariant dependency graph

Some invariants depend on lower-level invariants.

Conceptually:

```text
DSI-001 Exclusive Driver
    ↑
    ├── DSI-003 legal Driver transitions
    ├── DSI-004 fencing
    └── database conditional mutation

DSI-008 Single Trip Assignment
    ↑
    ├── DSI-016 current-round guard
    ├── DSI-017 unresolved blocks next candidate
    └── DSI-030 Driver–Trip agreement

DSI-032 Cancellation Monotonicity
    ↑
    ├── DSI-010 terminal monotonicity
    └── DSI-033 exact cleanup identity

DSI-034 One Payment Effect
    ↑
    ├── DSI-024 Payment idempotency
    └── stable operationKey

DSL-005 Saga Resolution
    ↑
    ├── durable Saga state
    ├── DSL-002 cleanup
    ├── DSL-004 Payment resolution
    └── DSL-006 durable outgoing intent
```

This dependency model helps identify which lower-level mechanism is actually responsible when a higher-level scenario fails.

---

# 26. Database defense contracts

Doc 01 freezes **what must be defended**, not exact SQL.

Recommended persistence defenses:

## Driver

```text
unique reservationId
conditional status/version update
active reserved Trip uniqueness where schema supports it
```

## Trip

```text
unique tripId
one-active-Trip-per-Rider defense if frozen P0
idempotency key uniqueness/scoping
version/concurrency token
```

## Matching

```text
UNIQUE(tripId, matchingRound)
UNIQUE(inputMessageId)
UNIQUE(reservationId)
```

## Payment

```text
UNIQUE(operationKey)
```

## Consumer dedup

```text
UNIQUE(consumerScope, messageId)
```

Microsoft DDD guidance places invariant responsibility in the domain model, but database constraints remain important defense-in-depth against concurrent writes.

---

# 27. Transaction isolation implications

PostgreSQL's default `READ COMMITTED` permits each statement to observe a snapshot current at statement start.

Therefore this reasoning is unsafe:

```text
SELECT driver WHERE status='AVAILABLE'
→ application decides available
→ later unconditional UPDATE
```

because another transaction can change state between statements.

Correctness must be enforced at:

- guarded UPDATE;
- row lock where intentionally used;
- unique constraint;
- transaction boundary.

The architecture does not require globally serializable isolation for every transaction.

It uses the weakest practical isolation plus explicit guards that make each critical transition correct.

---

# 28. Domain-level failure classification

For correctness, failures should be classified by semantic effect.

## 28.1 Conclusive business rejection

Examples:

```text
Driver not AVAILABLE
stale reservation
Trip terminal
Payment declined
```

Caller may proceed according to workflow.

## 28.2 Validation/auth failure

Request never becomes a valid business operation.

No retry with unchanged invalid data.

## 28.3 Transient dependency failure with no side-effect ambiguity

Safe retry may be possible.

Example:

```text
read-only GET failed before response
```

depending on operation.

## 28.4 UNKNOWN side-effect outcome

Examples:

```text
Reserve timed out
Payment provider effect response lost
```

Must reconcile same identity.

## 28.5 Visible unresolved/review

If automatic progress cannot safely continue:

```text
REQUIRES_REVIEW
DLQ/error storage
```

is superior to silently guessing.

---

# 29. Saga domain contract

Exact orchestration mechanics belong to Doc 02.

This file freezes the business meaning.

## 29.1 Saga goal

Coordinate:

```text
Trip
Driver
Payment
```

without distributed ACID.

## 29.2 Simplified Saga phases

### Phase A — pre-reservation/matching

No Driver yet.

### Phase B — Driver reserved/accepted

Driver resource is held.

### Phase C — Payment authorization

May succeed or trigger compensation.

### Phase D — ready/start

Approaches physical pivot.

### Phase E — ride in progress/completed

Past ordinary rollback point.

### Phase F — capture/closure

Failures require forward recovery.

## 29.3 Compensable steps

Examples:

```text
Driver reservation
Payment authorization
```

can have compensation:

```text
ReleaseDriver
VoidAuthorization
```

depending on current state.

## 29.4 Pivot / point of no return

Once physical ride reaches the configured pivot:

```text
IN_PROGRESS
```

or at latest:

```text
COMPLETED
```

the system cannot pretend the ride never happened.

Post-pivot failure uses forward recovery.

Microsoft's Saga guidance explicitly distinguishes compensable transactions and pivot transactions serving as the point of no return.

---

# 30. Domain acceptance scenarios

These are semantic scenarios, not exact Doc 03 test IDs.

## AC-01 — Happy ride

```text
Driver AVAILABLE
Trip created
Matching reserves Driver
Driver accepts
Payment authorizes
Trip starts
Trip completes
Payment capture resolves
```

All invariants remain true.

## AC-02 — Double assignment attack

100 Trips contend for one Driver.

Required:

```text
one current reservation
```

## AC-03 — Duplicate messages everywhere

Replay:

- RideRequested;
- DriverReserved;
- PaymentAuthorized;
- Complete-related integration messages.

Required:

```text
no second logical domain effect
```

## AC-04 — Old compensation arrives late

Required:

```text
new reservation survives
```

## AC-05 — Matching timeout after Driver commit

Required:

```text
UNKNOWN → reconcile same reservation
```

## AC-06 — Cancel during in-flight reservation

Required:

```text
Trip remains terminal
Driver eventually not leaked
```

## AC-07 — Saga restart

Kill Trip Service with pending Payment/compensation.

Required:

```text
restart resumes from durable workflow state
```

## AC-08 — Payment capture failure after ride

Required:

```text
Trip completed
Payment unresolved/review
```

## AC-09 — Redis stale AVAILABLE

Required:

```text
Driver authority conflicts
no double assignment
```

## AC-10 — Redis unavailable

Required:

```text
Matching degraded/unavailable
existing Driver/Trip safety preserved
```

---

# 31. Invariant-auditor contract

The test-only auditor may read service databases with read-only test credentials.

It is not production business code.

Its job is to independently inspect final state.

Minimum checks:

```text
DSI-001 Exclusive Driver Reservation
DSI-002 Driver State Coherence
DSI-008 Single Trip Assignment
DSI-010 Terminal Monotonicity where history/evidence supports check
DSI-024 Payment logical uniqueness
DSI-030 Driver–Trip Assignment Agreement
DSL-002 no leaked pre-start reservation after recovery scenario
```

Example result:

```text
DSI-001 Exclusive Driver Reservation .... PASS
DSI-004 Guarded Release ................. PASS
DSI-008 Single Trip Assignment .......... PASS
DSI-024 Payment Idempotency ............. PASS
DSI-030 Driver/Trip Agreement ........... PASS
```

Logs are supporting evidence.

Final authoritative state is the primary correctness evidence.

---

# 32. What this domain model intentionally does not solve

## 32.1 Driver fairness as a hard invariant

Fairness may be measured or used in ranking.

It is not a P0 safety invariant.

## 32.2 Exact ETA

No production routing engine.

Distance/ETA proxy does not determine correctness.

## 32.3 Dynamic pricing consistency

P0 fare snapshot is deterministic.

No marketplace surge consistency problem.

## 32.4 Multi-ride pooling

One Driver/one active Trip assumption keeps P0 semantics clear.

Ride sharing/pooling would invalidate several invariants and requires a new model.

## 32.5 Byzantine service behavior

Services are assumed non-malicious.

Authentication/authorization still protects external actors.

---

# 33. Relationship to old DS03 v1 invariants

The old research/freeze used `DOM-I*`.

The rewrite replaces that namespace with explicit DS-focused IDs.

Conceptual compatibility:

| Old concept | New canonical |
|---|---|
| `DOM-I1` exclusive Driver | `DSI-001` |
| `DOM-I2` legal Driver availability | `DSI-002/003` |
| `DOM-I3` single Trip assignment | `DSI-008/030/031` |
| `DOM-I4` terminal monotonicity | `DSI-010/032` |
| `DOM-I5` guarded compensation | `DSI-004/033` |
| `DOM-I6` Payment/idempotent effect | `DSI-024/025/034` |
| `DOM-I7` Payment ordering | `DSI-026/027/028` |
| `DOM-I8` Fare stability | `DSI-014` |
| `DOM-I9` eventual reservation cleanup | `DSL-002` |
| `DOM-I10` workflow resolution | `DSL-005/006` |

This is a documentation namespace refactor, not a weakening of the old correctness model.

---

# 34. Implementation guidance derived from the contract

These are consequences, not infrastructure specifications.

## 34.1 Domain methods over setters

Each aggregate should expose behavior.

## 34.2 Current identity in every stale-sensitive command

Examples:

```text
reservationId
matchingRound
operationKey
version
```

## 34.3 Persist unresolved work before dangerous remote call

For Matching:

```text
persist attempt/reservationId
before/around remote reserve
```

so restart can reconcile.

## 34.4 Never switch candidate on ambiguous timeout

Unknown must be resolved first.

## 34.5 Keep state transition and outgoing intent atomic where required

Doc 02 implements via Outbox.

## 34.6 Consumer should be both dedup-safe and state-machine-safe

Dedup alone does not protect:

```text
different old messageId
```

State guards also reject stale legal history.

---

# 35. Proof obligations for Doc 03

Every DSI/DSL must map to at least one of:

```text
unit/property test
integration test
concurrency test
fault test
invariant auditor
experiment
```

Examples:

| Contract | Minimum proof style |
|---|---|
| DSI-001 | synchronized real PostgreSQL concurrency |
| DSI-004 | delayed stale-release integration test |
| DSI-010 | table-driven state-machine tests + late event test |
| DSI-017 | fault-injected reserve timeout/reconciliation |
| DSI-024 | concurrent same-operationKey integration test |
| DSI-028 | capture failure after completed Trip |
| DSL-002 | fault/reconnect cleanup test |
| DSL-005 | Saga restart/resume |
| DSL-006 | Outbox crash/restart |

Doc 03 will assign exact IDs, scripts, repetition counts and evidence format.

---

# 36. Report-ready theory summary

The final report can summarize the correctness model as:

> The system separates **local strong consistency** from **cross-service eventual consistency**. Each critical business fact has exactly one authoritative service and local transaction boundary. Concurrency is resolved at the authority using guarded mutation and persistence constraints. Distributed retries preserve stable logical identities, while timeouts of side-effecting calls are modeled as **unknown outcomes** until reconciled. Asynchronous delivery is treated as at-least-once, so consumers combine deduplication with state-machine guards. Long-running cross-service workflow consistency is achieved through a persisted Saga and compensating actions rather than global ACID rollback. Derived Redis state may be stale, but stale reads can only reduce matching efficiency because final reservation safety is enforced by Driver authority.

---

# 37. Authoritative external references

These references validate theory/implementation choices. They do not override project-specific contracts.

## Domain invariants / aggregates

1. Microsoft Learn — **Design validations in the domain model layer**  
   https://learn.microsoft.com/en-us/dotnet/architecture/microservices/microservice-ddd-cqrs-patterns/domain-model-layer-validations

   Key point: invariants belong in domain entities/aggregate root and entities should remain valid through state changes.

2. Microsoft Learn — **Design a microservice domain model**  
   https://learn.microsoft.com/en-us/dotnet/architecture/microservices/microservice-ddd-cqrs-patterns/microservice-domain-model

   Key point: aggregate root is consistency guardian and normal entry point for updates.

3. Microsoft Learn — **Implement a microservice domain model with .NET**  
   https://learn.microsoft.com/en-us/dotnet/architecture/microservices/microservice-ddd-cqrs-patterns/net-core-microservice-domain-model

   Key point: domain model should not depend directly on infrastructure ORM concerns.

## Saga / compensation

4. Azure Architecture Center — **Saga distributed transactions pattern**  
   https://learn.microsoft.com/en-us/azure/architecture/patterns/saga

   Key points:

   - sequence of local transactions;
   - compensable transaction;
   - pivot transaction / point of no return;
   - compensation rather than global rollback.

## PostgreSQL concurrency

5. PostgreSQL 18 — **Concurrency Control**  
   https://www.postgresql.org/docs/18/mvcc.html

6. PostgreSQL 18 — **Transaction Isolation**  
   https://www.postgresql.org/docs/18/transaction-iso.html

7. PostgreSQL 18 — **Unique Indexes**  
   https://www.postgresql.org/docs/18/indexes-unique.html

Use these to reason about:

- READ COMMITTED;
- concurrent writers;
- row/update behavior;
- persistence-level uniqueness defense.

## RabbitMQ delivery semantics

8. RabbitMQ 4.3 — **Consumer Acknowledgements and Publisher Confirms**  
   https://www.rabbitmq.com/docs/confirms

RabbitMQ explicitly states publisher confirms and consumer acknowledgements are orthogonal mechanisms protecting different communication boundaries.

## OpenTelemetry messaging

9. OpenTelemetry — **Messaging semantic conventions**  
   https://opentelemetry.io/docs/specs/semconv/messaging/

Messaging semantic conventions are currently marked Development; use them for diagnostics and pin emitted conventions where necessary.

---

# 38. Freeze checklist

Before promoting Doc 01 to **ACCEPTED/FROZEN**:

## Vocabulary

- [ ] every aggregate has one authoritative service;
- [ ] `tripId`, `matchingRound`, `reservationId`, `operationKey`, `messageId` roles are unambiguous;
- [ ] no business rule depends on `traceId`.

## Driver

- [ ] state machine frozen;
- [ ] DSI-001 through DSI-005 accepted;
- [ ] GoOffline-vs-Reserve outcome accepted;
- [ ] stale release fencing accepted.

## Location/Redis

- [ ] stale location rule accepted;
- [ ] aggregate version projection rule accepted;
- [ ] Redis remains derived.

## Trip

- [ ] Trip states frozen;
- [ ] terminal set frozen;
- [ ] single-assignment rule frozen;
- [ ] cancellation semantics frozen;
- [ ] FareSnapshot stability accepted.

## Matching

- [ ] MatchingRound states frozen;
- [ ] Attempt states frozen;
- [ ] UNKNOWN semantics frozen;
- [ ] next candidate blocked until conclusive;
- [ ] deterministic ranking property accepted;
- [ ] stage monotonicity accepted.

## Payment

- [ ] operation types frozen;
- [ ] operationKey semantics accepted;
- [ ] duplicate/conflicting key behavior accepted;
- [ ] post-completion capture failure forward-recovery accepted.

## Cross-service

- [ ] Driver–Trip agreement contract accepted;
- [ ] exact cleanup identity accepted;
- [ ] terminal Matching outcome durability accepted;
- [ ] Saga logical Payment identity accepted.

## Liveness

- [ ] every UNKNOWN state has a reconciliation path;
- [ ] every compensation can terminate or become visible review;
- [ ] no “retry forever invisibly” obligation exists.

## Proofability

- [ ] every critical invariant has a planned executable proof;
- [ ] final-state auditor can inspect critical outcomes;
- [ ] no invariant relies solely on log ordering.

---

# 39. North star

> **A distributed ride-hailing workflow is correct not because every message arrives once or every service sees the same state instantly, but because each business fact has a clear authority, every state change is guarded by stable identity and legal transition rules, uncertainty is modeled explicitly, stale work is fenced, duplicates are harmless, and durable recovery eventually converges the system to one legal explainable outcome.**
