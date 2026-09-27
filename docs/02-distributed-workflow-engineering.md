# DS03 DISTRIBUTED RIDE-HAILING SYSTEM
## Distributed Workflow Engineering · Messaging · Reliability · Recovery Contract

> **Document:** `docs/02-distributed-workflow-engineering.md`  
> **Course:** Distributed Systems  
> **Topic:** DS03 — Ride-hailing Microservices System simulating Grab/Uber  
> **Type:** Distributed Execution Contract + Reliability Engineering Specification + Messaging/Recovery Design  
> **Status:** **PROPOSED FREEZE v2.0 — REVIEW CANDIDATE**  
> **Baseline date:** **2026-09-26**  
> **Parents:** `00-project-scope-architecture.md`, `01-domain-correctness-contract.md`  
> **Children:** `03-verification-experiments-demo.md`, `04-team-execution-plan.md`, `05A/05B/05C`  
> **Normative role:** source of truth for synchronous/asynchronous boundaries, service discovery, message envelope/contracts, RabbitMQ topology and delivery semantics, publisher confirms, consumer acknowledgements, idempotent consumption, Transactional Outbox, MatchingRound durability, Saga persistence, retry ownership, DLQ/error handling, UNKNOWN-outcome reconciliation, Redis projection updates, network recovery, observability propagation, health/readiness semantics and distributed fault windows.  
> **Not source of truth for:** domain legality/invariants (`01`), exact executable test IDs/repetition/evidence (`03`), team schedule (`04`), individual work breakdown (`05`).  
> **Executable source of truth:** exact package versions, migrations, queue arguments, exchange/queue declarations, retry numbers, timeouts, connection settings and schemas are pinned in source/config after freeze. If executable configuration changes a semantic rule in this file, this document must be updated in the same architecture change.  
> **Change policy:** changing delivery guarantees, message ownership, command/event meaning, ACK timing, Outbox semantics, Saga persistence, retry owner, UNKNOWN handling, Redis authority, queue type for critical flow, or cross-service synchronous/asynchronous boundary requires architecture review.

> **Reliability thesis.**  
> **No distributed call is assumed to occur exactly once, in global order, or atomically with another service. Correctness is obtained from local ACID boundaries, durable intent, stable logical identity, explicit uncertainty, idempotent processing, guarded domain transitions and eventual recovery.**

---

# 0. Executive distributed-execution contract

## 0.1 The one-sentence rule

> **Persist enough intent before crossing a dangerous network boundary, assume transport can repeat or disappear from the caller's point of view, apply effects only through the current authoritative state, and ACK/declare completion only after the durable effect that the acknowledgement is supposed to represent.**

---

## 0.2 Global semantics baseline

```text
Cross-service transaction model:
    Saga / local transactions / compensation
    NOT global ACID

Transport delivery assumption:
    at-least-once where durable RabbitMQ delivery is used
    NOT exactly-once transport

Global message ordering:
    not assumed

Synchronous timeout:
    may imply UNKNOWN remote outcome

Local aggregate consistency:
    strong/local ACID

Cross-service consistency:
    eventual

Redis:
    derived read model only

Required outgoing message after authoritative state:
    Transactional Outbox

Critical consumer:
    local commit before RabbitMQ ACK

Critical producer:
    durable Outbox intent + publisher confirm

Long-running workflow:
    persisted Saga / persisted MatchingRound

Retry:
    bounded, classified, one owner per boundary

Unrecoverable/unsafe-to-continue state:
    explicit DLQ / FAILED / REQUIRES_REVIEW
    never silent guessing
```

---

## 0.3 Mechanisms and the problem each one solves

| Mechanism | Problem solved | Main correctness contracts |
|---|---|---|
| Driver conditional mutation/OCC | concurrent reservations | DSI-001/002/003 |
| `reservationId` | duplicate reserve, stale release, UNKNOWN reconciliation | DSI-004/005/018/031/033 |
| durable MatchingRound/Attempt | remote-success/local-crash, redelivery | DSI-015/017/018/035; DSL-003 |
| local FSM/version guards | stale/out-of-order input | DSI-003/010/016 |
| Payment `operationKey` | duplicate/unknown payment effect | DSI-024/025/034 |
| Transactional Outbox | DB commit + publish dual-write gap | DSL-006 |
| publisher confirms | broker acceptance uncertainty | DSL-006 support |
| manual ACK after commit | consumer crash window | idempotent effect / DSL-005/006 |
| processed-message dedup | redelivery/duplicate publish | DSI-024/034 genericized |
| bounded retry | transient failure | DSL-* |
| DLQ/error store | poison/permanent failure | DSL-005/006 |
| persisted Saga | cross-service restart/compensation | DSL-002/004/005 |
| Redis version/freshness guard | stale projection regression | DSI-006/007 |
| circuit breaker | resource protection under failing dependency | availability/resource safety |
| trace/correlation propagation | causal reconstruction | explainability, not correctness |
| deterministic faultpoints | reproducible crash windows | verification support |

---

# 1. Distributed execution model

## 1.1 Why a network call is not a local function call

A local call usually gives the caller a strong observation:

```text
returned value
or
threw before returning
```

Across a network, the caller sees only its local observation.

For a side-effecting request:

```text
Caller                      Network                    Callee
  |                            |                         |
  |------ request ------------>|------------------------>|
  |                            |                     may commit
  |                            |                         |
  |<----- response ------------|<------------------------|
```

Failures can occur at many points.

The caller may observe:

```text
timeout
connection reset
socket exception
circuit breaker rejection
process crash
```

without learning whether the callee committed.

Therefore:

```text
transport failure observation
!=
business outcome
```

This distinction drives the entire reliability architecture.

---

## 1.2 Four important completion notions

A distributed operation can have several different “completion” moments.

### Request sent

The caller serialized/wrote bytes.

This does not prove remote receipt.

### Remote accepted

The remote server/broker accepted the request/message.

This does not necessarily prove business commit.

### Business effect committed

Authoritative database transaction committed.

This does not prove caller observed the result.

### Caller observed success

Caller received a success response/confirm/event.

These moments must not be conflated.

---

## 1.3 Failure windows as first-class design objects

For every cross-service mechanism, design review must identify:

```text
W1 before remote request
W2 after request sent, before remote commit
W3 after remote commit, before response
W4 after response, before caller local commit
W5 after caller local commit, before next outgoing intent
```

The implementation is accepted only if each dangerous window has:

- durable identity;
- legal retry/reconcile behavior;
- final-state proof.

---

## 1.4 No global transaction by call-stack nesting

Forbidden reasoning:

```text
BEGIN Trip DB transaction
→ call Driver Service
→ call Payment Service
→ COMMIT Trip DB
```

This is not a cross-service transaction.

It creates:

- long locks;
- partial commit ambiguity;
- network-dependent DB transaction lifetime;
- no atomic rollback of remote services.

Rule:

> **Do not hold an authoritative local DB transaction open across a remote service call.**

Use:

```text
local transaction
→ durable intent/state
→ network action
→ later local transaction
```

---

# 2. Communication boundary

## 2.1 Hybrid communication model

The project intentionally uses both synchronous and asynchronous communication.

It does **not** make every interaction synchronous.

It does **not** make every interaction event-driven.

Selection principle:

### Use synchronous HTTP when

- caller needs an immediate authority decision;
- operation is short;
- caller's algorithm depends on the direct result;
- UNKNOWN behavior is explicitly modeled.

### Use asynchronous RabbitMQ when

- workflow can wait;
- durable retry/restart is valuable;
- producer should not block on downstream workflow completion;
- integration side effect should be decoupled;
- one producer event may have multiple downstream consumers.

---

## 2.2 P0 communication matrix

