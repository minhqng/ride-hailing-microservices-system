# DS03 DISTRIBUTED RIDE-HAILING SYSTEM
## Project Scope · Architecture Baseline · Distributed-Systems Foundations

> **Document:** `docs/00-project-scope-architecture.md`  
> **Course:** Distributed Systems  
> **Topic:** **DS03 — Ride-hailing Microservices System simulating Grab/Uber**  
> **Type:** Project Charter + Architecture Baseline + Distributed-Systems Theory / Internal RFC  
> **Status:** **PROPOSED FREEZE v2.0 — REVIEW CANDIDATE**  
> **Baseline date:** **2026-09-26**  
> **Team:** 3 members  
> **Planning horizon:** 16 weeks  
> **Normative role:** source of truth for project scope, system decomposition, authority boundaries, technology families, communication styles, deployment/failure assumptions, quality goals, architecture decisions, non-goals and course-requirement traceability.  
> **Downstream documents:**  
> `01-domain-correctness-contract.md` → domain vocabulary, state machines, invariants, legal outcomes  
> `02-distributed-workflow-engineering.md` → messages, Outbox, Saga, retry, dedup, delivery/recovery semantics  
> `03-verification-experiments-demo.md` → tests, fault campaigns, experiments, evidence, demo/defense  
> `04-team-execution-plan.md` → team timeline, ownership, gates  
> `05A/05B/05C` → individual chronological playbooks  
> **Executable source of truth:** source code, migrations, OpenAPI/message contracts, broker configuration, Compose files, CI and executable tests win over prose for exact implementation patch/config details after an approved freeze.  
> **Change policy:** changing a service boundary, data owner, authoritative state, persistence technology, broker, delivery claim, synchronous/asynchronous boundary, P0/P1 scope or failure model requires an architecture-change PR and an update to this document before or together with code.

> **Project thesis.**  
> **The project is not a miniature Grab/Uber feature clone. Ride-hailing is the domain used to demonstrate a deeper distributed-systems question: how independent services preserve business invariants and eventually recover when requests race, reads are stale, messages are duplicated or reordered, responses disappear, processes crash, and infrastructure is only partially available.**

---

# 0. Executive baseline

## 0.1 The problem we are actually solving

A ride-hailing system appears simple in a happy-path sequence:

```text
Passenger requests ride
→ find Driver
→ Driver accepts
→ ride starts
→ ride completes
→ payment
→ notification
```

The distributed-systems problem is hidden in the words **“find,” “accepts,” “completes,” and “payment.”**

Those actions happen across:

- independent processes;
- independent databases;
- network calls that can time out;
- asynchronous messages that can be redelivered;
- caches/read models that can be stale;
- multiple users acting concurrently;
- processes that can crash after committing but before acknowledging;
- workflows that span several local transactions.

Therefore a correct implementation cannot rely on assumptions such as:

```text
one request = one execution
timeout = operation failed
message delivered = message processed
event order = business order
cache state = authoritative state
service crash = workflow forgotten
```

The architecture instead adopts this central equation:

```text
DISTRIBUTED CORRECTNESS
=
explicit authority
+ local atomicity
+ stable logical identity
+ guarded state transition
+ durable intent
+ idempotency/deduplication
+ explicit uncertainty
+ eventual recovery
```

This equation is the project's main intellectual contribution.

---

## 0.2 Course-aligned research question

> **How can a microservice-based ride-hailing system preserve safety invariants and make observable progress under concurrent allocation, eventual consistency, at-least-once delivery, unknown remote outcomes, process crashes, broker/network interruption, and stale derived state?**

Sub-questions:

### RQ1 — Concurrency

How can many rides race for the same Driver while guaranteeing:

```text
at most one current reservation for that Driver
```

without a global distributed lock?

### RQ2 — Partial failure

If Matching calls Driver and receives a timeout, how can it determine whether:

- Driver never received the request;
- Driver rejected it;
- Driver committed a reservation but the response was lost?

### RQ3 — Messaging

If a consumer commits its database transaction and crashes before acknowledging RabbitMQ, how can redelivery avoid applying the same logical effect twice?

### RQ4 — Dual write

If a service commits authoritative state but crashes before publishing its integration event, how can downstream services eventually observe the committed intent?

### RQ5 — Long-running workflow

How can a ride workflow spanning Trip, Driver, Matching and Payment continue after a service restart without a cross-service ACID transaction?

### RQ6 — Stale state

How can Redis GEO remain fast and potentially stale without becoming a correctness authority?

### RQ7 — Recovery

Can every accepted business operation either:

```text
converge to a legal state
or
become an explicit observable unresolved/review state
```

instead of disappearing silently?

### RQ8 — Evaluation

Can the team prove the above properties through reproducible concurrency/fault experiments rather than diagrams or screenshots alone?

---

## 0.3 Architecture in one paragraph

The system uses **five authoritative business microservices**—Identity, Driver/Vehicle/Location, Trip/Booking, Matching, and Payment—plus an asynchronous Notification worker and an API Gateway. Each stateful business component owns its data and does not directly access another service's database. PostgreSQL is the local ACID authority; RabbitMQ carries integration commands/events with at-least-once semantics; Redis GEO is a Matching-owned derived read model for fast candidate discovery. Driver reservation is a PostgreSQL-guarded authority decision rather than a Redis lock. Trip coordinates the long-running ride workflow with a persisted Saga. Authoritative state transitions that require outgoing integration messages use a Transactional Outbox. Consumers deduplicate messages and ACK only after durable local processing. Unknown outcomes are reconciled using stable business identities rather than guessed from timeouts. Docker Compose provides reproducible independent processes and service-name discovery; OpenTelemetry provides cross-process evidence but never recovery state.

---

## 0.4 Scope baseline

### P0 — mandatory implementation and evidence

P0 contains the smallest architecture that can deeply demonstrate the course's distributed-systems concepts.

```text
Edge
- API Gateway
- authentication/authorization routing baseline

Business services
- Identity Service
- Driver / Vehicle / Location Service
- Trip / Booking Service
- Matching Service
- Payment Simulator Service
- Notification Worker

Communication
- REST/HTTP synchronous flows
- RabbitMQ asynchronous commands/events
- correlation/message/business identities

Data
- service-owned PostgreSQL database/role
- Matching-owned Redis GEO projection
- durable MatchingRound state
- Outbox / dedup state

Correctness
- Driver OCC/conditional reservation
- reservation fencing
- Trip/Driver/Payment state machines
- idempotency
- unknown-outcome reconciliation
- Saga/compensating actions

Reliability
- publisher confirms
- manual consumer ACK
- bounded retry
- DLQ/error visibility
- restart/resume
- deterministic faultpoints
- reconnect/backlog recovery

Observability
- OpenTelemetry traces/metrics/logs
- service/instance identity
- business correlation
- invariant auditor

Deployment/evaluation
- Docker Compose baseline
- 3-node RabbitMQ quorum validation profile
- functional/concurrency/fault/security/performance evidence
```

### P1 — optional only after the Core Release Gate

Examples:

- richer service-monitoring dashboards;
- selective gRPC comparison;
- additional consumer-scaling experiments;
- richer telemetry/report projections;
- optional realtime visualization;
- additional geospatial/fairness experiments.

### P2 / explicitly out of scope

The following are intentionally excluded from the baseline:

- full Grab/Uber feature parity;
- real payment network integration;
- ML/RL driver dispatch;
- production navigation/routing engine;
- production surge-pricing marketplace;
- Kafka in parallel with RabbitMQ;
- service mesh;
- Temporal/MassTransit migration purely for orchestration novelty;
- Debezium/CDC platform baseline;
- event sourcing as global persistence model;
- Redis distributed lock as assignment authority;
- Kubernetes as a P0 requirement;
- custom consensus implementation;
- multi-region active-active deployment;
- Byzantine-fault tolerance;
- real emergency/safety service;
- production-scale SLO claims.

**Scope principle:**

> Add a mechanism only if it closes a named distributed-systems problem or creates measurable evidence that the current architecture cannot provide more simply.

---

## 0.5 Quality goals

| ID | Goal | Meaning | Acceptance signal |
|---|---|---|---|
| **Q1 Correctness** | preserve invariants | no illegal Driver/Trip/Payment state despite races/replays | invariant tests + final-state auditor |
| **Q2 Recoverability** | no silent committed intent loss | crash/restart resumes or exposes unresolved work | Outbox/Saga/recovery fault tests |
| **Q3 Determinism** | same logical input is reproducible | ranking/test/fault result can be reconstructed | fixed fixtures + persisted identities |
| **Q4 Explainability** | causal path can be traced | team can explain HTTP + broker + DB transitions | trace + message/business IDs |
| **Q5 Reproducibility** | clean environment boots consistently | another reviewer can run it | versioned Compose/scripts |
| **Q6 Measurability** | performance/recovery is quantified | claims include workload/environment/raw data | experiment artifacts |
| **Q7 Security baseline** | identity/authority cannot be trivially forged | authz, validation, secret discipline | negative tests |
| **Q8 Scope discipline** | complexity is intentional | no redundant infrastructure | decision register + Core Gate |

---

# 1. Course specification decomposition

The course specification is treated as a hard external contract. This architecture intentionally exceeds several minimums, but it does not replace or reinterpret them.