| Interaction | Mode | Why |
|---|---|---|
| Client → Gateway → Identity | REST/HTTP | interactive authentication |
| Client → Driver | REST/HTTP | GoOnline/GoOffline/current state/location |
| Client → Trip | REST/HTTP | Create/Cancel/Accept/Start/Complete commands |
| Trip → Matching `RideRequestedV1` | RabbitMQ | durable/replayable matching workflow start |
| Trip → Matching `CancelMatchingRoundV1` | RabbitMQ | cancellation intent must survive process failure |
| Matching → Driver `ReserveDriver` | synchronous HTTP | serial candidate algorithm needs current authority decision |
| Driver → integration state events | RabbitMQ | authoritative state propagation/projection |
| Matching → Trip `MatchingFailedV1` | RabbitMQ | terminal result replay/restart safe |
| Trip Saga → Payment commands | RabbitMQ | long-running durable workflow |
| Payment → Trip outcomes | RabbitMQ | durable result |
| Trip Saga → Driver release/start/complete command | RabbitMQ | recoverable cross-service workflow |
| business events → Notification | RabbitMQ | delivery must not block authority commit |
| services → OTel Collector | telemetry protocol | diagnostics only |

---

## 2.3 Why `ReserveDriver` remains synchronous

Matching executes a sequential decision:

```text
D1
→ conclusive success? stop
→ conclusive conflict? D2
→ UNKNOWN? reconcile D1
```

A short synchronous authority call gives:

- explicit candidate result;
- simple serial algorithm;
- direct conflict path.

Making reserve asynchronous would still require:

- persisted attempt;
- stable `reservationId`;
- timeout/deadline;
- reconciliation;
- stale-result guards.

It would increase message/state complexity without removing the fundamental problem.

---

## 2.4 Why Payment is asynchronous

Payment is a durable Saga step.

The workflow benefits from:

- broker redelivery;
- process restart;
- idempotent `operationKey`;
- delayed retry;
- compensation;
- no user HTTP connection held open.

Therefore:

```text
Trip commits Saga state + Outbox(Payment command)
→ returns/continues asynchronously
→ Payment handles
→ outcome event eventually drives Trip
```

---

# 3. Service naming, discovery and process identity

## 3.1 Docker Compose service DNS

P0 service discovery uses Compose networking.

On the Compose project network, services are discoverable by service name.

Examples:

```text
http://driver:8080
http://trip:8080
rabbitmq:5672
redis:6379
postgres:5432
```

Never pin container IPs.

Containers may be recreated with different IP addresses.

---

## 3.2 Stable service identity vs instance identity

Observability uses both:

```text
service.name
service.instance.id
deployment.environment
```

Examples:

```text
service.name = matching-service
service.instance.id = matching-service-2
deployment.environment = fault
```

`service.name` identifies logical service.

`service.instance.id` identifies one running process/container.

---

## 3.3 Client-provided RabbitMQ connection names

Where practical, connections should expose useful client names:

```text
trip-service/outbox-publisher
matching-service/ride-consumer
payment-service/payment-consumer
```

This improves broker inspection without becoming business identity.

---

# 4. Health, readiness and startup semantics

## 4.1 Liveness

Liveness answers:

> Is this process alive enough that restarting it may be appropriate if it fails?

It should not automatically fail because one dependency is temporarily unavailable if the process can recover.

Example endpoint:

```text
/health/live
```

---

## 4.2 Readiness

Readiness answers:

> Is the process ready to accept its intended traffic/work now?

Possible readiness dependencies:

- migrations completed;
- required configuration loaded;
- broker consumer topology ready;
- critical startup state initialized.

Example:

```text
/health/ready
```

ASP.NET Core explicitly distinguishes readiness from liveness.

---

## 4.3 Health checks are not recovery logic

Do not implement:

```text
RabbitMQ unavailable
→ health endpoint unhealthy
→ assume workflow fixed by restart
```

A restart may not solve:

- broker outage;
- stale state;
- unknown remote effect.

Health probes detect/route.

Durable workflow/retry mechanisms recover business work.

---

# 5. Canonical message model

## 5.1 Message categories

### Command

An instruction directed at one logical capability.

Examples:

```text
AuthorizePaymentV1
ReleaseDriverReservationV1
CancelMatchingRoundV1
```

A command expresses intent:

> Please attempt this action.

### Event

A statement about an already committed authoritative fact.

Examples:

```text
DriverReservedV1
PaymentAuthorizedV1
MatchingFailedV1
```

An event expresses:

> This fact committed at the owning authority.

Do not use event names for uncommitted intent.

---

## 5.2 Canonical envelope

Conceptual envelope:

```json
{
  "messageId": "01K...",
  "messageType": "payment.authorization.requested.v1",
  "schemaVersion": 1,
  "occurredAt": "2026-09-26T14:12:33.481Z",

  "correlationId": "ride-flow-...",
  "causationId": "01K-parent-message",

  "aggregateType": "Trip",
  "aggregateId": "trip-123",
  "aggregateVersion": 7,

  "sagaId": "saga-trip-123",
  "operationKey": "trip-123:authorize:v1",

  "payload": {}
}
```

Not every field is mandatory for every message.

The semantic rules are mandatory.

---

## 5.3 Identity roles

### `messageId`

Identifies one logical integration message.

If the same Outbox row is republished:

```text
same messageId
```

### `operationKey`

Identifies one logical business side effect.

Example:

```text
trip-123:authorize:v1
```

If the Payment command is redelivered in another message:

```text
operationKey stays the same
```

### `causationId`

Identifies the message/command that caused the current message.

Useful for causal chain analysis.

### `correlationId`

Groups a long-running business flow.

It is diagnostic.

It is not an idempotency key.

### `aggregateVersion`

Helps detect stale state events.

Consumer must still check local current state.

---

## 5.4 Trace context belongs in transport metadata

Use W3C Trace Context:

```text
traceparent
tracestate
```

in HTTP headers and RabbitMQ message headers.

Do not replace business IDs with trace IDs.

Trace identity can be sampled or re-originated.

Business identity must remain durable.

---

# 6. Versioned integration-contract catalog

This catalog freezes **semantic names and ownership**.

Exact CLR namespace/JSON schema location is executable source.

| Kind | Contract | Routing key | Producer authority |
|---|---|---|---|
| Event | `RideRequestedV1` | `trip.ride-requested.v1` | Trip Outbox |
| Command | `CancelMatchingRoundV1` | `matching.cancel-round.v1` | Trip Outbox |
| Event | `MatchingFailedV1` | `matching.failed.v1` | Matching Outbox |
| Event | `DriverAvailableV1` | `driver.available.v1` | Driver Outbox |
| Event | `DriverOfflineV1` | `driver.offline.v1` | Driver Outbox |
| Event | `DriverReservedV1` | `driver.reserved.v1` | Driver Outbox |
| Event | `DriverReservationReleasedV1` | `driver.reservation-released.v1` | Driver Outbox |
| Command | `ReleaseDriverReservationV1` | `driver.release-reservation.v1` | Trip Outbox/Saga |
| Command | `MarkDriverTripStartedV1` | `driver.mark-trip-started.v1` | Trip Outbox/Saga |
| Event | `DriverTripStartedV1` | `driver.trip-started.v1` | Driver Outbox |
| Command | `MarkDriverTripCompletedV1` | `driver.mark-trip-completed.v1` | Trip Outbox/Saga |
| Event | `DriverTripCompletedV1` | `driver.trip-completed.v1` | Driver Outbox |
| Command | `AuthorizePaymentV1` | `payment.authorize.v1` | Trip Outbox/Saga |
| Event | `PaymentAuthorizedV1` | `payment.authorized.v1` | Payment Outbox |
| Event | `PaymentAuthorizationFailedV1` | `payment.authorization-failed.v1` | Payment Outbox |
| Command | `VoidPaymentAuthorizationV1` | `payment.void-authorization.v1` | Trip Outbox/Saga |
| Event | `PaymentAuthorizationVoidedV1` | `payment.authorization-voided.v1` | Payment Outbox |
| Command | `CapturePaymentV1` | `payment.capture.v1` | Trip Outbox/Saga |
| Event | `PaymentCapturedV1` | `payment.captured.v1` | Payment Outbox |
| Event | `PaymentCapturePendingV1` | `payment.capture-pending.v1` | Payment Outbox |
| Event | `PaymentRequiresReviewV1` | `payment.requires-review.v1` | Payment Outbox |

Location projection event:

| Kind | Contract | Routing key | Producer |
|---|---|---|---|
| Event | `DriverLocationAcceptedV1` | `driver.location-accepted.v1` | Driver integration publisher |