## 1.1 General distributed nature

The course requires independent processes/services communicating over a network, unique identities, concurrency and logs.

The project implements this as:

```text
API Gateway                   independent process
Identity Service              independent process
Driver Service                independent process
Trip Service                  independent process
Matching Service              independent process
Payment Service               independent process
Notification Worker           independent process
RabbitMQ                      independent infrastructure process/cluster
PostgreSQL                    independent infrastructure process
Redis                         independent infrastructure process
OpenTelemetry Collector       independent observability process
```

No global variable/shared-memory shortcut is used for inter-service coordination.

---

## 1.2 DS03-specific requirement traceability

| Course requirement | Project implementation | Proof family |
|---|---|---|
| at least 4 business microservices | 5 authoritative services + Notification worker | topology/process evidence |
| each service owns separate data/schema | distinct DB/role per stateful component | DB permission/isolation tests |
| REST or gRPC | REST/HTTP baseline | API integration traces |
| asynchronous message queue | RabbitMQ | broker traces/tests |
| user registration/login + role | Identity | functional/security tests |
| Driver status/location updates | Driver service | functional + stale-location tests |
| passenger creates ride | Trip service | idempotency tests |
| find/select Driver | Matching + Redis GEO + Driver authority | matching/concurrency tests |
| Driver accept/reject | Trip/Driver workflow | state-machine tests |
| no Driver assigned to two Trips | Driver OCC + DB guard | synchronized contention |
| trip lifecycle | Trip FSM | transition tests |
| fare | deterministic FareSnapshot in Trip domain | functional tests |
| simulated payment | Payment service | idempotency/failure tests |
| notification | asynchronous Notification worker | delivery-independence tests |
| event IDs/idempotency keys | `messageId`, operation-specific keys | duplicate tests |
| retries | bounded classified retry | retry/DLQ tests |
| deduplication | consumer dedup + business uniqueness | redelivery tests |
| DLQ/error storage | finite retry + DLQ/error record | poison test |
| correlation IDs | `correlationId` + W3C trace context | trace evidence |
| Saga/compensation | persisted Trip Saga | compensation/restart tests |
| concurrent same Driver demo | controlled OCC race | concurrency scenario |
| stop service mid-flow | deterministic crash/fault/network isolation | recovery scenario |
| restart and continue | durable queue/Outbox/Saga | restart tests |

---

## 1.3 Testing minimum versus project target

Course minimum:

```text
8 functional
2 concurrency/conflict
2 fault/recovery
2 security/invalid-data
1 performance/scalability
```

Project target:

```text
10+ functional
4+ concurrency/conflict
8+ fault/recovery
4+ security/invalid-data
4 core performance/recovery experiments
```

The higher target is intentional because a distributed architecture cannot be credibly defended using only happy-path tests.

---

## 1.4 Demo requirement interpretation

Course demo requires:

- independent services;
- Driver available;
- passenger ride request;
- Matching selects Driver;
- Driver accepts/completes;
- two passengers race for same Driver;
- one service stops during business flow;
- retry/queue/compensation;
- restart and continued processing;
- logs/traces.

The architecture therefore treats **race + live failure** as first-class design inputs, not as demo scripts added at the end.

---

# 2. Distributed-systems foundations

This section is intentionally theoretical because implementation choices only make sense after the underlying failure/consistency model is explicit.

---

## 2.1 What makes this system distributed?

A system becomes meaningfully distributed when correctness depends on communication among independently failing processes.

In this project:

```text
Trip cannot directly transact Driver DB.
Matching cannot trust its local memory after crash.
Payment cannot atomically commit in Trip's transaction.
RabbitMQ delivery can be redelivered.
Redis can lag behind Driver state.
Network timeout hides the remote outcome.
```

There is no single atomic state transition spanning all services.

This creates four fundamental consequences:

1. **partial failure** exists;
2. **communication has uncertainty**;
3. **state is temporarily inconsistent across components**;
4. **recovery must be explicitly designed**.

---

## 2.2 Partial failure

In a monolith:

```text
function call returns
or
process crashes
```

and one database transaction can cover many business changes.

Across services:

```text
caller is alive
callee may be dead
network may be partitioned
request may arrive
response may not
broker may accept
consumer may crash
DB may commit
ACK may not happen
```

One component can fail while all others continue.

This property is why:

```text
try/catch
```

is insufficient as a distributed recovery model.

---

## 2.3 The timeout ambiguity theorem used by the project

When a side-effecting remote request times out, the caller cannot infer the remote state from the timeout alone.

For:

```text
ReserveDriver(driverId=D1, tripId=T1, reservationId=R1)
```

after timeout, any of these may be true:

```text
A. request never reached Driver
B. request reached Driver but failed before commit
C. reservation committed, response was lost
D. reservation committed, response is only delayed
```

Therefore:

```text
timeout ≠ failed
timeout = outcome unknown to caller
```

The project names this explicitly:

```text
UNKNOWN
```

and requires reconciliation with the **same logical identity**.

This is one of the most important concepts in the entire project.

---

## 2.4 Stable logical identity

A distributed system cannot safely retry if it cannot identify “the same work.”

Different identities solve different problems:

```text
tripId
    identity of one ride

matchingRound
    identity/version of one matching cycle for a Trip

reservationId
    identity/fencing token for one Driver reservation attempt/effect

operationKey
    identity of one logical Payment operation

messageId
    identity of one logical integration message

correlationId
    business-flow diagnostic correlation

traceId
    observability trace identity
```

A retry is safe only if the system can answer:

> Am I retrying the same logical effect or creating a new effect?

This is why generic “retry on exception” is dangerous for writes.

---

## 2.5 Safety versus liveness

Distributed correctness has two dimensions.

### Safety

> Something bad never happens.

Examples:

```text
one Driver is never actively reserved for two Trips
one Trip never has two current assignments
stale release never frees a newer reservation
duplicate Payment command never creates a second logical effect
terminal Trip never returns to active state
```

### Liveness

> Something good eventually happens, assuming dependencies recover.

Examples:

```text
pending Outbox row eventually publishes
UNKNOWN reservation eventually becomes conclusive
cancelled Trip eventually releases old reservation
Saga eventually progresses/compensates/becomes visible review state
```

A system can satisfy safety but fail liveness:

```text
never double-assign
but remain stuck forever
```

or appear live while violating safety:

```text
quickly assign another Driver after timeout
but first Driver was already reserved
```

Both dimensions are separately specified in Doc 01/02 and tested in Doc 03.

---

## 2.6 Local strong consistency, distributed eventual consistency

Inside one service/database:

```text
aggregate state
+ outbox row
+ consumer dedup marker
```

can use ACID transactions.

Across independent service databases:

```text
Trip
Driver
Matching
Payment
```

there is no baseline cross-service ACID transaction.

Therefore the consistency model is:

```text
strong/local consistency
inside an authoritative service transaction

eventual consistency
between services and derived projections
```

Microsoft's microservices data guidance similarly emphasizes service-owned data and notes that once data is split across microservices, cross-service business processes must tolerate eventual consistency rather than one global database transaction.

The design rule is stronger than simply saying “eventual consistency”:

> **Eventual consistency is acceptable for copies/projections/workflow visibility; safety invariants must still have a single explicit authority boundary.**

---

## 2.7 CAP theorem applied carefully

CAP is often misused as “pick two.”

A more useful application here is operation-specific.

During a network partition:

### Driver reservation

The project favors **consistency/safety** over availability.

If authoritative Driver state cannot be determined:

```text
do not fabricate a new successful reservation
```

The operation may become:

```text
UNKNOWN
dependency unavailable
```

rather than risking double assignment.

### Redis candidate lookup

The project can favor availability/latency.

Matching may use a stale read model:

```text
Redis says AVAILABLE
Driver DB says RESERVED
```

The stale candidate is allowed to be rejected by the authoritative reserve call.

Thus different operations make different consistency/availability trade-offs.

This is a more precise design than assigning one CAP label to the entire system.

---

## 2.8 Linearization point

For a concurrent operation, a useful reasoning technique is to identify the point at which it becomes authoritative.

For Driver reservation, the linearization point is the successful guarded PostgreSQL mutation/transaction, not:

```text
Redis query
HTTP request send
Matching local decision
UI display
```

If two calls race, only the database decision determines the winner.

This makes concurrency proof concrete.

---

## 2.9 Optimistic Concurrency Control

The project uses guarded writes rather than global distributed locks.

Conceptual:

```sql
UPDATE drivers
SET status = 'RESERVED',
    reserved_trip_id = :tripId,
    reservation_id = :reservationId,
    version = version + 1
WHERE driver_id = :driverId
  AND status = 'AVAILABLE'
  AND version = :expectedVersion;
```

Interpretation:

```text
affected rows = 1
→ this operation won

affected rows = 0
→ precondition is no longer true
```

Benefits for course scale:

- no lock service;
- correctness remains where state lives;
- easy to test with real concurrent requests;
- conflicts are explicit;
- integrates naturally with version/fencing semantics.

Database uniqueness can be an additional defense.

---

## 2.10 Fencing tokens

Idempotency prevents duplicate execution of the same logical action.

Fencing prevents an **old** action from corrupting a newer state.

Example:

```text
reservation RA
→ release RA
→ later reservation RB
→ delayed Release(RA) arrives
```