`DriverLocationAcceptedV1` may use an explicitly documented publication path because P0 does not require a relational business-state + event atomic dual-write for every GPS sample. If implementation couples an authoritative relational state mutation to this event, it must use Outbox.

---

## 6.1 Producer-ownership rule

A service can only publish an authoritative event about a fact it owns.

Example:

```text
DriverReservedV1
```

must come from Driver Service.

Matching may not publish:

```text
DriverReservedV1
```

because Matching does not own Driver reservation truth.

Matching owns:

```text
MatchingFailedV1
MatchingRound internal progress
```

---

## 6.2 Schema evolution

Version in contract name/routing key when semantics are incompatible.

Examples:

```text
DriverReservedV1
DriverReservedV2
```

Rules:

- additive optional fields may remain V1 if semantics remain compatible;
- removing/renaming required field or changing meaning requires version review;
- consumer should explicitly reject unsupported versions into bounded error/DLQ path;
- do not silently parse “close enough.”

---

# 7. RabbitMQ topology

## 7.1 Topology goals

Topology should provide:

- semantic routing;
- independent consumer queues;
- durable critical queues;
- bounded retry;
- DLQ visibility;
- reproducible declaration;
- 3-node quorum validation profile.

Topology should not become a maze.

---

## 7.2 Exchange baseline

Use a small number of semantic topic exchanges.

Example:

```text
ride.commands
ride.events
```

Optionally:

```text
ride.deadletter
```

Exact exchange names may be adjusted in executable config, but routing ownership remains.

---

## 7.3 Queue-per-consumer-capability

Events are not consumed from one shared “event queue.”

Example:

```text
matching.ride-requested.q
trip.driver-state.q
trip.payment-outcome.q
notification.business-events.q
matching.driver-projection.q
```

Why?

If two different services share one queue:

```text
only one receives each message
```

which is work-queue semantics, not event fan-out.

Each logical event consumer gets its own queue/binding.

---

## 7.4 Critical queue type

Final reliability profile:

```text
critical business queues → quorum queues
```

RabbitMQ 4.3 quorum queues:

- are replicated;
- use Raft;
- support publisher confirms tied to quorum acceptance;
- recommend manual consumer ACK for data safety;
- support poison/delayed-retry capabilities.

Development may use a simpler one-node profile while preserving queue semantics as closely as practical.

---

## 7.5 Which queues should not become quorum by default

Not every ephemeral/debug queue needs quorum.

Avoid quorum queue for:

- temporary exclusive diagnostics;
- RPC reply queues;
- high-churn temporary queues.

Use quorum where losing durable business messages matters.

---

## 7.6 Durability

Critical queue declarations:

```text
durable
non-exclusive
non-auto-delete
```

Critical messages:

```text
persistent
```

Persistence alone still does not tell publisher whether broker safely accepted the message.

That requires confirms.

---

# 8. Publisher confirms

## 8.1 What confirm means

RabbitMQ publisher confirms cover:

```text
publisher ↔ broker
```

For persistent messages to quorum queues, confirm is issued after quorum replicas have accepted the message according to RabbitMQ's queue semantics.

It does **not** mean:

- consumer received;
- consumer processed;
- consumer committed business effect.

---

## 8.2 Critical publication sequence

```text
Outbox row pending
→ publish same messageId
→ await/track publisher confirm
→ mark Outbox published
```

---

## 8.3 Dangerous confirm window

```text
broker accepts M
→ confirm/connection result lost
→ relay does not know
```

Relay may publish M again.

Required:

```text
same messageId
```

Consumer dedup makes duplicate harmless.

---

## 8.4 Unroutable messages

Where critical, publisher can use mandatory-routing detection.

If message has no route:

```text
BasicReturn / equivalent
```

must be visible as publication failure/configuration error.

A positive confirm alone for an unroutable publication does not necessarily represent intended consumer delivery.

---

## 8.5 Publish batching and throughput

Do not serialize the entire system by:

```text
publish one message
block entire worker until confirm
```

A relay can track batches/outstanding confirms, but semantic rule remains:

```text
row is not PUBLISHED until its publication is conclusively accepted
```

Course implementation may start simple and optimize only from measurement.

---

# 9. Consumer acknowledgements

## 9.1 Critical consumer pattern

```text
receive M
→ deserialize + validate
→ begin local transaction
→ dedup / current-state guard
→ apply local effect
→ persist processed marker
→ persist resulting Outbox if required
→ COMMIT
→ ACK M
```

ACK means:

> Local durable processing for this delivery completed according to consumer contract.

---

## 9.2 Why ACK-before-commit is unsafe

Sequence:

```text
receive
→ ACK
→ process crashes
→ DB never committed
```

Broker believes message complete.

Business effect is lost.

---

## 9.3 Why commit-before-ACK creates duplicates

Sequence:

```text
receive
→ DB COMMIT
→ process crashes
→ ACK missing
```

Broker redelivers.

This is expected and safe **only if** the consumer is idempotent/deduplicated.

---

## 9.4 `redelivered` flag is a hint, not an idempotency key

RabbitMQ may mark a delivery as redelivered.

The application must not rely on:

```text
redelivered=false
```

to decide whether its own business effect exists.

Use:

```text
messageId
business idempotency key
current state
```

as authoritative logic.

---

# 10. Processed-message deduplication

## 10.1 Durable dedup record

Each critical consumer owns a local record conceptually:

```text
processed_messages
- consumer_scope
- message_id
- processed_at
- handler_version? optional
```

Constraint:

```text
UNIQUE(consumer_scope, message_id)
```

---

## 10.2 Same transaction as business effect

Correct:

```text
BEGIN
  INSERT processed_message
  apply business effect
  INSERT resulting Outbox
COMMIT
ACK
```

or equivalent ordering with a unique insert/check, as long as processed marker and business effect become atomic.

Incorrect:

```text
INSERT dedup marker
COMMIT
then business update
```

Crash could permanently suppress the real effect.

---

## 10.3 Dedup is not enough

A late event can have a different `messageId`.

Example:

```text
DriverAvailableV1(v8)  message M8
DriverReservedV1(v9)   message M9
late DriverAvailableV1(v8) M8 was never seen before
```

Dedup will not reject M8.

State/version guard must.

Therefore:

```text
dedup
+
domain FSM/version/round guard
```

are both required.

---

# 11. Transactional Outbox

## 11.1 The dual-write problem

Unsafe:

```text
BEGIN
  Trip becomes CANCELLED
COMMIT

publish ReleaseDriver
```

Crash after commit:

```text
Trip CANCELLED
Driver remains RESERVED forever
```

if no durable release intent exists.

---

## 11.2 Outbox local transaction

Correct:

```text
BEGIN
  Trip becomes CANCELLED
  Saga step updated
  Outbox(ReleaseDriverReservationV1)
COMMIT
```

If process dies:

```text
pending Outbox remains
```

Relay publishes after restart.

---

## 11.3 Outbox schema

Conceptual:

```text
outbox_messages
- message_id
- aggregate_type
- aggregate_id
- aggregate_version
- message_type
- schema_version
- payload_json
- headers_json / correlation metadata
- occurred_at

- status                // PENDING | PUBLISHED | FAILED_VISIBLE
- attempt_count
- next_attempt_at
- published_at?
- last_error_code?
- last_error_at?
```

`messageId` is generated once with Outbox row.

---

## 11.4 Relay claim strategy

If multiple Outbox relay instances run:

```text
they must not independently create different message identities
```

Possible PostgreSQL strategy:

```sql
SELECT ...
FOR UPDATE SKIP LOCKED
LIMIT :batch;
```

inside a claim/update transaction.

`SKIP LOCKED` is appropriate for queue-like work distribution, not for general consistent reads.

Alternative claim mechanisms are acceptable if equivalent semantics are proven.

---

## 11.5 Claim vs publish transaction

Do not hold PostgreSQL row locks while waiting indefinitely for broker network I/O.

A typical pattern:

```text
short DB transaction:
    claim batch / mark processing

publish outside DB transaction

short DB transaction:
    mark confirmed published
```