A release that checks only:

```text
driverId
```

can wrongly free RB.

A fenced release checks:

```text
driverId
tripId
reservationId = RA
```

and cannot affect RB.

Fencing is critical for:

- compensation;
- delayed messages;
- Retry/DLQ redrive;
- network partition recovery.

---

## 2.11 Idempotency

Operation `f` is idempotent if repeated application does not create additional logical effect:

```text
f(f(x)) = f(x)
```

In a business system, this usually means:

> replay with the same logical identity returns the current/canonical result rather than executing another logical action.

Examples:

```text
same CreateRide idempotency key
→ same logical Trip

same Payment operationKey
→ same logical payment operation

same messageId
→ same local consumer effect

same reservationId
→ same reservation attempt/effect
```

Idempotency is not the same as “HTTP method is PUT.”

It is a domain property backed by durable identity and constraints.

---

## 2.12 Message ordering

RabbitMQ can preserve order in specific queue/consumer conditions, but distributed workflow correctness should not depend on a global total order across services.

Instead:

```text
aggregate version
matchingRound
reservationId
operationKey
state-machine guard
```

allow a service to reject stale/out-of-order input.

Example:

```text
DriverReserved(v8)
DriverReleased(v9)
delayed DriverReserved(v8)
```

The Redis projection must not regress from v9 to v8.

The architectural principle is:

> Prefer **state-aware monotonic guards** over an unrealistic requirement that all distributed messages arrive globally in order.

---

## 2.13 At-least-once delivery

The project expects RabbitMQ deliveries to be redeliverable.

Classic failure:

```text
consumer receives M
→ DB transaction commits
→ consumer crashes
→ ACK never reaches broker
→ M is delivered again
```

Therefore duplicate delivery is normal.

The system targets:

```text
at-least-once transport
+
durable dedup / idempotent business effect
=
effectively-once logical effect within defined operation scope
```

No global exactly-once transport claim is made.

RabbitMQ's official documentation treats publisher confirms and consumer acknowledgements as separate data-safety mechanisms.

---

## 2.14 Publisher confirm versus consumer ACK

These answer different questions.

### Publisher confirm

```text
producer → RabbitMQ
```

Question:

> Did the broker accept this publication according to its persistence/replication semantics?

### Consumer ACK

```text
RabbitMQ → consumer
```

Question:

> May the broker consider this delivery successfully processed?

Correct critical consumer shape:

```text
receive
→ validate/dedup
→ durable local effect
→ local Outbox if needed
→ commit
→ ACK
```

ACK-before-commit can create message loss after consumer crash.

---

## 2.15 Transactional Outbox

Outbox solves the local database + broker dual-write gap.

Unsafe:

```text
COMMIT authoritative state
→ process crashes
→ event never published
```

Outbox:

```text
BEGIN
  mutate authoritative state
  insert outgoing message intent
COMMIT
```

Relay:

```text
load pending Outbox
→ publish
→ wait for confirm
→ mark published
```

If the publish succeeds but the relay crashes before marking it, the same message can be republished.

That is why Outbox still requires downstream deduplication.

Microsoft's microservice guidance explicitly presents Outbox as a method to resiliently publish integration events without requiring a cross-resource transaction.

---

## 2.16 Saga and compensating transactions

A ride spans independent authorities:

```text
Trip
Driver
Payment
```

A global database transaction is intentionally absent.

The Saga decomposes the workflow into local transactions.

Example:

```text
Trip created
→ Driver reserved
→ Driver accepted
→ Payment authorized
→ ride starts
→ ride completes
→ Payment captured
```

Before a pivot/point of no return, failure may require compensation:

```text
Payment authorization fails
→ release exact Driver reservation
→ cancel Trip
```

After the physical ride begins/completes, some effects are not semantically reversible:

```text
capture fails after ride completes
```

Correct recovery is forward:

```text
Trip remains COMPLETED
Payment remains pending/requires review
```

not “un-complete the ride.”

Saga compensation is a new business action, not database rollback across services.

---

## 2.17 Orchestration versus choreography

Both styles are valid.

This project deliberately uses a **Trip-owned persisted Saga orchestrator** for the main ride workflow.

Why:

- one place can answer “which step is pending?”;
- failure/restart state is explicit;
- compensation order is easier to reason about;
- defense/evidence is clearer;
- course team size is small.

Events are still used for integration and projections, so the system is not purely synchronous orchestration.

Trade-off:

- Trip becomes workflow coordinator and must avoid absorbing unrelated service ownership;
- Saga persistence/monitoring is required;
- the orchestrator itself must be restart-safe.

---

## 2.18 Derived read models

Redis is intentionally a derived model.

Authority:

```text
Driver PostgreSQL
```

Projection:

```text
Redis GEO + Driver metadata
```

Advantages:

- fast geospatial candidate search;
- independent read optimization;
- easy demonstration of eventual consistency.

Constraint:

```text
Redis may nominate a candidate
Redis may not reserve the Driver
```

This “fast stale read + authoritative write” split is one of the project's core consistency patterns.

---

## 2.19 Observability is not correctness

A trace can be:

- sampled;
- delayed;
- dropped;
- incomplete;
- exporter-dependent.

Therefore:

```text
trace/log ≠ recovery state
```

Recovery uses:

```text
aggregate DB
Saga state
Outbox
dedup records
MatchingRound
RabbitMQ durable state
```

Observability exists to:

- explain;
- debug;
- measure;
- provide evidence.

OpenTelemetry messaging conventions recommend propagating message creation context so producer and consumer traces can be correlated, but this context remains diagnostic rather than authoritative.

---

# 3. Domain framing and bounded contexts

## 3.1 Minimal business story

The P0 product story is deliberately narrow:

```text
1. user authenticates
2. Driver goes AVAILABLE and reports simulated location
3. Rider requests a ride
4. Trip becomes eligible for Matching
5. Matching discovers/ranks candidate Drivers
6. Driver Service authoritatively reserves one Driver
7. Driver accepts or rejects
8. Payment authorization is simulated
9. ride becomes ready/startable
10. ride starts
11. ride completes
12. Payment capture is simulated
13. notifications are delivered asynchronously
```

This is enough domain complexity to produce real distributed-systems problems without expanding into product breadth.

---

## 3.2 Bounded contexts and authority

### Identity Service

Owns:

- user/account;
- role;
- password hash/session/authentication baseline;
- safe profile identity.

Does not own:

- Driver operational state;
- Trip;
- Payment.

### Driver / Vehicle / Location Service

Owns:

- Driver;
- Vehicle;
- authoritative Driver availability;
- current reservation;
- current Trip binding;
- accepted location ingress policy;
- reservation/release/start/complete state.

This is the **exclusive Driver assignment authority**.

### Trip / Booking Service

Owns:

- ride request;
- pickup/dropoff;
- FareSnapshot;
- Trip state machine;
- current assignment reference;
- long-running Saga state;
- cancellation/terminal state.

It does not decide whether a Driver is reservable.

### Matching Service

Owns:

- MatchingRound;
- stages/candidate snapshot;
- MatchingAttempt;
- deterministic candidate policy;
- Redis candidate projection;
- terminal matching result.

It asks Driver Service to reserve.

It never writes Driver authoritative state.

### Payment Simulator Service

Owns:

- Authorize/Capture/Void logical operations;
- operation idempotency;
- deterministic simulated provider outcomes;
- Payment operation state/result.

It does not own FareSnapshot or Trip lifecycle.

### Notification Worker

Owns:

- notification delivery projection;
- delivery attempts/status/dedup.

It does not own business truth.

### Gateway

Owns:

- edge routing;
- authentication forwarding/policy integration;
- rate/body controls where configured;
- edge diagnostics.

It is not a business service and stores no domain aggregate.

---

## 3.3 Why five authoritative services is the sweet spot

The course minimum is four business microservices.

Too few:

```text
Driver + Trip + Matching merged
```

would hide the exact distributed concurrency/authority problem the project wants to study.

Too many:

```text
separate Pricing
separate Fare
separate Location
separate Vehicle
separate Saga Coordinator
separate Read Model Service
```

would produce coordination overhead without adding proportional distributed-systems insight for a 3-person team.

Five authoritative services create enough independent state owners to demonstrate:

- cross-service consistency;
- distributed workflow;
- concurrent allocation;
- async messaging;
- independent failure;
- data ownership;

without turning service decomposition into the assignment itself.

---

## 3.4 Why no separate Pricing Service in P0

The course requires fare calculation, not a mandatory Pricing microservice.

A separate Pricing authority would add:

- another database;
- another network boundary;
- quote/fare consistency problem;
- another failure mode;
- more integration messages;

without being central to the research questions.

P0 uses a deterministic fare component/FareSnapshot under the Trip domain.

A Pricing service becomes justified only if the project studies:

- dynamic pricing;
- independent price deployment;
- marketplace pricing experiments.

Those are not core goals.

---

## 3.5 Why Notification is non-authoritative

Notification is deliberately downstream.

If:

```text
Trip = DRIVER_ACCEPTED
```

but Notification fails:

```text
Trip remains DRIVER_ACCEPTED
```

This makes notification a perfect example of an eventually consistent side effect that must not determine business state.

---

# 4. System architecture

## 4.1 Logical system view

```text
                       ┌─────────────────────┐
                       │ Rider / Driver/Test │
                       │      clients        │
                       └──────────┬──────────┘
                                  │ HTTP
                                  ▼
                       ┌─────────────────────┐
                       │     API Gateway     │
                       │ YARP / edge policy  │
                       └───┬─────┬─────┬─────┘
                           │     │     │
                 ┌─────────┘     │     └─────────┐
                 ▼               ▼               ▼
          ┌────────────┐  ┌────────────┐  ┌────────────┐
          │  Identity  │  │   Driver   │  │    Trip    │
          │  Service   │  │  Service   │  │  Service   │
          └─────┬──────┘  └──┬─────┬───┘  └──┬─────┬───┘
                │            │     │         │     │
                ▼            │     │         │     │
           identity_db       │     │         │     │
                             │     │         │     │
                             │ HTTP Reserve  │     │
                             │◄──────────────┼─────┘
                             │               │
                             │               ▼
                             │       ┌──────────────┐
                             │       │   RabbitMQ   │
                             │       └──────┬───────┘
                             │              │
                 ┌───────────┴────┐    ┌────┴────────┐
                 ▼                │    ▼             ▼
          ┌────────────┐          │ ┌────────────┐ ┌──────────────┐
          │  Matching  │◄─────────┘ │  Payment   │ │ Notification │
          │  Service   │            │  Service   │ │    Worker    │
          └──┬──────┬──┘            └─────┬──────┘ └─────┬────────┘
             │      │                     │              │
             ▼      ▼                     ▼              ▼
        matching_db Redis GEO         payment_db   notification_db
```

All authoritative services also use service-owned PostgreSQL storage.

---

## 4.2 Data ownership topology

Course-scale physical deployment may use one PostgreSQL server/container for reproducibility.

Logical ownership remains strict:

```text
identity_db      → Identity role only
driver_db        → Driver role only
trip_db          → Trip role only
matching_db      → Matching role only
payment_db       → Payment role only
notification_db  → Notification role only
```

No service performs:

```text
SELECT/UPDATE another_service_db.table
```

Other-service state is obtained through:

```text
REST/API
or
integration messages
```

This preserves the educational property of independent authority while acknowledging one shared physical PostgreSQL failure domain.

**Important limitation:**

> Separate databases/roles on one PostgreSQL server prove logical ownership, not database high availability or independent physical failure domains.

---

## 4.3 Why Matching requires durable storage

A purely stateless Matching service seems attractive:

```text
RideRequested
→ query Redis
→ try Drivers
→ publish result
```

But consider:

```text
1. Matching persists nothing
2. Matching calls Driver Reserve(R1)
3. Driver commits R1
4. Matching process crashes before recording success
5. message is redelivered
6. Redis no longer lists D1 as AVAILABLE
7. Matching tries D2
```

Now the same Trip may create conflicting allocation work.

Therefore Matching stores:

```text
MatchingRound
MatchingStage/Snapshot
MatchingAttempt
reservationId
attempt outcome
terminal result
```

before/around remote side effects.

This turns Matching from a transient algorithm into a recoverable distributed workflow component.

---

## 4.4 Conceptual Matching state

```text
MatchingRound
- tripId
- matchingRound
- inputMessageId
- status
- currentStage
- rankingConfigVersion
- candidateSnapshot metadata
- selectedDriverId?
- selectedReservationId?
- resultMessageId?
- timestamps
```

```text
MatchingAttempt
- tripId
- matchingRound
- ordinal
- driverId
- reservationId
- state:
    READY
    IN_FLIGHT
    UNKNOWN
    REJECTED
    RESERVED
- lastResult/error
- timestamps
```

Full invariant semantics belong to Doc 01/02.

---

## 4.5 Location hot path

Location updates are high-frequency telemetry compared with Trip transitions.

The architecture distinguishes:

```text
authoritative Driver state
```

from:

```text
latest location telemetry
```

P0 does not require every GPS sample to become a relational historical record.

Instead:

```text
client/test simulator
→ Driver Location API
→ validate actor/coordinate/freshness
→ accept/publish current sample according to contract
→ Matching consumes/projections into Redis
```

Why?

At only:

```text
500 Drivers
1 sample / 5 seconds
```

the ingest rate is:

```text
500 / 5 = 100 samples/s
```

If every sample were stored forever:

```text
100 × 86,400 = 8,640,000 samples/day
```

before indexes/metadata.

That historical-storage problem is not required for the course.

The baseline keeps the operational path lean and treats Redis as current candidate state.

---

# 5. Communication architecture

## 5.1 Communication rule

Use synchronous communication when the caller needs an immediate authority decision.

Use asynchronous messaging when:

- workflow can continue from durable intent;
- coupling should be reduced;
- retry/restart should be broker-friendly;
- multiple consumers may react;
- immediate response is not required.

---

## 5.2 Communication matrix

| Flow | Style | Why |
|---|---|---|
| Client → Gateway → Identity/Driver/Trip | REST | request/response UX, validation/auth |
| Matching → Driver `ReserveDriver` | synchronous HTTP | candidate iteration needs authority decision |
| Trip → Matching `RideRequested` | async RabbitMQ | recoverable workflow start |
| Driver → integration state events | async RabbitMQ | decoupled projection/workflow reaction |
| Matching → `MatchingFailed` | async RabbitMQ | durable terminal integration result |
| Trip → Payment commands | async RabbitMQ | long-running Saga/retry/restart |
| Payment → Trip outcomes | async RabbitMQ | durable result delivery |
| Trip → Driver release/start/complete commands | async where committed workflow intent benefits from retry | durable cross-service workflow |
| business events → Notification | async RabbitMQ | notification must not block state commit |
| service → OpenTelemetry Collector | telemetry protocol | diagnostics only |

---

## 5.3 Why ReserveDriver is synchronous

Matching candidate iteration is naturally:

```text
candidate D1
→ reserve authority decision
→ success? stop
→ conclusive conflict? try D2
→ unknown? reconcile D1
```

A synchronous call makes the immediate authority decision simple.

Making reservation asynchronous would not eliminate uncertainty.

It would still require:

- `reservationId`;
- persisted attempt state;
- timeout/reconciliation;
- current-round guards.

Therefore the baseline favors a short synchronous authority call plus explicit UNKNOWN handling.

---

## 5.4 Why Payment is asynchronous

Payment participates in a long-running workflow.

The caller does not need to hold an HTTP chain while:

- retries happen;
- service restarts;
- compensation becomes necessary.

Asynchronous command/event flow better demonstrates:

- durable workflow;
- idempotency;
- broker redelivery;
- Saga recovery.

---

## 5.5 Request/reply timeout philosophy

Timeout is a resource/control mechanism.

It is not a truth oracle.

A timeout answers:

> The caller stopped waiting.

It does not answer:

> The remote side definitely did nothing.

The design therefore separates:

```text
transport outcome
from
business outcome
```

This distinction is essential for both Driver reservation and Payment.

---

## 5.6 HTTP resilience policy

Modern .NET provides resilience pipelines including:

- total timeout;
- attempt timeout;
- retry;
- circuit breaker;
- concurrency/rate limiting;
- hedging options.

The project does **not** enable the same policy for every request.

### Reads

A bounded transient retry may be acceptable.

### Side-effecting reserve/payment operations

Retry is allowed only when:

```text
same logical identity
+
same operation semantics
+
reconciliation-aware caller
```

### Hedging

Hedging sends concurrent attempts to multiple endpoints.

For side-effecting operations such as:

```text
ReserveDriver
AuthorizePayment
```

hedging is forbidden in the baseline unless the operation is formally proven safe for duplicate concurrent execution—which is unnecessary here.

---

# 6. Naming, identification and discovery

## 6.1 Service naming

P0 service discovery uses Docker Compose DNS/service names.

Example:

```text
identity
driver
trip
matching
payment
rabbitmq
redis
postgres
otel-collector
```

Service consumers depend on logical DNS names, not container IP addresses.

This satisfies the course's naming/discovery requirement without adding Consul/Kubernetes solely for discovery.

---

## 6.2 Runtime service identity

Each service instance emits:

```text
service.name
service.instance.id
deployment.environment
```

Example:

```text
service.name = matching-service
service.instance.id = matching-2
deployment.environment = perf
```

This is necessary because “Matching service logged X” is insufficient once multiple replicas exist.

---

## 6.3 Business identity catalog

The exact catalog belongs to Docs 01/02, but architecture freezes the roles:

| Identity | Meaning |
|---|---|
| `userId` | account actor |
| `driverId` | Driver aggregate identity |
| `vehicleId` | Vehicle identity |
| `tripId` | one logical ride |
| `matchingRound` | one matching generation for a Trip |
| `reservationId` | one Driver reservation/fencing identity |
| `operationKey` | one logical Payment effect |
| `messageId` | one logical integration message |
| `correlationId` | cross-service business-flow diagnostic correlation |
| `traceId/spanId` | observability execution context |

Never substitute one for another merely because both are UUIDs.

---

# 7. Persistence architecture

## 7.1 PostgreSQL as authoritative local transaction store

PostgreSQL is selected because the project needs:

- ACID local transactions;
- conditional mutation;
- unique constraints;
- relational state machines/workflow tables;
- predictable integration tests;
- mature .NET support.