If process dies after publish before mark:

```text
message may be published again
```

which is safe via stable `messageId` + consumer dedup.

---

## 11.6 Outbox is not exactly-once

Outbox guarantees:

> A committed local outgoing intent is not silently lost due to the DB-commit/publish gap, assuming recovery continues.

It does not guarantee:

```text
broker sees message exactly once
```

Duplicate publication is expected.

---

## 11.7 Outbox liveness metrics

Required observability:

```text
pending_count
oldest_pending_age
publish_attempts
publish_success
publish_failure
confirm_latency
stuck/failed_visible_count
```

---

# 12. Durable Matching workflow

## 12.1 Why Matching cannot be stateless

Danger window:

```text
Matching sends Reserve(D1,R1)
Driver commits R1
Matching crashes before local success
RideRequested redelivered
Redis no longer returns D1
Matching tries D2
```

Potential result:

```text
same Trip holds D1 and D2
```

Therefore Matching persists:

- one round;
- deterministic candidate snapshot;
- exact attempt identity;
- unresolved state.

---

## 12.2 `matching_rounds`

Conceptual:

```text
trip_id
matching_round
input_message_id

status
current_stage
cancel_requested

ranking_config_version
candidate_snapshot_hash/version
candidate_count
next_attempt_ordinal

selected_driver_id?
selected_reservation_id?
result_message_id?

created_at
updated_at
```

Constraints:

```text
UNIQUE(trip_id, matching_round)
UNIQUE(input_message_id)
```

---

## 12.3 `matching_attempts`

Conceptual:

```text
trip_id
matching_round
ordinal

driver_id
reservation_id UNIQUE
state        // READY | IN_FLIGHT | UNKNOWN | REJECTED | RESERVED
last_error_code?
last_checked_at?
created_at
updated_at
```

---

## 12.4 Write-ahead remote intent

Before sending `ReserveDriver`:

```text
persist exact attempt as IN_FLIGHT
with reservationId
```

This is not an Outbox because reserve is synchronous HTTP.

It is durable write-ahead identity.

If Matching dies, restart knows:

```text
which Driver
which reservation
which round
```

must be reconciled.

---

## 12.5 Reserve sequence

```text
load current round
→ load current stage/attempt
→ persist IN_FLIGHT if new
→ call Driver Reserve with same reservationId
```

### Reserved

```text
mark attempt RESERVED
mark round RESERVED
ACK RideRequested
```

Driver's own Outbox emits `DriverReservedV1`.

### Conclusive conflict

```text
mark attempt REJECTED
advance to next attempt if allowed
```

### Timeout/connection uncertainty

```text
mark/keep UNKNOWN
do not advance candidate
reconcile same reservationId
```

---

## 12.6 Reconciliation

Conceptual Driver query:

```text
GET /internal/drivers/{driverId}/reservations/{reservationId}
```

or semantically equivalent API.

Results:

```text
OWNED_BY_THIS_RESERVATION
NOT_COMMITTED_OR_AVAILABLE
RESERVED_BY_OTHER
OFFLINE
ON_TRIP
NOT_FOUND
DEPENDENCY_UNAVAILABLE
```

Only conclusive result allows Matching to advance/terminate.

---

## 12.7 Duplicate `RideRequestedV1`

On redelivery:

```text
lookup by inputMessageId / (tripId, round)
```

If round exists:

- resume;
- do not create new candidate sequence;
- do not generate new reservation IDs.

---

## 12.8 Terminal `NO_DRIVER`

Legal only when:

```text
all bounded attempts are conclusive
no current UNKNOWN/IN_FLIGHT exists
```

Commit together:

```text
MatchingRound.status = NO_DRIVER
+ Outbox(MatchingFailedV1)
```

This protects DSI-035.

---

## 12.9 Cancel matching round

Trip emits:

```text
CancelMatchingRoundV1(tripId, matchingRound)
```

Matching:

```text
cancel_requested = true
```

Rules:

- start no new candidate attempts;
- unresolved existing attempt must still reconcile;
- do not “forget” an in-flight side effect.

If reconciliation reveals reservation committed after Trip already cancelled:

```text
DriverReservedV1 still represents authoritative fact
Trip terminal handler emits exact ReleaseDriverReservationV1
Trip remains CANCELLED
```

---

# 13. Redis GEO projection

## 13.1 Projection data model

Conceptual:

```text
driver:projection:{driverId}
  derivedStatus
  driverAggregateVersion
  longitude
  latitude
  lastLocationCapturedAt
  deviceSessionId
  sequenceNo
  lastLocationMessageId
```

Coordinates also live in:

```text
GEO sorted set
```

---

## 13.2 Driver-state projection rule

Pseudo:

```text
applyDriverState(event):
    if event.aggregateVersion <= stored.driverAggregateVersion:
        NO-OP
    else:
        update derivedStatus/version

        if derivedStatus == AVAILABLE
           and latest location is fresh:
            GEOADD
        else:
            ZREM/GEO membership removal
```

---

## 13.3 Location projection rule

Pseudo:

```text
applyLocation(event):
    if event freshness tuple <= stored freshness tuple:
        NO-OP
    else:
        update latest location metadata/coordinates

        if derivedStatus == AVAILABLE
           and location is fresh:
            GEOADD
        else:
            no candidate membership
```

---

## 13.4 Freshness tuple

Prefer deterministic monotonic identity where possible:

```text
deviceSessionId
sequenceNo
```

with timestamp as data-quality/freshness input.

Do not use local server wall-clock arrival order as the only correctness mechanism.

---

## 13.5 Atomic Redis Function

Use one short Redis Function for:

```text
compare version/freshness
+ metadata update
+ GEO membership update
```

Redis Functions execute atomically and block server activities during execution, so functions must remain short.

This prevents client-visible half state such as:

```text
metadata RESERVED
but GEO still candidate
```

during the function.

---

## 13.6 Redis remains non-authoritative

Even if projection is wrong:

```text
Matching ReserveDriver
→ Driver DB guard decides
```

Redis Function atomicity improves projection consistency.

It does not convert Redis into transaction coordinator.

---

## 13.7 Projection rebuild

If Redis is flushed/unavailable:

- Driver/Trip authoritative DB remains source;
- projection can be rebuilt from current authoritative state and/or new events;
- Matching may be temporarily degraded.

No business invariant relies on Redis persistence.

---

# 14. Saga orchestration

## 14.1 Why Trip owns the Saga

Trip already owns:

- ride lifecycle;
- current assignment reference;
- cancellation;
- terminal ride state.

Therefore Trip is the clearest workflow coordinator.

Saga ownership does not let Trip mutate other databases.

It stores:

```text
what step is pending
what logical operation identity is in use
what compensation remains
```

---

## 14.2 Saga conceptual schema

```text
sagas
- saga_id
- trip_id UNIQUE
- saga_type
- current_step
- status
- step_version

- current_matching_round?
- reservation_id?
- payment_authorize_operation_key?
- payment_capture_operation_key?
- payment_void_operation_key?

- deadline_at?
- retry_count?
- last_error_code?
- requires_review_reason?

- created_at
- updated_at
```

Status examples:

```text
ACTIVE
WAITING
COMPENSATING
COMPLETED
FAILED_VISIBLE
REQUIRES_REVIEW
```

---

## 14.3 Saga local transition pattern

```text
BEGIN
  load Trip + Saga
  validate current step/version
  mutate Trip/Saga
  insert next Outbox command/event
COMMIT
```

Remote network call happens later through transport/consumer.

---

## 14.4 Core workflow

Simplified:

```text
Trip created
→ RideRequested
→ DriverReserved
→ DriverAccepted
→ AuthorizePayment
→ PaymentAuthorized
→ READY_TO_START
→ Start
→ IN_PROGRESS
→ Complete
→ COMPLETED
→ CapturePayment
→ PaymentCaptured / CapturePending
```

---

## 14.5 Compensation path

Example authorization terminal failure:

```text
Trip/Driver accepted
→ PaymentAuthorizationFailed
→ Trip commits CANCELLED/compensation state
→ Outbox ReleaseDriverReservation
→ optional Void if authorization was partially valid under another branch
→ Driver exact release
→ Saga terminal compensated
```