Current stable PostgreSQL line at the project baseline is 18.x; PostgreSQL 19 is still beta in late September 2026, so the project intentionally stays on 18.x.

---

## 7.2 Local database transaction boundary

Typical authoritative local operation:

```text
BEGIN
  read/lock/check current aggregate version
  apply guarded transition
  write aggregate
  write Outbox message(s)
COMMIT
```

For consumers:

```text
BEGIN
  dedup messageId
  apply local legal effect
  write processed-message marker
  write local Outbox if needed
COMMIT
ACK
```

This is where ACID is most valuable.

---

## 7.3 Database constraint philosophy

Application/domain checks express business semantics.

Database constraints provide last-line concurrency defense.

Examples:

- unique `reservationId`;
- uniqueness preventing one active Trip reservation duplication where schema supports;
- unique Payment `operationKey`;
- unique processed-message identity;
- unique `(tripId, matchingRound)`.

The project does not rely on:

```text
"we checked in C# before INSERT"
```

as sufficient concurrent protection.

---

## 7.4 Redis architecture

Redis is used for:

```text
GEO candidate lookup
latest derived Driver eligibility/status metadata
freshness/version metadata
```

Redis is not:

```text
Driver reservation authority
distributed transaction coordinator
source of Trip state
long-term historical GPS database
```

Redis `GEOSEARCH` provides radius/box geospatial search suitable for candidate discovery.

---

## 7.5 Atomic Redis projection updates

Projection state may include:

```text
driverId
derivedStatus
driverAggregateVersion
lat/lon
capturedAt
sequence/session
```

A dangerous client sequence is:

```text
read old metadata
write new metadata
GEOADD
```

because concurrent/out-of-order updates can expose a half-applied view.

A short Redis Function can atomically:

```text
compare version/freshness
→ update metadata
→ add/remove GEO membership
```

Redis Functions execute atomically, so they must remain short.

Atomicity here protects the **projection's internal coherence**, not Driver business authority.

---

# 8. Matching architecture

## 8.1 Candidate pipeline

Baseline:

```text
RideRequested
→ load/create MatchingRound
→ open stage
→ query Redis GEO
→ filter eligibility/freshness
→ deterministic rank
→ persist bounded candidate/attempt identities
→ ReserveDriver
→ success / conflict / unknown
```

---

## 8.2 Eligibility before ranking

This order is mandatory:

```text
discover
→ eligibility filter
→ rank
```

not:

```text
score everything
→ high score overrides illegal state
```

Eligibility can include:

- derived AVAILABLE state;
- location freshness;
- compatible vehicle/capability;
- not conclusively rejected in same round;
- stage geometry.

Final authority still resides in Driver Service.

---

## 8.3 Deterministic ranking

For the same frozen candidate snapshot/config:

```text
orderedCandidates(input, config)
```

must be reproducible.

Possible score components:

```text
distance
ETA proxy
freshness penalty
bounded idle/fairness component
stable tie-breaker
```

No ML is required.

Why deterministic?

- easier debugging;
- repeatable tests;
- controlled experiments;
- defense can reconstruct why D2 was tried after D1.

---

## 8.4 Serial/conservative attempt baseline

P0 tries reservation candidates serially or with intentionally conservative bounded concurrency.

Reason:

Parallel reservation of several Drivers for one Trip creates a new cleanup problem:

```text
two or more reservations succeed
→ must choose winner
→ compensate losers
```

That is unnecessary complexity for course scale.

The serial baseline provides stronger reasoning ROI.

---

## 8.5 Progressive search

A staged search policy can expand:

```text
S0 → S1 → S2
```

when earlier stages are **conclusively exhausted**.

The key distributed property is not the exact radius.

It is:

```text
UNKNOWN attempt
→ stage MUST NOT advance
```

because the remote reservation may already have committed.

Exact stage policy belongs to Doc 01/02.

---

# 9. Reliability and failure model

## 9.1 Fault model in scope

The project explicitly considers:

### Process failures

- service crash/kill;
- restart;
- consumer crash;
- Outbox relay crash.

### Network failures

- latency;
- timeout;
- connection drop;
- temporary service isolation;
- reconnect.

### Messaging failures

- duplicate delivery;
- redelivery;
- delayed/out-of-order application events;
- poison message;
- publish-confirm uncertainty.

### Data/view failures

- stale Redis projection;
- stale location;
- delayed old Driver state event.

### Infrastructure failure

- one RabbitMQ node lost in 3-node quorum profile.

### Client/request concurrency

- many Trips reserve same Driver;
- GoOffline races ReserveDriver;
- Cancel races reserve.

---

## 9.2 Faults explicitly outside baseline

- Byzantine service sends intentionally malicious internal state;
- arbitrary memory corruption;
- simultaneous multi-region disaster;
- PostgreSQL full HA/failover;
- Redis Cluster failover;
- total broker majority loss recovery guarantees;
- exactly-once network transport;
- globally synchronized clocks.

These become limitations/future work rather than hidden assumptions.

---

## 9.3 Crash-stop recovery rule

For every long-running operation:

> If the process is destroyed and restarted, enough durable state must exist to resume, compensate or expose an explicit terminal/review state.

Bad:

```text
memory contained next step
```

Good:

```text
Saga row
MatchingAttempt
Outbox
dedup record
Payment operation
authoritative aggregate state
```

---

## 9.4 Retry ownership

Retries multiply easily.

Suppose:

```text
broker retries 5x
Saga retries command 3x
HTTP client retries 4x
```

Worst-case calls can approach:

```text
5 × 3 × 4 = 60 attempts
```

for one logical workflow step.

Therefore:

```text
one retry owner per boundary
```

is a project rule.

Retries are:

- bounded;
- classified;
- observable;
- same-identity where side effects are possible.

---

## 9.5 Circuit breaker

Circuit breaker protects:

- caller resources;
- dependency under repeated failure;
- system from retry storms.

It does not answer:

```text
did the previous request commit?
```

Therefore a breaker opening after a side-effect timeout still requires UNKNOWN reconciliation.

---

## 9.6 Poison messages and DLQ

A message that deterministically cannot be processed should not be requeued forever.

Examples:

```text
unsupported schema
invalid immutable contract
permanent decoding failure
```

Policy:

```text
classify
→ finite retry if appropriate
→ DLQ/error store
→ continue healthy work
→ operator/test evidence
```

DLQ is not a garbage bin.

Redrive must preserve logical identity and occur after the root cause is understood.

---

## 9.7 Broker HA claim boundary

Development:

```text
1 RabbitMQ node
```

Final broker reliability profile:

```text
3 RabbitMQ nodes
critical queues = quorum queues
```

RabbitMQ documents quorum queues as replicated queues and recommends publisher confirms/manual ACK for data safety.

The final project may claim:

> We validated the configured RabbitMQ quorum profile under loss of one broker node while a majority remained.

It may **not** claim:

> The entire ride-hailing system is highly available.

PostgreSQL and Redis remain separate P0 failure domains.

---

# 10. Security architecture

Security is not the main research axis but is a mandatory distributed-system baseline.

## 10.1 Authentication

Baseline:

- maintained ASP.NET authentication mechanism;
- password hashes only;
- token/session approach documented;
- no plaintext password.

## 10.2 Authorization

Policies:

```text
Rider
Driver
Admin/test-only where needed
```

Server derives actor identity from authenticated context.

Clients do not submit:

```text
driverId = "someone else"
```

as trusted authority.

---

## 10.3 Service data isolation

Per-service DB roles reduce accidental coupling.

A service should fail if it tries to write another service's database.

This is both:

- architecture enforcement;
- security boundary;
- evidence of service ownership.

---

## 10.4 Input validation

Validate:

- coordinates;
- IDs;
- enum/state inputs;
- payload schema;
- body/message size;
- malicious/invalid data.

Coordinates:

```text
-90 ≤ latitude ≤ 90
-180 ≤ longitude ≤ 180
finite values
```

---

## 10.5 Edge controls

YARP can apply route-level rate-limit policies through ASP.NET Core rate limiting.

Use selectively for:

- login;
- high-abuse endpoints;
- oversized request protection.

Rate limiting is resource protection, not concurrency correctness.

---

## 10.6 Secret policy

Never commit:

- JWT signing secret/private key;
- DB password;
- RabbitMQ admin password;
- external credentials.

Use:

- environment variables;
- local user secrets;
- CI secret store.

`.env.example` contains placeholders only.

---

## 10.7 TLS scope

External/demo endpoints should use TLS where exposed beyond a trusted local environment.

Internal mTLS is not a P0 requirement.

Its absence is documented as a limitation, not silently ignored.

---

# 11. Observability and diagnosability

## 11.1 Why observability is first-class

Distributed debugging without correlation degenerates into:

```text
Trip says sent
Matching says timeout
Driver says reserved
broker says delivered
```

with no shared identity.

The project therefore treats observability as part of the architecture.

---

## 11.2 Baseline stack

```text
ASP.NET / custom spans / metrics
RabbitMQ producer + consumer context
PostgreSQL/Redis instrumentation where useful
        ↓
OpenTelemetry Collector
        ↓
Prometheus + Grafana
trace backend such as Jaeger
```

Use one coherent stack, not multiple overlapping telemetry products.

---

## 11.3 Required resource attributes

Every service:

```text
service.name
service.instance.id
deployment.environment
```

Useful safe business attributes:

```text
tripId
matchingRound
messageId
correlationId
operation type
outcome class
```

Avoid sensitive data in baggage.

---

## 11.4 Messaging trace propagation

OpenTelemetry messaging guidance states that consumer traces cannot be directly correlated to producer traces unless message creation context is propagated.

Therefore:

```text
traceparent/tracestate
```

travel in transport metadata.

Business payload retains business identities.

Do not replace:

```text
messageId
```

with:

```text
traceId
```

---

## 11.5 Minimum useful metrics

### HTTP

```text
request count
duration
error/result class
```

### Driver

```text
reserve success
reserve conflict
reserve unknown
stale release blocked
```

### Matching

```text
round duration
candidate count
stage distribution
attempt count
unknown reconciliation
no-driver result
```

### RabbitMQ/consumer

```text
queue ready/unacked depth
redelivery
retry
DLQ
processing latency
```

### Outbox

```text
pending count
oldest pending age
publish latency
confirm failure
```

### Saga

```text
active by step
compensation count
stuck age
requires-review count
```

### Payment

```text
authorize/capture outcomes
idempotency hits
unknown/reconcile
```

### Recovery

```text
backlog peak
backlog drain time
workflow recovery duration
```

---

# 12. Technology baseline — 2026 SOTA review

The project chooses stable supported technologies that maximize distributed-systems learning and reproducibility rather than novelty.

---

## 12.1 .NET 10 LTS

Baseline:

```text
.NET 10 LTS / current supported 10.0.x patch
ASP.NET Core
```

As of 2026-09-26, Microsoft lists .NET 10 as active LTS, with 10.0.12 released 2026-09-08 and support through November 2028.

Why:

- mature ASP.NET Core;
- strong async/concurrency model;
- first-party resilience/health/rate-limit infrastructure;
- strong Testcontainers ecosystem;
- productive for 3-person semester project.

Exact patch is pinned in executable project files.

---

## 12.2 YARP API Gateway

Use YARP as a lightweight reverse proxy.

Responsibilities:

- route;
- edge auth policy;
- request/body limits;
- optional rate limiting;
- correlation.

Why not a heavier API-management platform?

Because the assignment studies service communication, consistency and failure—not enterprise API product management.

---

## 12.3 PostgreSQL 18.x

As of the baseline date:

- PostgreSQL 18 is stable;
- 18.6 is listed in the current 18 release series;
- PostgreSQL 19 remains beta.

Therefore:

```text
PostgreSQL 18.x
```

is the stable supported family.

Why:

- local ACID;
- strong constraints;
- mature concurrency behavior;
- good inspection/debugability;
- reliable container testing.

---

## 12.4 RabbitMQ 4.3.x

RabbitMQ 4.3.6 was released 2026-09-16.

RabbitMQ 4.3 includes improvements such as quorum-queue delayed retry and consumer timeout capabilities.

Baseline:

```text
RabbitMQ 4.3.x
```

Exact patch pinned in Compose.

Why RabbitMQ over Kafka?

The project needs:

- work queues;
- commands/events;
- acknowledgements;
- redelivery;
- routing;
- quorum validation;
- low operational overhead.

Kafka would add:

- partition/log semantics;
- separate operational model;
- more learning surface;

without closing a missing project requirement.

One broker studied deeply creates higher ROI.

---

## 12.5 Redis 8.10.x

Redis 8.10 is the current selected family.

Use only:

- GEO candidate lookup;
- current derived metadata;
- short atomic projection functions.

Redis 8.10 includes broader platform changes, but the project deliberately uses only the primitives needed by the architecture.

---

## 12.6 OpenTelemetry .NET 1.17.x

OpenTelemetry .NET core 1.17.0 was released in July 2026.

Use:

- traces;
- metrics;
- logs/OTLP as appropriate;
- W3C trace context;
- messaging correlation.

Note:

OpenTelemetry messaging semantic conventions remain an evolving/development area, so the project should pin/record emitted semantic-convention behavior and avoid tying business correctness to telemetry field names.

---

## 12.7 Docker Compose

Compose is the deployment baseline because it provides:

- independent processes;
- isolated networks;
- service DNS;
- reproducible topology;
- versioned infrastructure;
- low operational overhead.

Kubernetes would demonstrate a different class of platform concerns but is unnecessary for P0 correctness.

---

## 12.8 Testcontainers

Use disposable real:

- PostgreSQL;
- RabbitMQ;
- Redis

for integration tests where technology semantics matter.

Do not mock:

- database concurrency;
- broker redelivery;
- Redis atomic projection

and then claim those technologies were proven.

---

## 12.9 k6

Use for controlled API/load experiments.

Prefer arrival-rate executors when the research question is:

> What happens under a fixed externally offered request rate?

Record:

- target rate;
- achieved rate;
- p50/p95/p99;
- errors;
- dropped iterations;
- hardware/config;
- final invariant audit.

---

# 13. Architecture ROI and alternatives

## 13.1 Complexity budget

A distributed project can fail through “architecture tourism”:

```text
microservices
+ Kafka
+ Redis
+ Kubernetes
+ service mesh
+ event sourcing
+ Temporal
+ Elasticsearch
```

without deeply proving any mechanism.

The project instead uses a complexity budget.

Every technology must answer:

1. Which course requirement/problem does it solve?
2. Which invariant/failure window needs it?
3. Can current stack solve it more simply?
4. What test will prove its value?
5. What implementation/ops cost does it add?

---

## 13.2 Why RabbitMQ, not RabbitMQ + Kafka

Rejected.

Two brokers would create:

- two delivery models;
- two operational stacks;
- two retry/ordering stories;
- duplicated observability.

No P0 requirement needs both.

---

## 13.3 Why Outbox polling, not CDC/Debezium

A polling Outbox is sufficient for course throughput.

CDC would add:

- connector platform;
- log-position semantics;
- extra failure/recovery surface.

It is a production option but poor semester ROI.

---

## 13.4 Why no global event sourcing

Event sourcing can provide:

- history;
- replay;
- temporal reconstruction.

But it also changes:

- persistence model;
- schema evolution;
- snapshots;
- projection correctness;
- team mental model.

The course does not require it.

Normal state persistence + Outbox + audit evidence is sufficient.

---

## 13.5 Why no distributed Redis lock

Driver assignment state already lives in PostgreSQL.

Moving correctness to a separate lock system would create:

```text
lock state
+
Driver state
```

that must itself remain consistent.

Conditional mutation at the authority is simpler.

---

## 13.6 Why no Temporal/MassTransit Saga framework baseline

Frameworks can reduce boilerplate.

But the course explicitly evaluates whether students understand:

- distributed transaction;
- retry;
- compensation;
- message processing;
- recovery.

Implementing the core persisted orchestration semantics deliberately provides greater educational value.

A framework can be future work after mechanisms are understood.

---

## 13.7 Why no Kubernetes P0

Compose already demonstrates:

- independent services;
- network communication;
- naming;
- failure/restart;
- scaling selected replicas;
- broker cluster profile.

Kubernetes would add:

- scheduler;
- deployment objects;
- service abstraction;
- readiness/liveness orchestration;

but not improve the central correctness proof enough to justify its cost.

---

## 13.8 Why no real payment

Real payment adds:

- secrets/compliance;
- external availability;
- sandbox-provider behavior;
- legal/product concerns.

A deterministic simulator gives better distributed-systems evidence because failure modes can be forced exactly.

---

## 13.9 Why no ML Matching

ML would require:

- representative training data;
- objective definition;
- model evaluation;
- fairness discussion;
- model serving.

The project's matching research is about:

```text
determinism
concurrency
stale state
recovery
```

A deterministic ranking algorithm is the correct baseline.

---

# 14. Quantitative sizing and engineering calculations

These calculations are **design envelopes**, not production claims.

They help decide where complexity is justified.

---

## 14.1 Driver location rate

Assume:

```text
D = active Drivers
I = location interval seconds
λ_location = D / I
```

Examples:

| Drivers | Interval | Samples/s |
|---:|---:|---:|
| 100 | 5 s | 20 |
| 300 | 5 s | 60 |
| 500 | 5 s | 100 |
| 1,000 | 5 s | 200 |

At 500 active Drivers:

```text
100 samples/s
8.64 million samples/day
```

This calculation supports the decision not to build mandatory relational historical GPS persistence.

---

## 14.2 Candidate-search size

Course experiment target:

```text
50
100
300
500 Drivers
```

Matching need not scan every Driver in application memory.

Redis GEO narrows candidate region first.

If:

```text
C = 20–50 candidates/stage
```

ranking cost remains bounded and easy to reproduce.

---

## 14.3 Reservation contention

Critical correctness load:

```text
2 / 10 / 50 / 100 simultaneous contenders
→ one Driver
```

The expected number of successful reservations remains:

```text
1
```

The number of conflicts approaches:

```text
N - 1
```

for a fixture where every contender targets the same Driver.

Therefore high conflict count in this test is expected, not evidence of failure.

The invariant metric is:

```text
active reservation violations = 0
```

---

## 14.4 Retry multiplication

If unconstrained layers retry:

```text
broker = 5
Saga = 3
HTTP = 4
```

then one logical step can generate up to:

```text
5 × 3 × 4 = 60
```

remote attempts.