---

## 14.6 Pivot

Before physical ride start:

- reservation release;
- payment void;
- Trip cancellation

can compensate.

After ride reaches the configured physical pivot:

```text
IN_PROGRESS
```

and especially after:

```text
COMPLETED
```

the workflow favors forward recovery.

Example:

```text
Capture fails
→ Trip remains COMPLETED
→ Payment CAPTURE_PENDING / REQUIRES_REVIEW
```

---

## 14.7 Saga restart

No step is considered recoverable if the only record is:

```text
in-memory task/callback
```

On restart:

```text
load Saga rows with non-terminal status
load Trip current state
inspect pending Outbox/operation identities
continue safely
```

---

# 15. Payment execution contract

## 15.1 Payment logical operations

P0:

```text
AUTHORIZE
VOID
CAPTURE
```

Each logical operation has one stable:

```text
operationKey
```

Examples:

```text
trip-123:authorize:v1
trip-123:void:v1
trip-123:capture:v1
```

---

## 15.2 Payment operation table

Conceptual:

```text
payment_operations
- operation_key UNIQUE
- trip_id
- operation_type
- request_fingerprint
- status
- provider_reference?
- result_code?
- created_at
- updated_at
```

---

## 15.3 Consumer + operation idempotency

Payment consumer has two duplicate defenses:

### Transport duplicate

```text
messageId
```

### Business duplicate

```text
operationKey
```

This is important because two different messages may intentionally refer to the same logical Payment step after a workflow retry/recovery.

---

## 15.4 Deterministic provider simulator modes

P0 should support explicit test modes:

```text
SUCCESS
DECLINE
TRANSIENT_FAILURE
TIMEOUT_BEFORE_EFFECT
TIMEOUT_AFTER_EFFECT
SLOW_SUCCESS
```

Required fault tests must not depend on random failure probability.

---

## 15.5 `TIMEOUT_AFTER_EFFECT`

Sequence:

```text
Payment/provider effect persisted
→ response suppressed
→ caller/handler uncertain
```

Reconciliation/retry:

```text
same operationKey
```

returns existing result.

Never generate a fresh operation for the same Saga step.

---

# 16. Retry architecture

## 16.1 Retry is not universally safe

Before retrying, classify:

```text
Was there a possible side effect?
Is there stable logical identity?
Can current state be queried?
Can repeating the same operation be idempotent?
```

---

## 16.2 Failure taxonomy

| Class | Example | Baseline action |
|---|---|---|
| validation/permanent | invalid coordinate/schema | no normal retry |
| authorization | forbidden actor | no normal retry |
| domain conflict | Driver already reserved | conclusive workflow branch |
| transient dependency | temporary connection failure before conclusive effect | bounded retry |
| UNKNOWN side-effect outcome | reserve/payment response lost | reconcile same identity |
| poison message | unsupported schema | bounded handling → DLQ |
| resource pressure | dependency overload | backoff/circuit breaker |
| operator/config error | missing queue binding | visible failure, fix config |

---

## 16.3 One retry owner per boundary

Example baseline:

| Boundary | Retry owner |
|---|---|
| public client write API | client only with same idempotency key |
| Matching → Driver Reserve | Matching application logic |
| RabbitMQ delivery | broker/consumer policy |
| Payment logical provider simulation | Payment service |
| Outbox → RabbitMQ | Outbox relay |
| Saga business step | Saga only after lower-boundary result classification |

---

## 16.4 Retry multiplication calculation

If:

```text
broker retry = 5
Saga retry = 3
HTTP retry = 4
```

then one logical step can create up to:

```text
5 × 3 × 4 = 60
```

attempts.

That can:

- overload dependencies;
- extend recovery time;
- create more unknown outcomes;
- make experiments meaningless.

Hence retry ownership is architectural, not merely configuration.

---

## 16.5 Backoff

Transient retries should use:

- bounded count/deadline;
- increasing delay;
- optional jitter when many workers can synchronize.

Do not use unbounded:

```text
while(true) retry
```

for a business workflow.

---

# 17. RabbitMQ retry and DLQ

## 17.1 Avoid hot requeue loops

Danger:

```text
consumer gets poison message
→ nack requeue immediately
→ gets same message
→ repeat at high rate
```

This burns CPU/broker bandwidth and can starve healthy work.

---

## 17.2 Bounded delayed retry

P0 uses a bounded delayed-redelivery approach.

RabbitMQ 4.3 quorum queues include delayed retry support; a topology-based delayed retry approach is also acceptable if versioned and tested.

Semantic requirement:

```text
retry attempt count is finite
delay is observable/configured
message identity remains understandable
permanent failure eventually exits hot path
```

---

## 17.3 DLQ / error store

When automatic retry is exhausted/permanent:

```text
dead-letter or error record
```

with safe metadata:

```text
messageId
messageType
schemaVersion
correlationId
failureClass
failureCode
attemptCount
firstFailureAt
lastFailureAt
```

Do not store secrets.

---

## 17.4 Redrive

Redrive is an operator/test action.

Rules:

1. understand/fix cause;
2. preserve same logical business identity;
3. preserve/understand message identity semantics;
4. do not redrive obsolete commands blindly;
5. receiver still checks current state.

A DLQ message can become stale while waiting.

---

# 18. Consumer prefetch and backpressure

## 18.1 Prefetch meaning

RabbitMQ prefetch limits the number of unacknowledged deliveries allowed to a consumer.

It controls:

- in-flight work;
- memory;
- fairness;
- throughput.

---

## 18.2 Conservative baseline

Start with a modest prefetch for critical consumers.

Example starting point:

```text
8–32
```

Exact value is tunable and must come from measurement.

Do not freeze `16` as a correctness constant.

---

## 18.3 Why too-high prefetch can hurt

A slow consumer can accumulate many unacked messages.

On crash:

- many messages redeliver;
- memory pressure rises;
- work distribution becomes uneven.

---

## 18.4 Why too-low prefetch can hurt

With one-message prefetch:

- consumer may idle during round trips;
- throughput may underutilize CPU/DB.

Therefore prefetch belongs to performance tuning after semantics are proven.

---

# 19. Synchronous HTTP resilience

## 19.1 .NET resilience pipeline

Current .NET resilience libraries support:

- total request timeout;
- attempt timeout;
- retry;
- circuit breaker;
- concurrency/rate limiting;
- hedging.

The project uses these selectively.

---

## 19.2 Standard pipeline is not automatically safe for writes

Framework defaults understand transport/transient failures.

They do not understand:

```text
reservationId
payment operationKey
current matching attempt
```

Therefore side-effect retry semantics remain application-owned.

---

## 19.3 `ReserveDriver` pipeline

Recommended conceptual responsibilities:

```text
total deadline
attempt timeout
circuit breaker
optional concurrency limiter
NO generic identity-changing retry
```

Application logic decides:

```text
same reservationId retry/reconcile
```

---

## 19.4 Circuit breaker classification

Expected business conflict:

```text
409 DriverNotAvailable
```

must not be treated like:

```text
Driver Service system failure
```

Otherwise normal contention can open the circuit breaker.

Breaker failure predicates should focus on:

- connection failures;
- 5xx/transient server failure;
- timeouts;

not expected domain conflicts.

---

## 19.5 Breaker state

```text
CLOSED
  |
  | failure threshold
  v
OPEN
  |
  | cool-down
  v
HALF_OPEN
  |
  | successful probes
  v
CLOSED
```

Breaker is a resource-protection state machine.

It is not a business state machine.

---

## 19.6 No correctness-destroying fallback

Forbidden fallback:

```text
Driver service unavailable
→ Matching assumes candidate succeeded
```

Forbidden:

```text
Payment unavailable
→ silently treat as authorized
```

Fallback must preserve domain policy.

Often correct fallback is:

```text
degraded/unavailable/pending
```

---

## 19.7 Hedging

Hedging issues multiple attempts/endpoints for latency resilience.

For side-effecting commands:

```text
ReserveDriver
AuthorizePayment
ReleaseDriver
```

baseline rule:

```text
NO hedging
```

unless operation has been explicitly proven safe for concurrent duplicated execution.