This simple calculation motivates:

```text
one retry owner per boundary
```

and bounded policies.

---

## 14.5 Backlog recovery approximation

Let:

```text
λ = incoming messages/s
T = outage seconds
B ≈ λT = backlog
μ = processing service rate after recovery
```

If:

```text
μ > λ
```

approximate backlog drain time:

```text
T_drain ≈ B / (μ - λ)
```

Example:

```text
λ = 20 msg/s
T = 60 s
B ≈ 1,200

μ = 50 msg/s
net drain = 30 msg/s
T_drain ≈ 40 s
```

The project compares measured drain behavior against this simple model.

It does not expect exact equality because:

- startup/reconnect overhead;
- batch/prefetch;
- DB contention;
- retry;
- consumer warm-up

affect real results.

---

## 14.6 Message volume per Trip

A single ride may generate on the order of:

```text
RideRequested
DriverReserved
DriverAccepted
AuthorizePayment
PaymentAuthorized
DriverTripStarted
TripStarted
Complete commands/events
CapturePayment
PaymentCaptured
Notification events
```

Even if a logical ride produces ~10–20 integration messages, course load remains far below the scale that would require a Kafka-scale log platform.

This reinforces the RabbitMQ choice.

---

## 14.7 Service decomposition cost

With `n` services, possible pairwise service relationships grow roughly as:

```text
n(n-1)/2
```

At:

```text
n=5 → 10 possible pairs
n=8 → 28 possible pairs
n=10 → 45 possible pairs
```

Not every pair communicates, but coordination complexity grows quickly.

Therefore reducing unnecessary service count is an architectural control, not lack of sophistication.

---

# 15. Deployment profiles

## 15.1 `core`

```text
Gateway
5 business services
Notification worker
1 PostgreSQL server
1 Redis
1 RabbitMQ node
```

Purpose:

- daily development;
- functional/integration tests.

Claim:

- process distribution;
- messaging semantics;
- local durability.

Not:

- HA.

---

## 15.2 `obs`

Adds:

```text
OpenTelemetry Collector
Prometheus
Grafana
Jaeger or one trace backend
```

Purpose:

- trace/evidence;
- metrics.

---

## 15.3 `fault`

Adds/enables:

```text
testing-only named faultpoints
network fault tooling/scripts
response suppression
process kill/restart controls
```

Purpose:

- deterministic failure reproduction.

---

## 15.4 `perf`

Adds:

```text
load generator
fixed seed datasets
selected service replica counts
raw result export
```

Purpose:

- experiments.

---

## 15.5 `ha`

Uses:

```text
3 RabbitMQ nodes
critical quorum queues
```

Purpose:

- broker node-loss validation.

Does not imply PostgreSQL/Redis HA.

---

# 16. Availability, consistency and degradation decisions

## 16.1 If Redis is down

Allowed:

```text
Matching unavailable/degraded
```

Not allowed:

```text
fallback to random Driver and bypass authority
```

Existing reservations remain correct because Redis was never authority.

---

## 16.2 If Driver Service is unreachable

Matching cannot safely reserve.

Possible state:

```text
attempt pending/unknown
dependency unavailable
```

No fabricated success.

---

## 16.3 If RabbitMQ is temporarily down

Services with Outbox can continue local commits only where business semantics allow, leaving:

```text
pending Outbox
```

for later publication.

Work that requires broker-mediated downstream completion remains pending.

---

## 16.4 If Notification is down

Business workflow continues.

Delivery backlog/error is observable.

---

## 16.5 If Payment is down

Ride workflow may wait/retry/compensate according to Saga state.

It does not silently skip authorization if authorization is required by the current business contract.

---

## 16.6 If PostgreSQL physical host is down

Multiple services may become unavailable because P0 uses one physical PostgreSQL instance.

This is an explicit limitation.

The project does not falsely claim service data ownership creates independent physical DB availability.

---

# 17. Architecture decision register

| ID | Status | Decision | Why / consequence |
|---|---|---|---|
| **DS-A01** | ACCEPTED | five authoritative business services + Notification worker | enough distributed boundaries without service explosion |
| **DS-A02** | ACCEPTED | REST/HTTP synchronous baseline | observable/simple client and authority calls |
| **DS-A03** | ACCEPTED | RabbitMQ as only P0 broker | deep study of one delivery model |
| **DS-A04** | ACCEPTED | PostgreSQL 18.x authoritative local store | ACID/constraints/OCC |
| **DS-A05** | ACCEPTED | separate logical DB + role per stateful component | service data ownership |
| **DS-A06** | ACCEPTED | one physical PostgreSQL server P0 | reproducibility; common failure domain documented |
| **DS-A07** | ACCEPTED | Redis GEO is derived Matching read model | fast lookup without authority ambiguity |
| **DS-A08** | ACCEPTED | Driver conditional reservation/OCC | concurrency resolved at state owner |
| **DS-A09** | ACCEPTED | reservationId fencing | stale compensation cannot corrupt newer state |
| **DS-A10** | ACCEPTED | durable MatchingRound/Attempt | remote-success/local-crash recovery |
| **DS-A11** | ACCEPTED | Trip-owned persisted Saga | explicit long-running workflow state |
| **DS-A12** | ACCEPTED | Transactional Outbox for required state→message coupling | removes DB-commit/publish loss window |
| **DS-A13** | ACCEPTED | at-least-once delivery claim | duplicate delivery expected/tested |
| **DS-A14** | ACCEPTED | manual ACK after durable local effect | crash-safe consumer semantics |
| **DS-A15** | ACCEPTED | publisher confirms for critical publication | publication acceptance evidence |
| **DS-A16** | ACCEPTED | bounded classified retry + DLQ | no infinite retry storm |
| **DS-A17** | ACCEPTED | explicit UNKNOWN remote outcome | timeout cannot be treated as failure |
| **DS-A18** | ACCEPTED | no side-effect hedging | avoid duplicate concurrent writes |
| **DS-A19** | ACCEPTED | Compose DNS P0 discovery | sufficient without extra control plane |
| **DS-A20** | ACCEPTED | Docker Compose P0; no Kubernetes requirement | focus engineering effort on correctness |
| **DS-A21** | ACCEPTED | OpenTelemetry as diagnostic standard | cross-process explainability |
| **DS-A22** | ACCEPTED | deterministic faultpoints + invariant auditor | reproducible proof |
| **DS-A23** | ACCEPTED | 3-node RabbitMQ quorum validation final | replication/failure evidence |
| **DS-A24** | ACCEPTED | deterministic Payment simulator | controllable distributed failure modes |
| **DS-A25** | ACCEPTED | FareSnapshot stays in Trip domain P0 | avoid artificial Pricing service |
| **DS-A26** | ACCEPTED | no per-sample relational GPS history P0 | avoid high-volume nonessential persistence |
| **DS-A27** | ACCEPTED | exact package patch versions live in executable config | architecture docs resist patch drift |
| **DS-A28** | ACCEPTED | P1 locked until Core Gate | protect distributed correctness from scope creep |

---

# 18. Architecture risks

| Risk | Why dangerous | Control |
|---|---|---|
| Redis becomes hidden authority | stale read could violate Driver safety | final reservation only Driver DB |
| timeout interpreted as failure | double reservation/payment | UNKNOWN + same-identity reconcile |
| stale compensation | frees newer reservation | fencing token |
| duplicate delivery | repeats business effect | dedup + unique business key |
| DB/message dual write | committed state loses event | Outbox |
| ACK before commit | message can be lost after crash | commit then ACK |
| infinite retry | storms/starvation | classification + bounded retry/DLQ |
| Matching restart | recomputes around committed remote effect | persisted round/attempt |
| Saga in memory | restart forgets workflow | persisted Saga |
| shared physical PostgreSQL overclaim | false HA claim | explicit limitation |
| too many services | coordination dominates learning | service-count discipline |
| too many frameworks | shallow implementation | architecture gate |
| telemetry as truth | dropped trace breaks recovery | telemetry diagnostic only |
| flaky fault test | unverifiable defense | deterministic faultpoints/barriers |
| benchmark without correctness | fast invalid system | invariant audit after load |
| version drift | docs contradict executable stack | family in docs, patch in repo |

---

# 19. Verification implications already imposed by architecture

Even though Doc 03 owns exact tests, architecture is accepted only if it is testable.

Minimum architecture proof scenarios:

```text
P-01 many Trips contend for one Driver
P-02 GoOffline races ReserveDriver
P-03 stale old Release after newer reservation
P-04 reserve commits but HTTP response is lost
P-05 Matching crashes after remote reserve commit
P-06 consumer commits then crashes before ACK
P-07 state + Outbox commits then producer dies before publish
P-08 publish accepted but local confirm completion is lost → duplicate
P-09 Payment final failure triggers compensation
P-10 Saga restarts while pending
P-11 poison message reaches finite DLQ
P-12 stale Redis state event cannot regress projection
P-13 stale location cannot become newest accepted projection
P-14 network/service isolation reconnects and backlog drains
P-15 one RabbitMQ node lost in quorum profile
```

Every scenario must end with:

```text
final authority-state assertion
```

not merely:

```text
logs look reasonable
```

---

# 20. SOTA/industry cross-check

## 20.1 Production systems are more complex—but reinforce the same fundamentals

Uber has publicly described a Fulfillment Platform that acts as a source of truth for trip and Driver/delivery-session state. In its rearchitecture discussion, Uber highlights consistency problems, concurrent writes, multi-entity workflows, statecharts and transaction coordination.

The project does **not** claim to reproduce Uber's architecture or scale.

The industry relevance is narrower:

- fulfillment state needs explicit authority;
- state-machine modeling improves correctness;
- multi-entity workflows require coordination;
- best-effort consistency can create hard-to-debug mismatches.

That supports the project's choice to prioritize state authority, guarded transitions and persisted workflow over feature breadth.

---

## 20.2 Modern microservice guidance aligns with the architecture

Microsoft's current microservice architecture guidance emphasizes:

- private data ownership per service;
- eventual consistency for cross-service workflows;
- Saga for distributed consistency;
- Outbox/idempotency for asynchronous integration.

The project adopts those patterns but implements only the subset necessary to make the mechanisms inspectable and defensible in a course setting.

---

## 20.3 Modern RabbitMQ capability does not remove application semantics

RabbitMQ 4.3 offers richer quorum/retry capabilities.

That does not eliminate the need for:

- business idempotency;
- stable operation IDs;
- state guards;
- Saga state;
- application-level poison classification.

Infrastructure can deliver messages.

Only the application can decide whether applying a message is currently legal.

---

# 21. Report-ready mapping

The final 20–30 page report can derive major sections directly from the documentation set.

| Report section | Primary source |
|---|---|
| Problem/requirements | 00 §0–1 |
| Distributed-system issues | 00 §2, §9 |
| Analysis/solution selection | 00 §12–17 |
| System architecture | 00 §3–8 |
| Component/protocol design | 00 + 02 |
| Naming/discovery | 00 §6 |
| Concurrency/consistency | 00 §2, 01 |
| Fault tolerance/recovery | 00 §9, 02 |
| Security | 00 §10 |
| Implementation | 02 + executable repo |
| Testing/results | 03 |
| Performance | 00 §14 + 03 |
| Limitations/future work | 00 §0, §9, §16 |
| Team contributions | 04/05 |

This separation prevents the final report from becoming a chronological implementation diary.

---

# 22. Documentation governance

## 22.1 Canonical hierarchy

```text
COURSE SPECIFICATION
        ↓
00 Scope / architecture / technology / decisions
        ↓
01 Domain / states / invariants
        ↓
02 Distributed execution / messaging / recovery
        ↓
03 Verification / experiments / evidence
        ↓
04 Team execution
        ↓
05A / 05B / 05C individual execution
```

Lower documents cannot silently override higher contracts.

---

## 22.2 What belongs where

### 00

```text
WHAT system exists?
WHY these service/technology boundaries?
WHAT is P0?
WHAT failure model?
WHAT architecture trade-offs?
```

### 01

```text
WHAT must never be wrong?
WHAT states/transitions are legal?
WHAT identities fence stale work?
```

### 02

```text
HOW do network/message/retry/Outbox/Saga mechanisms execute?
```

### 03

```text
HOW do we prove the claims?
```

### 04/05

```text
WHO does WHAT and WHEN?
```

---

# 23. External references / research basis

These sources are validation/reference material. They do not override the project's own contracts.

## Course source

- `COURSE PROJECT SPECIFICATION.pdf` — official Distributed Systems project specification supplied for the course.

## Microsoft / .NET / microservices

1. **.NET Support Policy**  
   https://dotnet.microsoft.com/en-us/platform/support/policy  
   Confirms .NET 10 LTS status/current support line.

2. **Data sovereignty per microservice**  
   https://learn.microsoft.com/en-us/dotnet/architecture/microservices/architect-microservice-container-applications/data-sovereignty-per-microservice  
   Supports private service data ownership and eventual consistency across microservices.

3. **Cloud-native distributed data patterns**  
   https://learn.microsoft.com/en-us/dotnet/architecture/cloud-native/distributed-data  
   Discusses database-per-service and Saga.

4. **Saga pattern**  
   https://learn.microsoft.com/en-us/azure/architecture/patterns/saga  
   Covers local transactions, compensation, pivot and recovery considerations.

5. **Asynchronous message-based communication**  
   https://learn.microsoft.com/en-us/dotnet/architecture/microservices/architect-microservice-container-applications/asynchronous-message-based-communication  
   Discusses idempotence/dedup and Outbox-style resilient publication.

6. **Transactional Outbox pattern**  
   https://learn.microsoft.com/en-us/azure/architecture/databases/guide/transactional-out-box-cosmos  
   Technology differs, but the local-state + outgoing-intent atomicity principle applies.

7. **.NET HTTP resilience**  
   https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.http.resilience.httpstandardresilienceoptions?view=net-10.0-pp  
   Documents timeout/retry/circuit-breaker/concurrency strategies.

8. **YARP rate limiting**  
   https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/yarp/rate-limiting?view=aspnetcore-10.0

## RabbitMQ

9. **Consumer Acknowledgements and Publisher Confirms**  
   https://www.rabbitmq.com/docs/confirms

10. **RabbitMQ 4.3 release**  
    https://www.rabbitmq.com/blog/2026/04/23/rabbitmq-4.3-release

11. **RabbitMQ release information**  
    https://www.rabbitmq.com/release-information

12. **Quorum Queues**  
    https://www.rabbitmq.com/docs/quorum-queues

## PostgreSQL

13. **PostgreSQL release archive**  
    https://www.postgresql.org/docs/release/

14. **PostgreSQL 18 documentation**  
    https://www.postgresql.org/docs/18/

## Redis

15. **Redis GEOSEARCH**  
    https://redis.io/docs/latest/commands/geosearch/

16. **Redis Functions**  
    https://redis.io/docs/latest/develop/programmability/functions-intro/

17. **Redis Open Source 8.10 release notes**  
    https://redis.io/docs/latest/operate/oss_and_stack/stack-with-enterprise/release-notes/redisce/redisos-8.10-release-notes/

## OpenTelemetry

18. **Messaging semantic conventions**  
    https://opentelemetry.io/docs/specs/semconv/messaging/

19. **Messaging span/context propagation**  
    https://opentelemetry.io/docs/specs/semconv/messaging/messaging-spans/

20. **OpenTelemetry .NET releases**  
    https://github.com/open-telemetry/opentelemetry-dotnet/releases

## Industry reference

21. **Uber Fulfillment Platform rearchitecture**  
    https://www.uber.com/us/en/blog/fulfillment-platform-rearchitecture/

The Uber source is used only as an industry architecture reference, not as a claim that this project matches Uber's topology, scale or algorithm.

---

# 24. Architecture freeze checklist

Before promoting this file to **ACCEPTED/FROZEN**:

### Course alignment

- [ ] at least 4 business microservices are independently deployable;
- [ ] every stateful service has owned data/schema/role;
- [ ] no direct cross-service DB access;
- [ ] at least one synchronous flow;
- [ ] at least one RabbitMQ asynchronous flow;
- [ ] idempotency/event identity defined;
- [ ] retry defined;
- [ ] dedup defined;
- [ ] DLQ/error visibility defined;
- [ ] correlation identity defined;
- [ ] Saga/compensation defined;
- [ ] concurrency demo architecture possible;
- [ ] stop/restart workflow architecture possible.

### Authority

- [ ] Driver Service is sole Driver reservation authority;
- [ ] Trip is sole Trip lifecycle authority;
- [ ] Payment is sole Payment operation authority;
- [ ] Matching owns matching workflow, not Driver state;
- [ ] Redis is explicitly derived;
- [ ] Notification is explicitly non-authoritative.

### Distributed correctness

- [ ] timeout can become UNKNOWN;
- [ ] stable identities exist for side-effect retry;
- [ ] stale cleanup has fencing;
- [ ] required state→message coupling has Outbox;
- [ ] critical consumers ACK after durable effect;
- [ ] duplicate delivery is expected and handled;
- [ ] persisted Saga survives restart;
- [ ] Matching persists unresolved attempts;
- [ ] no global exactly-once claim.

### Technology

- [ ] .NET family supported;
- [ ] PostgreSQL stable family selected;
- [ ] RabbitMQ current supported 4.3.x line selected;
- [ ] Redis 8.10.x family selected;
- [ ] exact patches pinned in executable config;
- [ ] no preview technology is required for P0;
- [ ] observability stack is singular/coherent.

### Scope

- [ ] Pricing remains in-process unless a real requirement justifies service split;
- [ ] no Kafka/Kubernetes/ML/service-mesh scope creep;
- [ ] P1 closed until Core Gate;
- [ ] every P0 technology buys a named proof.

### Claims

- [ ] one-node core is not called HA;
- [ ] RabbitMQ quorum test is not called full-system HA;
- [ ] course-scale measurements are not called production scale;
- [ ] eventual consistency does not excuse safety-invariant violations;
- [ ] telemetry is not called recovery state.

---

# 25. North star

> **The architecture is successful when a reviewer can intentionally create a race, stale read, duplicate delivery, lost response, process crash or broker interruption—and the team can point to one explicit authority, one stable logical identity, one durable recovery mechanism and one final-state assertion that explains why the system remains legal.**

> **The goal is not the largest architecture. The goal is the smallest architecture deep enough to make distributed-systems correctness visible, breakable, recoverable and provable.**