This project has no need to take that risk.

---

# 20. Timeout budgets and deadlines

## 20.1 Timeouts are resource limits, not truth

A timeout means:

```text
caller stops waiting
```

It does not mean:

```text
remote rolled back
```

---

## 20.2 Nested timeout budget

A caller total deadline must accommodate inner attempts.

Conceptually:

```text
T_total
>
Σ attempt budgets
+ retry/backoff
+ network/serialization margin
```

Do not configure:

```text
outer timeout = 500ms
inner attempt = 1s
```

and expect coherent behavior.

---

## 20.3 Starting values vs measured values

Exact milliseconds are operational tuning.

Initial values can be conservative.

Final values should be informed by:

- local P95/P99;
- fault tests;
- course environment.

Never write an initial timeout as a production SLO.

---

# 21. Ordering and stale messages

## 21.1 No global total order

The project does not require all queues/services to observe one universal order.

Instead each authority uses:

```text
state
aggregateVersion
matchingRound
reservationId
operationKey
```

to decide whether an input is current.

---

## 21.2 Event versioning

State events from Driver include:

```text
aggregateVersion
```

Projection applies only newer version.

---

## 21.3 Trip stale event

Trip receives:

```text
DriverReservedV1 round=3
```

while:

```text
Trip.currentRound=4
```

Result:

```text
stale/no-op/cleanup branch
```

never current assignment.

---

## 21.4 Late events after terminal state

Example:

```text
Trip CANCELLED
late PaymentAuthorizedV1
```

Trip remains terminal.

Handler may:

- ACK as no-op;
- emit compensation if needed;
- record metric.

Event payload alone never sets state.

---

# 22. Network and broker recovery

## 22.1 RabbitMQ .NET automatic recovery

The RabbitMQ .NET client can automatically recover:

- connection;
- channels;
- listeners;
- QoS;
- publisher-confirm settings;
- exchanges/queues/bindings;
- consumers.

This helps infrastructure recovery.

It does not provide durable application publish buffering.

---

## 22.2 Important publication limitation

RabbitMQ .NET documentation explicitly states:

> publishes attempted while connection is down are not buffered by the client for later delivery.

Therefore:

```text
Outbox remains required
```

for reliable outgoing business intent.

---

## 22.3 Connection-loss publish ambiguity

A message may have:

- reached broker;
- not reached broker;

when connection fails.

Publisher confirms + stable Outbox identity handle this ambiguity.

---

## 22.4 Consumer recovery

Manual ACK consumers must expect:

```text
redelivery after connection recovery
```

Therefore dedup is not optional.

---

## 22.5 Initial connection failure

Automatic recovery does not solve all initial-connect cases.

Service startup must:

- use bounded/repeated connection establishment policy;
- report readiness correctly;
- avoid crashing forever on first broker unavailability if recoverable.

---

# 23. RabbitMQ quorum profile

## 23.1 Final validation topology

```text
rabbitmq-1
rabbitmq-2
rabbitmq-3
```

Critical queues:

```text
quorum
```

---

## 23.2 Data safety claim

Publisher confirms on quorum queues reflect acceptance by quorum according to RabbitMQ semantics.

Consumers use manual ACK.

Final test kills one node while majority remains.

---

## 23.3 Claim boundary

Allowed:

> In the tested three-node RabbitMQ quorum profile, critical queues continued according to quorum behavior when one broker node was lost and a majority remained.

Not allowed:

> The whole system is highly available.

PostgreSQL/Redis P0 do not have equivalent HA.

---

# 24. Redis outage behavior

## 24.1 Redis unavailable

Matching cannot reliably discover candidates.

Allowed:

```text
matching degraded
dependency unavailable
round pending/fails visibly according to policy
```

Forbidden:

```text
scan another service DB directly
randomly assign Driver
```

---

## 24.2 Existing business state remains safe

Redis outage does not invalidate:

- Driver current reservation;
- Trip state;
- Payment state.

Because Redis owns no authority.

---

# 25. Saga failure windows

## 25.1 Trip commits Saga step before command publication

Protected by:

```text
Trip state/Saga + Outbox same transaction
```

Crash:

```text
Outbox relay eventually publishes
```

---

## 25.2 Command published twice

Protected by:

```text
same messageId for same Outbox row
consumer dedup
stable operationKey/reservationId
```

---

## 25.3 Result event duplicated

Protected by:

```text
consumer dedup
Trip current Saga step/FSM guard
```

---

## 25.4 Compensation delayed

Trip can remain:

```text
CANCELLED
```

while exact release command remains pending.

DSL-002 requires eventual cleanup/review.

---

# 26. Failure handling matrix

| Failure | Immediate interpretation | Required behavior |
|---|---|---|
| Trip process crash after local commit | next in-memory step lost | persisted Saga/Outbox resumes |
| Outbox relay crash before publish | pending row remains | later publish |
| Outbox publish accepted, mark lost | publication uncertain locally | same message may republish |
| consumer crash before DB commit | no durable local effect | delivery redelivers |
| consumer crash after commit before ACK | effect committed, ACK unknown | redelivery → dedup |
| RabbitMQ outage | publication/consume unavailable | pending Outbox/backlog; no fake success |
| Reserve timeout | remote result UNKNOWN | reconcile same reservationId |
| Matching crash after remote reserve | unresolved attempt durable | resume/reconcile same attempt |
| Payment command duplicate | same operation | existing result |
| Payment response lost after effect | UNKNOWN | same operationKey reconcile |
| compensation delayed | old reservation may remain | exact guarded cleanup later |
| stale event | possibly obsolete fact | version/round/FSM no-op/conflict |
| poison message | repeated handling unsafe/useless | finite retry → DLQ |
| Redis stale | candidate inefficiency | Driver authority conflict |
| Redis down | candidate discovery degraded | preserve safety |
| one Rabbit node lost | broker member failure | quorum profile continues if majority |
| shared PostgreSQL down | authorities unavailable | explicit outage; no HA claim |

---

# 27. Faultpoint engineering

## 27.1 Why deterministic faultpoints

Random chaos can be useful later.

For correctness proof, the team needs to hit exact windows:

```text
after DB COMMIT before ACK
after Outbox publish before mark
after Driver reserve COMMIT before HTTP response
after Saga COMMIT before dispatch
```

A deterministic barrier is much more defensible than:

```text
Thread.Sleep
then hope to kill at right time
```

---

## 27.2 Faultpoint contract

Testing-only interface conceptually:

```text
WaitAsync("Consumer.AfterCommitBeforeAck")
WaitAsync("Driver.AfterReserveCommitBeforeResponse")
WaitAsync("Outbox.AfterPublishBeforeMark")
WaitAsync("Saga.AfterPersistBeforeDispatch")
```

Requirements:

- disabled by default;
- only test/demo profile;
- bounded wait timeout;
- observable “reached” signal;
- cannot be activated by untrusted production API.

---

## 27.3 Faultpoint is not production workflow logic

Domain/business code must remain correct without faultpoint enabled.

Faultpoint only pauses/suppresses at a predefined boundary.

---

# 28. Observability propagation

## 28.1 Identity separation

```text
tripId
    business aggregate

reservationId
    reservation/fencing

operationKey
    business side effect

messageId
    integration message

correlationId
    business diagnostic grouping

traceId
    observed execution trace
```

Never substitute one for another.

---

## 28.2 HTTP propagation

Use:

```text
traceparent
tracestate
```

for distributed tracing.

Pass/generate:

```text
correlationId
```

according to project convention.

---

## 28.3 Messaging propagation

Producer attaches message creation/trace context in transport headers.

Consumer extracts it before processing.

OpenTelemetry messaging conventions explicitly require propagation of message creation context to directly correlate producer and consumer traces.

---

## 28.4 Long-running async workflow

A single ride may span long time and multiple traces.

Therefore:

```text
correlationId
tripId
sagaId
```

help reconnect business evidence even if tracing is sampled or trace topology changes.

---

## 28.5 Safe telemetry fields

Useful:

```text
service.name
service.instance.id
deployment.environment

traceId
spanId
correlationId
messageId

tripId
matchingRound
reservationId
operationKey
sagaStep

outcome
errorClass
durationMs
retryAttempt
redelivered
```

Do not include:

- passwords;
- tokens;
- signing secrets;
- raw sensitive payment data;
- unnecessary PII.

---

# 29. Observability metrics contract

## 29.1 Outbox

```text
outbox_pending
outbox_oldest_pending_seconds
outbox_publish_total
outbox_publish_fail_total
outbox_confirm_latency
```

## 29.2 Consumer

```text
consumer_delivery_total
consumer_ack_total
consumer_redelivery_total
consumer_dedup_hit_total
consumer_processing_duration
```

## 29.3 Retry/DLQ

```text
retry_total{reason}
dlq_total{messageType}
dlq_oldest_age
```

## 29.4 Matching

```text
matching_round_total
matching_duration
matching_candidate_count
matching_reserve_conflict_total
matching_unknown_total
matching_reconcile_total
matching_no_driver_total
```

## 29.5 Driver

```text
driver_reserve_success_total
driver_reserve_conflict_total
driver_stale_release_blocked_total
```

## 29.6 Saga

```text
saga_active{step}
saga_compensation_total
saga_oldest_active_age
saga_requires_review_total
```

## 29.7 Payment

```text
payment_operation_total{type,outcome}
payment_idempotency_hit_total
payment_unknown_total
payment_reconcile_total
```

---

# 30. Observability is not a write-ahead log

Forbidden recovery:

```text
look at Jaeger
infer what probably happened
change DB manually
```

Normal recovery must use:

```text
aggregate state
Saga
MatchingRound/Attempt
Payment operation
Outbox
processed message
broker durable state
```

Trace/logs are:

- diagnosis;
- evidence;
- performance analysis.

---

# 31. Topology-as-code

RabbitMQ exchanges/queues/bindings/policies must be created from:

- versioned configuration;
- startup bootstrap;
- infrastructure scripts.

Manual Management UI creation is not sufficient.

Why:

- clean-clone reproducibility;
- CI;
- correct queue type;
- consistent DLQ/retry args;
- evidence.

---

# 32. Configuration classes

## 32.1 Normative semantic values

Require architecture review:

```text
ACK after local durable effect
same operation identity on retry
no new candidate while reserve UNKNOWN
Outbox same transaction as required state change
Redis non-authoritative
no global order assumption
finite retry
```

---

## 32.2 Operational starting defaults

May tune through config PR/measurement:

```text
prefetch
attempt timeout
total timeout
retry delay
retry count
circuit-breaker thresholds
Outbox batch size
relay polling interval
candidate radius/count
```

---

## 32.3 Measured results

Never pre-freeze as achieved:

```text
P95 matching latency
throughput
recovery time
maximum sustainable arrival rate
```

These come from Doc 03 experiments.

---

# 33. Quantitative reliability calculations

## 33.1 Retry multiplication

Already shown:

```text
R_total ≈ ∏ R_layer
```

for nested independent retry counts.

Minimize layers.

---

## 33.2 Backlog after outage

Let:

```text
λ = arrival rate msg/s
T = outage duration s
```

Approximate:

```text
B ≈ λT
```

---

## 33.3 Backlog drain

If recovered consumer service rate:

```text
μ > λ
```

then:

```text
T_drain ≈ B / (μ - λ)
```

This is a planning approximation.

---

## 33.4 Prefetch upper-bound intuition

If:

```text
C = consumer count
P = prefetch per consumer
```

maximum broker-delivered but unacked in-flight messages is roughly:

```text
C × P
```

Example:

```text
4 consumers × prefetch 16 = up to ~64 unacked
```

This helps explain redelivery burst after consumer failure.

---

## 33.5 Outbox poll trade-off

If relay polls every:

```text
Δ
```

then idle-case publication latency can include approximately:

```text
0..Δ
```

before network/broker latency.

Very small `Δ` increases DB polling overhead.

Course system can use modest polling and measure.

---

# 34. Contract security and trust

## 34.1 Internal message is not automatically trusted semantically

A message received from RabbitMQ still undergoes:

- schema validation;
- version check;
- current domain state check;
- stable identity check.

Broker access does not turn stale input into legal input.

---

## 34.2 Actor identity in public HTTP

Public APIs derive:

```text
userId/driverId
```

from authenticated server context.

Do not trust arbitrary actor ID supplied in request body.

---

## 34.3 Internal endpoints

Reserve/reconcile/internal workflow endpoints should not be exposed as unprotected public capabilities in production-like profile.

Course baseline may use network segmentation/shared internal auth approach appropriate to scope, but public role tokens must not be able to impersonate system commands freely.

---

# 35. Mechanism-to-invariant traceability

| Mechanism | Protects / supports |
|---|---|
| conditional Driver mutation | DSI-001/002/003 |
| reservationId | DSI-004/005/018/031/033 |
| unique active Trip defense | DSI-008/030 |
| Trip FSM/version | DSI-009/010/011/012/032 |
| stable FareSnapshot | DSI-014 |
| persisted MatchingRound | DSI-015/016/017/021/022/035; DSL-003 |
| deterministic attempts | DSI-018/019/020/023 |
| Payment operation uniqueness | DSI-024/025/034; DSL-004 |
| Saga | DSI-026/027/028/033/034; DSL-002/005 |
| Outbox | DSI-035; DSL-002/005/006 |
| publisher confirms | DSL-006 transport support |
| consumer ACK after commit | durable at-least-once consumption |
| consumer dedup | duplicate delivery safety |
| Redis version/freshness function | DSI-006/007 |
| Notification decoupling | DSI-029; DSL-007 |
| retry classification | DSL-001..006 |
| DLQ/review | DSL-001/004/005/006 |
| OTel context | explainability only |
| Compose DNS | naming/discovery requirement |

---

# 36. Mandatory engineering rules for PR review

A PR must be rejected if it violates any of these.

## R-01

No required integration event is published “next to” an authoritative DB commit without Outbox when losing the event would break workflow correctness.

## R-02

No critical consumer ACKs before durable local effect.

## R-03

No side-effecting write retry without stable identity or explicit reconcile story.

## R-04

No handler applies event payload directly as authoritative state without loading/checking current domain state.

## R-05

No Redis/distributed lock becomes Driver reservation authority.

## R-06

No unguarded:

```text
ReleaseDriver(driverId)
```

for compensation.

Exact `reservationId` required.

## R-07

No nested unbounded retry.

## R-08

No broker topology exists only through manual UI clicks.

## R-09

No global “exactly once” claim.

Preferred wording:

> at-least-once transport with idempotent/effectively-once logical effect for defined operations.

## R-10

No stateless Matching loop that forgets unresolved remote reservation attempts.

## R-11

No candidate advance while current reserve outcome is UNKNOWN.

## R-12

No non-atomic Redis eligibility projection mutation that can regress version/freshness.

## R-13

No side-effect hedging for Driver/Payment writes.

## R-14

No trace/log dependency in business recovery.

## R-15

No P1 platform expansion while Core Gate is red.

---

# 37. Mandatory crash-window walkthroughs

Before shared messaging abstraction is considered frozen, all three team members must explain:

## CW-01 — Producer DB commit / publish gap

```text
DB committed
process dies before publish
```

Answer:

```text
Outbox
```

## CW-02 — Publish / confirm gap

```text
broker accepted
publisher did not observe confirm
```

Answer:

```text
republish same messageId
consumer dedup
```

## CW-03 — Consumer commit / ACK gap

```text
DB committed
consumer dies before ACK
```

Answer:

```text
redelivery
processed-message dedup/current-state guard
```

## CW-04 — Reserve commit / response gap

```text
Driver reserved
Matching timeout
```

Answer:

```text
UNKNOWN
same reservationId reconcile
```

## CW-05 — Compensation delay

```text
Trip cancelled
Driver release not yet delivered
```

Answer:

```text
terminal Trip remains terminal
durable exact release intent retries
```

---

# 38. Verification obligations exported to Doc 03

Doc 03 must implement reproducible proofs for at least:

```text
DW-01 Outbox commit → crash before publish
DW-02 publish accepted → mark/confirm handling lost → duplicate
DW-03 consumer commit → crash before ACK
DW-04 poison message → bounded retry → DLQ
DW-05 reserve timeout after remote commit
DW-06 Matching crash after Driver commit
DW-07 duplicate RideRequested
DW-08 stale projection version
DW-09 stale location sequence
DW-10 Payment same-key concurrency
DW-11 Payment timeout-after-effect
DW-12 Saga restart
DW-13 compensation delayed/outage
DW-14 broker reconnect/backlog drain
DW-15 Rabbit quorum one-node failure
```

Each proof must assert:

```text
final authoritative business state
```

not only logs.

---

# 39. Research/industry alignment

## 39.1 RabbitMQ data safety

RabbitMQ's current 4.3 documentation explicitly distinguishes:

```text
publisher confirms
```

from:

```text
consumer acknowledgements
```

and calls them orthogonal mechanisms.

For quorum queues, confirms represent replicated acceptance by a quorum according to RabbitMQ semantics, while manual ACK ensures failed consumers cause unprocessed work to return.

This directly supports the P0 producer/consumer contract.

---

## 39.2 RabbitMQ client recovery limitation

The .NET client can recover connection/topology/consumers.

However, its documentation states that messages published while the connection is down are not internally queued for later sending.

Therefore client auto-recovery does not replace Outbox.

---

## 39.3 .NET resilience

Modern `Microsoft.Extensions.Resilience` / `Microsoft.Extensions.Http.Resilience` is built on Polly and exposes retry, timeout, circuit breaker, rate/concurrency limiting, fallback and hedging.

The project uses the framework as an implementation mechanism but keeps business retry semantics above it.

Legacy `Microsoft.Extensions.Http.Polly` should not be chosen for new baseline work because Microsoft marks it deprecated in favor of current resilience packages.

---

## 39.4 Redis

Redis Functions execute atomically and block other server activity for the function's duration.

This makes them suitable for short projection compare-and-update operations.

It also means they must remain small.

Redis `GEOSEARCH` provides bounded radius/box geospatial lookup and optional nearest-distance ordering.

---

## 39.5 PostgreSQL

PostgreSQL unique indexes/constraints enforce uniqueness at persistence level.

`SKIP LOCKED` can distribute queue-like rows among multiple workers.

Neither replaces business semantics.

---

## 39.6 OpenTelemetry

OpenTelemetry messaging semantic conventions remain marked **Development** in the current specification.

The project should therefore:

- pin/record instrumentation behavior;
- avoid making report correctness depend on exact evolving attribute names;
- preserve stable business IDs independently.

---

# 40. External authoritative references

These references validate mechanism behavior. Project-specific semantics remain defined by Docs 00–02.

## RabbitMQ

1. **Consumer Acknowledgements and Publisher Confirms**  
   https://www.rabbitmq.com/docs/confirms

2. **Reliability Guide**  
   https://www.rabbitmq.com/docs/reliability

3. **Quorum Queues**  
   https://www.rabbitmq.com/docs/quorum-queues

4. **RabbitMQ .NET/C# Client API Guide**  
   https://www.rabbitmq.com/client-libraries/dotnet-api-guide

5. **Consumer Prefetch**  
   https://www.rabbitmq.com/docs/consumer-prefetch

6. **RabbitMQ 4.3 Highlights**  
   https://www.rabbitmq.com/blog/2026/04/23/rabbitmq-4.3-release

## Microsoft / .NET

7. **Saga distributed transactions pattern**  
   https://learn.microsoft.com/en-us/azure/architecture/patterns/saga

8. **Introduction to resilient app development**  
   https://learn.microsoft.com/en-us/dotnet/core/resilience/

9. **HttpStandardResilienceOptions**  
   https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.http.resilience.httpstandardresilienceoptions?view=net-10.0-pp

10. **ASP.NET Core health checks (.NET 10)**  
    https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/health-checks?view=aspnetcore-10.0

## Redis

11. **Redis Functions**  
    https://redis.io/docs/latest/develop/programmability/functions-intro/

12. **Redis GEOSEARCH**  
    https://redis.io/docs/latest/commands/geosearch/

## PostgreSQL

13. **PostgreSQL 18 — Unique indexes**  
    https://www.postgresql.org/docs/18/indexes-unique.html

14. **PostgreSQL 18 — SELECT / locking / SKIP LOCKED**  
    https://www.postgresql.org/docs/18/sql-select.html

15. **PostgreSQL 18 — Transaction isolation**  
    https://www.postgresql.org/docs/18/transaction-iso.html

## Docker

16. **Docker Compose networking/service discovery**  
    https://docs.docker.com/compose/how-tos/networking/

## OpenTelemetry / W3C

17. **OpenTelemetry messaging semantic conventions**  
    https://opentelemetry.io/docs/specs/semconv/messaging/

18. **OpenTelemetry messaging spans/context propagation**  
    https://opentelemetry.io/docs/specs/semconv/messaging/messaging-spans/

19. **W3C Trace Context**  
    https://www.w3.org/TR/trace-context/

---

# 41. Freeze checklist

Before promoting this document to **ACCEPTED/FROZEN**:

## Communication

- [ ] every P0 cross-service interaction classified sync/async;
- [ ] only authority-owning service publishes authoritative state event;
- [ ] no hidden cross-service transaction.

## Identities

- [ ] `messageId` semantics frozen;
- [ ] `operationKey` semantics frozen;
- [ ] `reservationId` retry/reconcile semantics frozen;
- [ ] correlation and trace identity separated.

## RabbitMQ

- [ ] exchanges/queues/bindings versioned;
- [ ] critical queue type decided;
- [ ] persistent message/durable queue policy;
- [ ] publisher confirms required;
- [ ] critical manual ACK required;
- [ ] prefetch tunable;
- [ ] retry finite;
- [ ] DLQ visible.

## Consumer

- [ ] processed-message pattern frozen;
- [ ] dedup + business effect atomic;
- [ ] ACK after commit;
- [ ] stale state checked independently of dedup.

## Outbox

- [ ] state + required outgoing intent same local transaction;
- [ ] stable `messageId`;
- [ ] relay confirm rule;
- [ ] duplicate publication accepted;
- [ ] claim/restart strategy defined.

## Matching

- [ ] round durable;
- [ ] candidate snapshot durable;
- [ ] attempt identity durable before remote reserve;
- [ ] UNKNOWN blocks next candidate;
- [ ] duplicate RideRequested resumes same round;
- [ ] NO_DRIVER + Outbox atomic.

## Redis

- [ ] Redis explicitly non-authoritative;
- [ ] Driver aggregate version guard;
- [ ] location freshness guard;
- [ ] atomic metadata/GEO update;
- [ ] rebuild/degraded behavior defined.

## Saga/Payment

- [ ] Saga persisted;
- [ ] next command from local Outbox;
- [ ] operationKey stable;
- [ ] compensation exact/fenced;
- [ ] pivot/forward recovery defined.

## Resilience

- [ ] retry owner named per boundary;
- [ ] write retry has stable identity;
- [ ] no side-effect hedging;
- [ ] domain conflicts excluded from breaker failure where appropriate;
- [ ] timeout not treated as conclusive failure.

## Recovery

- [ ] Rabbit client auto-recovery limitations understood;
- [ ] broker outage leaves durable pending work;
- [ ] process restart loads durable state;
- [ ] no recovery depends on logs/traces.

## Observability

- [ ] W3C trace propagation;
- [ ] messaging context propagation;
- [ ] `service.name/instance.id`;
- [ ] safe business correlation;
- [ ] required mechanism metrics;
- [ ] no secret/PII leakage.

## Proofability

- [ ] all major crash windows have deterministic faultpoint/test path;
- [ ] Doc 03 can assert final business state;
- [ ] broker HA claim is narrow and testable.

---

# 42. North star

> **A reliable distributed workflow does not require the network or broker to behave perfectly. It requires the application to remain correct when communication is duplicated, delayed, lost from one participant's point of view, reordered, interrupted or resumed. The mechanism is always the same family of ideas: persist local truth, preserve logical identity, expose uncertainty, make repeats harmless, fence stale work, and recover from durable state.**
