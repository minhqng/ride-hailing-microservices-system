# DS03 DISTRIBUTED RIDE-HAILING SYSTEM — MEMBER C EXECUTION PLAYBOOK
## Payment Authority · RabbitMQ · Outbox Relay · Dedup · Retry/DLQ · Observability · Fault Platform · Release

> **Document:** `docs/05C-member-c-payment-messaging-platform.md`  
> **Course:** Distributed Systems  
> **Topic:** DS03 — Ride-hailing Microservices System simulating Grab/Uber  
> **Type:** Individual Execution Playbook / 16-week chronological delivery guide  
> **Status:** **PROPOSED FREEZE v2.0 — REVIEW CANDIDATE**  
> **Baseline date:** **2026-09-26**  
> **Member:** **C**  
> **Primary role:** Payment Service · Payment operation idempotency · RabbitMQ topology · canonical MessageEnvelope · publisher confirms · manual consumer ACK · processed-message dedup · bounded retry / DLQ · shared Outbox relay mechanics · Notification worker · OpenTelemetry · Docker Compose/platform bootstrap · deterministic faultpoint framework · RabbitMQ reconnect/quorum validation · security/release infrastructure  
> **Secondary role:** reviewer for Trip Outbox/Saga delivery semantics, Matching retry/UNKNOWN semantics, DB/service isolation, evidence packaging and final release  
> **Planning horizon:** 16 weeks  
> **Physical planning envelope:** **~151 focused hours**  
> **Average:** ~9.4 focused h/week  
> **Heavy weeks:** W8–W12 and W14  
> **Normative hierarchy:** `00 → 01 → 02 → 03 → 04 → 05C`  
> **Execution rule:** this document refines C's implementation chronology, task granularity, theory, tests, evidence and review obligations. It MUST NOT redefine domain authority, invariants, Saga semantics or test claims already frozen in Docs 00–04.

> **Member C north star.**  
> **Every network or message step can fail, repeat, reconnect or become ambiguous. C's job is to make those failures visible and harmless: logical side effects have stable identities, committed work is durably transferred, consumers ACK only after durable effect, retries are bounded, poison work becomes visible, and observability explains recovery without becoming recovery state itself.**

---

# 0. How C should use this playbook

At the start of every issue, C should answer:

1. Which business effect or delivery boundary is being protected?
2. Which identity makes replay safe?
3. What does “success” mean at this boundary?
4. What exact crash window exists before/after commit, publish or ACK?
5. What is durable before the process can die?
6. Who owns retry at this boundary?
7. What happens after retry is exhausted?
8. Which test/faultpoint proves the mechanism?
9. What final authoritative state proves no duplicate/loss occurred?
10. What telemetry explains the path without being required for correctness?

Each week follows:

```text
objective
→ theory/knowledge
→ issue pack
→ implementation order
→ crash/failure windows
→ tests/evidence
→ review/handoff
→ exit criteria
```

Do not accept “RabbitMQ will handle it” as an answer.

---

# 1. Member C role contract

## 1.1 What C owns

| Area | C owns | C must not silently own |
|---|---|---|
| Payment Service | operation persistence, idempotency, deterministic provider simulator, Payment outcomes | Trip lifecycle |
| RabbitMQ platform | topology-as-code, critical queues, confirms, ACK conventions, retry/DLQ policies | business legality |
| MessageEnvelope | message/correlation/causation/trace metadata conventions | domain state |
| Consumer reliability | processed-message dedup pattern, ACK-after-commit contract | stale-state domain decision |
| Outbox relay | generic claim/publish/confirm/mark mechanics | semantic creation of another service's outgoing intent |
| Retry/DLQ | finite retry infrastructure and error visibility | deciding that UNKNOWN side-effect failed |
| Notification | async non-authoritative delivery/projection | ride truth |
| Observability | OTel bootstrap, propagation, platform metrics, safe fields | business recovery |
| Compose | reproducible core/obs/fault/ha profiles, health/bootstrap | application correctness shortcuts |
| Fault platform | deterministic barriers/faultpoints | production business behavior |
| Broker recovery | connection/topology recovery validation, one-node quorum proof | full-system HA |
| Security/release | secrets, DB/service isolation support, release smoke, evidence packaging | changing business authority |

---

## 1.2 C's canonical correctness contract

C is primary or co-owner of:

```text
DSI-024  Payment Operation Idempotency
DSI-025  Payment Key Semantic Stability
DSI-026  Authorization Before Start        [shared A/C]
DSI-027  Capture Ordering                  [shared A/C]
DSI-028  Completed Trip Does Not Regress on Capture Failure [shared A/C]
DSI-029  Notification Independence
DSI-034  One Logical Payment Effect per Saga Step [shared A/C]

DSL-004  Payment Operation Resolution
DSL-006  Outgoing Intent Is Not Silently Lost [shared A/C]
DSL-007  Notification Failure Is Non-blocking
```

C also owns infrastructure mechanisms that support:

```text
DSI-015 / DSI-035 Matching terminal durability
DSI-030 / DSI-033 cross-service cleanup
DSL-002 / DSL-003 / DSL-005
```

without owning those business states.

---

## 1.3 C's five defense anchors

C must explain without source code:

### 1. Publisher confirm versus consumer ACK

```text
publisher confirm
→ publisher ↔ broker acceptance

consumer ACK
→ consumer ↔ broker completed processing
```

They are independent.

### 2. Commit-before-ACK

Critical consumer:

```text
receive
→ local durable effect
→ COMMIT
→ ACK
```

not:

```text
ACK
→ maybe process
```

### 3. Outbox is durable intent, not exactly-once transport

Outbox prevents:

```text
DB COMMIT
→ process dies
→ outgoing message silently lost
```

but can republish duplicates.

### 4. Stable Payment operation identity

One logical Saga Payment step:

```text
one operationKey
```

even under:

- duplicate messages;
- retry;
- timeout after effect;
- restart.

### 5. Telemetry is evidence, not authority

A dropped trace must not block:

- Payment resolution;
- Saga;
- dedup;
- Outbox.

---

# 2. Theory C must master

## 2.1 RabbitMQ's two data-safety directions

RabbitMQ's current 4.3 documentation explicitly states publisher confirms and consumer acknowledgements are orthogonal.

That means:

```text
publisher confirm
```

does **not** prove:

```text
consumer processed
```

and:

```text
consumer ACK
```

does **not** tell producer what happened.

This separation is foundational.

---

## 2.2 Why manual ACK matters

If auto-ACK or ACK-before-commit is used:

```text
broker forgets delivery
consumer crashes before DB commit
```

the logical effect can be lost.

With manual ACK after commit:

```text
commit succeeds
process dies before ACK
```

broker redelivers.

This creates duplicate delivery, which is expected and solved with dedup/idempotency.

---

## 2.3 Why producer confirms matter

Publishing is asynchronous.

Writing bytes to a socket is not proof the broker safely accepted them.

Publisher confirms provide broker-side publication outcome.

For quorum queues, RabbitMQ documents that a publisher confirm is issued only after the message has been replicated to a quorum and is considered safe in that queue context.

---

## 2.4 Why automatic recovery does not replace Outbox

RabbitMQ .NET client can recover:

- connection;
- channels;
- topology;
- consumers.

But RabbitMQ's .NET client guide explicitly states that publishes attempted while a connection is down are not buffered by the client for later delivery.

Therefore:

```text
automatic recovery
≠
durable outgoing business intent
```

Outbox is still required.

---

## 2.5 Delivery semantics

Baseline:

```text
at-least-once transport
+
stable message identity
+
consumer dedup
+
business idempotency/FSM guard
=
effectively-once logical effect within defined scope
```

Do not claim global exactly-once.

---

## 2.6 Message identity versus business operation identity

Examples:

```text
messageId
→ one logical integration message

operationKey
→ one logical Payment effect

reservationId
→ one logical Driver reservation

correlationId
→ diagnostic flow grouping

traceId
→ observability execution identity
```

A Payment workflow retry may publish a new message that still refers to the same:

```text
operationKey
```

depending on frozen producer semantics.

---

## 2.7 Payment idempotency

A production analogy is Stripe's idempotency model:

- same idempotency key can be retried after connection errors;
- server recognizes the logical operation;
- repeated request does not perform the same logical effect twice.

This project uses the same principle but owns its own `operationKey` contract.

---

## 2.8 Outbox dual-write problem

Unsafe:

```text
COMMIT business state
→ publish
```

because crash can occur between.

Transactional Outbox:

```text
BEGIN
  business mutation
  outgoing intent
COMMIT
```

Relay later publishes the intent.

Duplicate publication remains possible.

---

## 2.9 Consumer dedup must be atomic with local effect

Unsafe:

```text
insert processed_message
COMMIT
then apply business effect
```

Crash can suppress real processing permanently.

Correct:

```text
BEGIN
  processed-message uniqueness
  business effect
  outgoing local Outbox if needed
COMMIT
ACK
```

---

## 2.10 Retry is a semantic decision

Never write:

```text
catch(Exception)
{
    nack(requeue: true);
}
```

as universal behavior.

Classify:

```text
validation/permanent
domain conflict
transient dependency
UNKNOWN side effect
poison schema
resource overload
```

Only then choose retry/no-retry/reconcile/DLQ.

---

## 2.11 Circuit breaker is not reconciliation

A circuit breaker answers:

> Should we temporarily stop calling this failing dependency?

It does not answer:

> Did the previous write commit?

Thus breaker cannot turn an UNKNOWN write into a failed write.

---

## 2.12 Quorum queue claim boundary

RabbitMQ quorum queues are:

- replicated;
- Raft-based;
- designed for data safety;
- appropriate for critical durable queues.

They do not make:

```text
PostgreSQL
Redis
entire ride workflow
```

highly available.

---

## 2.13 OpenTelemetry propagation

OTel messaging semantic conventions are still marked **Development** as of the project baseline.

Therefore:

```text
trace field naming
```

may evolve.

Business recovery must depend on:

```text
messageId
operationKey
aggregate state
Saga
Outbox
```

not telemetry schema.

---

# 3. C's recommended code shape

```text
src/
├─ PaymentService/
│  ├─ Api/
│  ├─ Domain/
│  │  ├─ PaymentOperation.cs
│  │  ├─ PaymentOperationType.cs
│  │  ├─ PaymentOperationState.cs
│  │  └─ PaymentErrors.cs
│  ├─ Application/
│  │  ├─ Authorize/
│  │  ├─ Capture/
│  │  ├─ Void/
│  │  └─ Consumers/
│  ├─ Infrastructure/
│  │  ├─ Persistence/
│  │  ├─ ProviderSimulator/
│  │  ├─ Outbox/
│  │  └─ Dedup/
│  └─ Observability/
│
├─ NotificationWorker/
│  ├─ Consumers/
│  ├─ Projection/
│  ├─ Persistence/
│  └─ Observability/
│
└─ BuildingBlocks/
   ├─ Messaging/
   │  ├─ Contracts/
   │  ├─ Publishing/
   │  ├─ Consuming/
   │  ├─ Outbox/
   │  ├─ Dedup/
   │  └─ Retry/
   ├─ Observability/
   ├─ Health/
   └─ Testing/
      └─ FaultPoints/

deploy/
├─ compose/
├─ rabbitmq/
├─ otel/
├─ prometheus/
├─ grafana/
└─ jaeger/

scripts/
├─ dev/
├─ fault/
├─ demo/
└─ evidence/
```

---

# 4. C's Payment persistence model

## 4.1 `payment_operations`

Conceptual:

```text
operation_key        UNIQUE
trip_id
operation_type       AUTHORIZE | CAPTURE | VOID
request_fingerprint
state

provider_effect_id?
result_code?
failure_class?
attempt_count?

created_at
updated_at
completed_at?
```

---

## 4.2 Recommended operation states

Normalized:

```text
PENDING
PROCESSING
SUCCEEDED
DECLINED
UNKNOWN
REQUIRES_REVIEW
```

The exact persisted enum may differ, but business semantics must preserve Docs 01–02.

---

## 4.3 Request fingerprint

Same:

```text
operationKey
```

with semantically different payload:

```text
amount
currency
trip
operation type
```

must be:

```text
IDEMPOTENCY_CONFLICT
```

not overwritten.

---

# 5. Deterministic Payment provider simulator

## 5.1 Why deterministic

Random 5% failure is poor correctness evidence.

Required modes:

```text
SUCCESS
DECLINE
TRANSIENT_FAILURE
TIMEOUT_BEFORE_EFFECT
TIMEOUT_AFTER_EFFECT
SLOW_SUCCESS
```

---

## 5.2 `TIMEOUT_BEFORE_EFFECT`

Conceptual:

```text
request received
provider effect NOT committed
response delayed/suppressed
```

Reconciliation finds:

```text
no prior logical effect
```

according to simulator contract.

---

## 5.3 `TIMEOUT_AFTER_EFFECT`

Conceptual:

```text
provider effect committed
response withheld
caller sees timeout
retry/reconcile same operationKey
existing effect returned
```

This is the signature Payment UNKNOWN proof.

---

## 5.4 Security of simulator controls

Fault/test mode controls:

```text
Development
Testing
Fault
```

only.

Release/demo-safe profile must not expose unrestricted public controls that allow arbitrary users to force Payment outcomes.

---

# 6. Canonical MessageEnvelope contract

Conceptual:

```json
{
  "messageId": "01K...",
  "messageType": "payment.authorize.v1",
  "schemaVersion": 1,
  "occurredAt": "...",

  "correlationId": "...",
  "causationId": "...",

  "aggregateType": "Trip",
  "aggregateId": "trip-...",
  "aggregateVersion": 7,

  "sagaId": "saga-...",
  "operationKey": "trip-...:authorize:v1",

  "payload": {}
}
```

Transport headers carry:

```text
traceparent
tracestate
```

where relevant.

Do not put business recovery identity only in trace baggage.

---

# 7. RabbitMQ topology contract

## 7.1 Exchange baseline

Recommended:

```text
ride.commands
ride.events
ride.deadletter
```

Topic exchanges.

Keep topology small.

---

## 7.2 Queue examples

```text
matching.ride-requested.q
trip.driver-events.q
trip.payment-outcomes.q
payment.commands.q
notification.business-events.q
matching.driver-projection.q
```

Exact names live in executable config.

---

## 7.3 Queue ownership

One logical consumer capability gets its own queue.

Do not put unrelated event consumers on one queue and expect fan-out.

---

## 7.4 Critical queues

Final reliability profile:

```text
quorum queues
```

with:

- durable queue;
- persistent messages;
- publisher confirms;
- manual ACK.

RabbitMQ documents quorum queues as appropriate for cases where message loss would materially harm correctness.

---

# 8. Publisher-confirm algorithm

Canonical relay/publisher path:

```text
load durable outgoing intent
→ serialize same logical messageId
→ publish to route
→ await/track confirm
→ only then mark published
```

Possible outcomes:

```text
ACK
NACK
timeout
connection loss
unroutable return
```

No silent:

```text
publish()
row=PUBLISHED
```

without evidence of broker acceptance.

---

# 9. Consumer ACK + dedup algorithm

Canonical:

```text
RabbitMQ delivers M
→ validate envelope/schema
→ BEGIN DB
→ try processed_messages(consumerScope,messageId)
→ if duplicate:
       COMMIT no-op
       ACK
  else:
       apply legal local effect
       persist processed marker
       persist local Outbox if needed
       COMMIT
       ACK
```

On error before commit:

```text
no ACK
→ retry/DLQ policy
```

On crash after commit before ACK:

```text
redelivery
→ dedup hit
→ no second logical effect
→ ACK
```

---

# 10. Processed-message schema

Conceptual:

```text
processed_messages
- consumer_scope
- message_id
- message_type
- processed_at
```

Constraint:

```text
UNIQUE(consumer_scope, message_id)
```

Optional metadata:

```text
handler_version
aggregate_id
```

Do not make dedup retention so short that expected redeliveries become unsafe.

---

# 11. Outbox relay mechanics C owns

A/B own semantic creation in their services.

C owns reusable relay mechanics.

## 11.1 Conceptual schema

```text
outbox_messages
- message_id
- message_type
- payload
- headers
- occurred_at

- status
- attempt_count
- next_attempt_at
- claimed_by?
- claimed_at?
- published_at?
- last_error_code?
```

---

## 11.2 Multi-relay claim

Possible PostgreSQL pattern:

```text
SELECT ...
FOR UPDATE SKIP LOCKED
LIMIT batch
```

Then mark claim in a short transaction.

`SKIP LOCKED` is useful for queue-like workers.

It is not a general consistency primitive for arbitrary reads.

---

## 11.3 Do not hold DB locks across broker I/O

Preferred:

```text
short DB claim
→ publish outside DB transaction
→ short DB confirm/mark
```

Crash after publish before mark:

```text
safe duplicate
```

---

# 12. Retry and DLQ contract

## 12.1 Retry classes

```text
PERMANENT
TRANSIENT
DOMAIN_CONFLICT
UNKNOWN
POISON
OVERLOAD
```

---

## 12.2 Immediate requeue loop is prohibited

Bad:

```text
nack(requeue=true)
```

for deterministic poison input.

Can create hot loops and starvation.

---

## 12.3 Delayed bounded retry

Use broker/application retry path with:

- finite attempts;
- increasing delay;
- observable attempt count;
- stable identity.

RabbitMQ 4.3 quorum queues provide delayed-retry/poison-handling capabilities that may be used where they fit the client's semantics.

---

## 12.4 DLQ semantics

DLQ entry should preserve safe metadata:

```text
messageId
messageType
schemaVersion
correlationId
failureClass
failureCode
attemptCount
timestamps
```

DLQ is not a garbage bin.

---

## 12.5 Redrive

Rules:

1. root cause understood/fixed;
2. logical identity preserved;
3. stale business state still checked;
4. bulk blind redrive prohibited;
5. evidence records operator/test action.

---

# 13. Notification worker contract

Notification is intentionally non-authoritative.

If:

```text
Trip COMPLETED
Notification unavailable
```

then:

```text
Trip remains COMPLETED
```

Notification can record:

```text
PENDING
DELIVERED
FAILED_VISIBLE
```

according to implementation.

The worker's failure must not mutate authoritative Trip/Driver/Payment state.

---

# 14. Observability contract

## 14.1 Required service resource attributes

```text
service.name
service.instance.id
deployment.environment
```

---

## 14.2 Safe business context

```text
tripId
matchingRound
reservationId
operationKey
messageId
correlationId
sagaStep
outcome
retryAttempt
redelivered
```

Only when safe and necessary.

---

## 14.3 Do not put in telemetry

- passwords;
- auth tokens;
- private keys;
- raw secret/payment credentials;
- unnecessary PII.

---

## 14.4 Metrics C owns/coordinates

### Broker/publisher

```text
publish_total
confirm_total
publish_failure_total
confirm_latency
```

### Consumer

```text
delivery_total
ack_total
redelivery_total
dedup_hit_total
processing_duration
```

### Retry/DLQ

```text
retry_total{reason}
dlq_total{type}
dlq_oldest_age
```

### Payment

```text
payment_operation_total{type,outcome}
payment_idempotency_hit_total
payment_unknown_total
payment_requires_review_total
```

### Outbox

Coordinate:

```text
pending_count
oldest_pending_age
publish_latency
```

### Platform

```text
service readiness
service restarts
queue ready/unacked
```

---

# 15. Compose/platform contract

## Profiles

```text
core
obs
fault
perf
ha
```

### `core`

- services;
- PostgreSQL;
- Redis;
- one RabbitMQ.

### `obs`

Adds:

- OTel Collector;
- Prometheus;
- Grafana;
- one trace backend.

### `fault`

Enables testing-only fault controls.

### `perf`

Adds load/evidence helpers.

### `ha`

Three RabbitMQ nodes + critical quorum queues.

---

# 16. Faultpoint platform contract

Conceptual API:

```csharp
public interface IFaultPoint
{
    Task HitAsync(string name, CancellationToken ct);
}
```

Possible names:

```text
Consumer.AfterBusinessCommitBeforeAck
Outbox.AfterPublishBeforeMark
Payment.AfterProviderEffectBeforeResponse
Saga.AfterPersistBeforeDispatch
Driver.AfterReserveCommitBeforeResponse
```

Production implementation:

```text
no-op / unavailable
```

Test implementation may:

- block;
- signal harness;
- throw;
- terminate process;
- suppress response.

Faultpoint must have bounded timeout.

---

# 17. Personal evidence map

```text
evidence/member-c/
├─ w02-platform/
├─ w03-payment/
├─ w06-payment-idempotency/
├─ w08-rabbitmq/
├─ w09-outbox-dedup/
├─ w10-saga-payment/
├─ w11-fault-platform/
├─ w12-observability-security/
├─ w14-reconnect-quorum/
└─ final/
```

---

# 18. C's primary test ownership

## Functional

```text
FUN-07 Payment authorization
FUN-10 Payment capture
Notification happy delivery
```

## Concurrency

```text
CON-03 concurrent same-operationKey Payment
```

## Reliability

```text
REL-04 Outbox crash before publish [shared A]
REL-05 publish accepted / mark lost
REL-06 consumer commit / crash before ACK
REL-07 Saga restart support
REL-08 Payment timeout-after-effect
REL-09 Payment terminal failure / compensation support
REL-10 capture failure after completion support
REL-11 poison message → retry → DLQ
REL-15 outage/backlog recovery
REL-16 RabbitMQ one-node quorum loss
REL-18 Outbox relay restart
```

## Security/platform

```text
SEC-01 auth failure coordination
SEC-06 idempotency key conflict
SEC-08 DB credential isolation coordination
SEC-09 secret scan
```

---

# 19. Capacity map

| Week | Planned C hours | Primary focus |
|---:|---:|---|
| W1 | 8 | messaging/payment/reliability comprehension |
| W2 | 9 | Compose/Rabbit/OTel/platform foundation |
| W3 | 9 | Payment FSM/simulator |
| W4 | 9 | MessageEnvelope/topology/security baseline |
| W5 | 9 | Payment idempotency |
| W6 | 9 | concurrent Payment proof |
| W7 | 9 | retry/DLQ/UNKNOWN support |
| W8 | 12 | RabbitMQ confirms/ACK |
| W9 | 11 | dedup + Outbox relay |
| W10 | 11 | Payment + Saga integration |
| W11 | 10 | deterministic fault platform |
| W12 | 11 | observability/security/Core Gate |
| W13 | 8 | performance support |
| W14 | 9 | reconnect/backlog/quorum |
| W15 | 9 | freeze/report/evidence |
| W16 | 9 | release/demo/defense |
| **Total** | **151 h** | — |

---

# 20. Week 1 — Understand the delivery model before writing infrastructure
**Planned: 8 h**

## 20.1 Objective

C can explain:

- Payment operation identity;
- publisher confirms;
- manual ACK;
- Outbox;
- dedup;
- retry/DLQ;
- automatic recovery limitations;
- observability boundary.

---

## 20.2 Required project reading

```text
00:
RabbitMQ/Payment/OTel/Compose decisions
failure model
at-least-once claim

01:
DSI-024..029
DSI-034
DSL-004/006/007

02:
MessageEnvelope
topology
confirm/ACK
dedup
Outbox
retry/DLQ
Saga Payment interaction
automatic recovery

03:
CON-03
REL-04..11
REL-15/16/18
SEC/PERF evidence

04:
C ownership and W8–W14 critical path
```

External:

- RabbitMQ confirms;
- RabbitMQ .NET client recovery;
- quorum queues;
- OTel messaging semantics;
- .NET resilience;
- idempotent request production analogy.

---

## 20.3 Issue pack

### C-W01-01 Draw four crash windows · 1.5 h

```text
DB commit → publish
publish → confirm/mark
consumer commit → ACK
Payment effect → response
```

For each:

```text
what durable state exists?
what can repeat?
what identity survives?
```

### C-W01-02 Build identity table · 1 h

```text
messageId
operationKey
reservationId
correlationId
traceId
```

### C-W01-03 Draw Payment FSM · 1 h

### C-W01-04 Draw Rabbit ownership-transfer diagram · 1 h

### C-W01-05 Write retry taxonomy · 1 h

### C-W01-06 Build personal evidence/GitHub view · 0.75 h

### C-W01-07 Team crash-window walkthrough · 1.25 h

### C-W01-08 90-second defense note · 0.5 h

---

## 20.4 Exit

A/B agree C's definitions of:

```text
confirm
ACK
dedup
Outbox
operationKey
UNKNOWN
```

---

# 21. Week 2 — Reproducible platform foundation
**Planned: 9 h**

## 21.1 Objective

Clean clone boots platform without manual RabbitMQ UI configuration.

---

## 21.2 Issue pack

### C-W02-01 Implement Compose `core` profile · 1.5 h

Include:

```text
postgres
redis
rabbitmq
services
```

### C-W02-02 Create per-service DB roles/isolation · 1 h

Support SEC-08.

### C-W02-03 RabbitMQ topology bootstrap skeleton · 1 h

Versioned declarations.

### C-W02-04 OTel Collector baseline · 1 h

### C-W02-05 Trace backend + Prometheus/Grafana minimal `obs` · 1 h

### C-W02-06 Health/readiness conventions · 0.75 h

### C-W02-07 Service instance identity · 0.5 h

### C-W02-08 CI infrastructure smoke · 0.75 h

### C-W02-09 `reset-state`/seed script conventions · 0.75 h

### C-W02-10 Clean-clone peer test · 0.75 h

---

## 21.3 Platform rule

Compose `depends_on`/health sequencing is startup convenience.

It is **not** runtime resilience.

Applications still need to handle dependency loss after startup.

---

## 21.4 Exit

```text
docker compose up
```

from clean clone produces reproducible core platform.

---

# 22. Week 3 — Payment aggregate and deterministic simulator
**Planned: 9 h**

## 22.1 Objective

Build Payment authority before RabbitMQ workflow integration.

---

## 22.2 Issue pack

### C-W03-01 Implement PaymentOperation aggregate · 1.5 h

### C-W03-02 Implement operation types · 0.5 h

```text
AUTHORIZE
CAPTURE
VOID
```

### C-W03-03 Implement local operation FSM · 1 h

### C-W03-04 Implement deterministic simulator · 1.5 h

Modes:

```text
SUCCESS
DECLINE
TRANSIENT_FAILURE
TIMEOUT_BEFORE_EFFECT
TIMEOUT_AFTER_EFFECT
SLOW_SUCCESS
```

### C-W03-05 Persistence mapping/migration · 1 h

### C-W03-06 Legal/illegal transition tests · 1 h

### C-W03-07 Provider-effect state tests · 1 h

### C-W03-08 A review Payment semantics · 0.75 h

### C-W03-09 Evidence · 0.75 h

---

## 22.3 Exit

Payment can be tested entirely locally with deterministic outcomes.

---

# 23. Week 4 — MessageEnvelope, topology and security baseline
**Planned: 9 h**

## 23.1 Objective

Freeze shared integration-message shape and reproducible topology before many consumers exist.

---

## 23.2 Issue pack

### C-W04-01 Implement `MessageEnvelope` · 1 h

### C-W04-02 Implement W3C trace header propagation helpers · 0.75 h

### C-W04-03 Freeze message versioning convention · 0.75 h

### C-W04-04 Declare `ride.commands` / `ride.events` · 0.75 h

### C-W04-05 Declare core queues/bindings · 1 h

### C-W04-06 Configure critical queue durability/persistence · 0.75 h

### C-W04-07 Build topology idempotent startup test · 1 h

Restart declaration:

```text
same semantics
```

### C-W04-08 Begin secrets/config policy · 0.75 h

### C-W04-09 DB role isolation test support · 0.75 h

### C-W04-10 A/B contract review · 1.5 h

---

## 23.3 Important rule

A service can only publish authoritative event about a fact it owns.

Examples:

```text
PaymentAuthorizedV1 → Payment
DriverReservedV1    → Driver
```

C owns contract mechanics, not event authority.

---

# 24. Week 5 — Payment idempotency and semantic stability
**Planned: 9 h**

## 24.1 Objective

One logical Payment step stays one effect under replay.

---

## 24.2 Issue pack

### C-W05-01 Add unique `operationKey` · 1 h

### C-W05-02 Add request fingerprint · 1 h

### C-W05-03 Same-key equivalent request returns canonical result · 1 h

### C-W05-04 Same-key conflicting request returns conflict · 1 h

### C-W05-05 Duplicate Authorize tests · 1 h

### C-W05-06 Duplicate Capture tests · 1 h

### C-W05-07 Duplicate Void tests · 0.75 h

### C-W05-08 Crash/restart persistence test · 1 h

### C-W05-09 A review operation lineage · 0.75 h

### C-W05-10 Evidence · 0.5 h

---

## 24.3 Production analogy

Stripe's published API docs use idempotency keys so a client can safely retry after connection errors without accidentally performing the same logical operation twice.

This validates the general pattern.

Project semantics remain defined by `operationKey`.

---

## 24.4 Exit

`DSI-024/025` green locally.

---

# 25. Week 6 — Concurrent Payment same-key proof
**Planned: 9 h**

## 25.1 Objective

Prove Payment idempotency under real concurrent execution, not only sequential replay.

---

## 25.2 Issue pack

### C-W06-01 Build synchronized CON-03 harness · 1.5 h

### C-W06-02 Run N=10 · 0.75 h

### C-W06-03 Run N=50 · 0.75 h

### C-W06-04 Run N=100 · 0.75 h

### C-W06-05 Assert one logical operation row/effect · 1 h

### C-W06-06 Add DB uniqueness/conflict defense · 1 h

### C-W06-07 Verify all callers converge to canonical result · 0.75 h

### C-W06-08 Record P50/P95 only as supporting evidence · 0.5 h

### C-W06-09 A review · 0.75 h

### C-W06-10 Evidence bundle · 1.25 h

---

## 25.3 Exit

```text
LogicalEffectCount(operationKey) = 1
```

for concurrent duplicate callers.

---

# 26. Week 7 — Retry taxonomy, poison handling and Payment UNKNOWN
**Planned: 9 h**

## 26.1 Objective

Build finite, classified recovery behavior before core broker integration becomes complex.

---

## 26.2 Issue pack

### C-W07-01 Implement shared failure classification · 1 h

### C-W07-02 Implement bounded retry metadata · 1 h

### C-W07-03 Implement DLQ/error envelope · 1 h

### C-W07-04 Implement unsupported-schema poison test · 1 h

### C-W07-05 Implement `TIMEOUT_AFTER_EFFECT` Payment test · 1.25 h

### C-W07-06 Same-operationKey reconciliation · 1 h

### C-W07-07 Configure .NET resilience selectively · 0.75 h

### C-W07-08 Review Matching retry with B · 0.75 h

### C-W07-09 Review Saga retry with A · 0.75 h

### C-W07-10 Evidence · 0.5 h

---

## 26.3 .NET resilience rule

Current .NET resilience packages provide:

- retry;
- total timeout;
- attempt timeout;
- circuit breaker;
- concurrency/rate limiting.

Framework capabilities do not define business idempotency.

C must avoid stacking:

```text
broker retry
+ Saga retry
+ HTTP retry
+ provider retry
```

without an explicit owner.

---

## 26.4 Exit

Payment UNKNOWN is resolvable with the same key; poison work cannot loop forever.

---

# 27. Week 8 — RabbitMQ backbone: confirms, ACK and topology
**Planned: 12 h**

## 27.1 Objective

Implement the messaging reliability semantics that the whole team will reuse.

---

## 27.2 Issue pack

### C-W08-01 Implement connection/channel lifecycle · 1 h

### C-W08-02 Enable publisher confirms · 1.5 h

### C-W08-03 Implement mandatory/unroutable handling where required · 0.75 h

### C-W08-04 Implement manual-ACK consumer base · 1.5 h

### C-W08-05 Implement consumer prefetch config · 0.5 h

### C-W08-06 Implement message validation/version handling · 0.75 h

### C-W08-07 Implement trace/correlation extraction/injection · 1 h

### C-W08-08 Real RabbitMQ integration tests · 1.25 h

### C-W08-09 Confirm-vs-ACK demonstration test · 0.75 h

### C-W08-10 Connection-loss smoke · 0.75 h

### C-W08-11 A/B crash-window review · 1 h

### C-W08-12 Evidence · 1.25 h

---

## 27.3 Publisher confirm semantics

RabbitMQ 4.3 states that publisher confirms cover publisher-to-broker communication and are independent from consumer ACKs.

For critical queues:

```text
publish
→ wait/track confirm
→ mark local publication accepted
```

---

## 27.4 Consumer semantics

```text
receive
→ durable local processing
→ ACK
```

If process dies after commit before ACK:

```text
redelivery
```

must be expected.

---

## 27.5 Exit G2 foundation

A/B can use one canonical messaging contract without inventing their own ACK/confirm rules.

---

# 28. Week 9 — Processed-message dedup + shared Outbox relay
**Planned: 11 h**

## 28.1 Objective

Make duplicate delivery/publication harmless and durable outgoing intent transferable.

---

## 28.2 Issue pack

### C-W09-01 Implement `processed_messages` support · 1 h

### C-W09-02 Make dedup marker atomic with local effect · 1.25 h

### C-W09-03 Build commit-before-ACK integration harness · 1 h

### C-W09-04 Implement shared Outbox claim/publish/confirm/mark · 1.5 h

### C-W09-05 Implement multi-relay claim semantics · 1 h

### C-W09-06 REL-05 publish accepted / mark lost · 1.25 h

### C-W09-07 REL-06 consumer commit / ACK lost · 1.25 h

### C-W09-08 REL-18 relay restart · 1 h

### C-W09-09 Outbox/dedup metrics · 0.75 h

### C-W09-10 A/B peer rerun · 0.75 h

### C-W09-11 Evidence · 0.75 h

---

## 28.3 Automatic recovery limitation

RabbitMQ .NET client can restore topology and consumers, but its current guide says messages published while connection is down are not internally buffered for later delivery.

Therefore:

```text
Outbox relay owns durable pending publication
```

---

## 28.4 Exit G2 complete

```text
duplicate publication
duplicate delivery
relay restart
commit-before-ACK crash
```

all have safe behavior.

---

# 29. Week 10 — Payment ↔ persisted Saga integration
**Planned: 11 h**

## 29.1 Objective

Integrate C's Payment authority into A's durable Saga without coupling Payment to Trip internals.

---

## 29.2 Issue pack

### C-W10-01 Consume `AuthorizePaymentV1` · 1 h

### C-W10-02 Publish `PaymentAuthorizedV1` · 0.75 h

### C-W10-03 Publish terminal authorization failure · 0.75 h

### C-W10-04 Consume/handle `VoidPaymentAuthorizationV1` · 0.75 h

### C-W10-05 Consume/handle `CapturePaymentV1` · 0.75 h

### C-W10-06 Publish capture success/pending/review · 1 h

### C-W10-07 Same `operationKey` across redelivery/restart · 1 h

### C-W10-08 Integrate REL-09 with A · 1.25 h

### C-W10-09 Integrate REL-10 with A · 1 h

### C-W10-10 Full happy ride Payment leg · 1 h

### C-W10-11 Cross-review with A · 0.75 h

### C-W10-12 Evidence · 0.75 h

---

## 29.3 Authority rule

Payment publishes:

```text
PaymentAuthorizedV1
PaymentAuthorizationFailedV1
PaymentCapturedV1
PaymentRequiresReviewV1
```

Trip decides how those facts affect:

```text
Trip FSM / Saga
```

Payment must not update Trip DB.

---

## 29.4 Exit

One logical Payment step remains one effect through Saga retries and duplicate delivery.

---

# 30. Week 11 — Deterministic fault platform
**Planned: 10 h**

## 30.1 Objective

Eliminate timing luck from the team's reliability proofs.

---

## 30.2 Issue pack

### C-W11-01 Implement faultpoint abstraction · 1 h

### C-W11-02 Environment-gate activation · 0.75 h

### C-W11-03 Implement `Consumer.AfterBusinessCommitBeforeAck` · 1 h

### C-W11-04 Implement `Outbox.AfterPublishBeforeMark` · 1 h

### C-W11-05 Implement `Payment.AfterProviderEffectBeforeResponse` · 1 h

### C-W11-06 Integrate A/B named faultpoints · 1 h

### C-W11-07 Harness signals/barriers · 1 h

### C-W11-08 Bounded wait/timeouts · 0.5 h

### C-W11-09 Run REL-05/06/08 five times · 1.25 h

### C-W11-10 Peer rerun · 0.75 h

### C-W11-11 Evidence · 0.75 h

---

## 30.3 Faultpoint rules

Must not:

- exist as unauthenticated production API;
- alter domain semantics;
- require debugger timing;
- block forever.

---

## 30.4 Exit

A/B can deterministically reproduce C-owned failure windows.

---

# 31. Week 12 — Observability, security and Core Release Gate
**Planned: 11 h**

## 31.1 Objective

Make delivery/recovery diagnosable and close platform/security P0 before performance.

---

## 31.2 Issue pack

### C-W12-01 Finalize OTel instrumentation baseline · 1 h

Use current supported project packages/config.

### C-W12-02 Messaging propagation validation · 0.75 h

OTel messaging semantic conventions remain Development.

Record/pin instrumentation behavior.

### C-W12-03 Build Rabbit dashboard · 1 h

Minimum:

```text
queue ready
unacked
redelivery
retry
DLQ
```

### C-W12-04 Build Outbox/consumer dashboard · 1 h

### C-W12-05 Build Payment dashboard · 0.75 h

### C-W12-06 Run SEC auth/role baseline · 1 h

### C-W12-07 Run DB isolation SEC-08 · 0.75 h

### C-W12-08 Secret scan/config review · 0.75 h

### C-W12-09 Run C full regression · 1.25 h

### C-W12-10 G4 review · 1 h

### C-W12-11 Explain C stack to A/B · 0.75 h

### C-W12-12 Evidence gap repair · 1 h

---

## 31.3 Current observability baseline

As of the project date:

```text
OpenTelemetry .NET core 1.17.0
```

is the latest core release line discovered during the documentation refresh.

Exact package versions remain pinned in the repo.

---

## 31.4 C's G4 checklist

```text
[ ] CON-03
[ ] REL-04 support
[ ] REL-05
[ ] REL-06
[ ] REL-07 support
[ ] REL-08
[ ] REL-09 support
[ ] REL-10 support
[ ] REL-11
[ ] REL-18
[ ] manual ACK
[ ] publisher confirms
[ ] durable dedup
[ ] finite retry/DLQ
[ ] stable operationKey
[ ] no secret leakage
[ ] zero S0/S1 C-owned defect
```

---

# 32. Week 13 — Performance support without semantic drift
**Planned: 8 h**

## 32.1 Objective

Support B's E01–E03 without tuning away correctness.

---

## 32.2 Issue pack

### C-W13-01 Freeze Rabbit prefetch/config for experiment baseline · 0.75 h

### C-W13-02 Support E03 consumer replica counts · 1 h

### C-W13-03 Capture queue ready/unacked metrics · 0.75 h

### C-W13-04 Capture consumer processing/redelivery/dedup · 0.75 h

### C-W13-05 Capture Payment DB/consumer metrics · 0.75 h

### C-W13-06 Validate no retry-config changes across compared runs · 0.75 h

### C-W13-07 Review system-error vs domain-conflict categorization · 0.75 h

### C-W13-08 Support 3–5 measured runs · 1 h

### C-W13-09 Preserve raw broker/OTel evidence · 0.75 h

### C-W13-10 Draft messaging performance analysis · 0.75 h

---

## 32.3 Rule

Do not change simultaneously:

```text
consumer count
prefetch
DB pool
retry policy
```

and then attribute improvement to one factor.

---

## 32.4 Exit

B has broker/application metrics needed to explain E03.

---

# 33. Week 14 — Reconnect, backlog and RabbitMQ quorum validation
**Planned: 9 h**

## 33.1 Objective

Close the final platform-reliability evidence.

---

## 33.2 Issue pack

### C-W14-01 Build 3-node RabbitMQ `ha` profile · 1.5 h

Current family:

```text
RabbitMQ 4.3.x
```

As of 2026-09-26, RabbitMQ release information lists 4.3.6 as the latest 4.3 patch.

### C-W14-02 Critical queues as quorum queues · 1 h

### C-W14-03 Validate publisher confirm behavior · 1 h

### C-W14-04 Validate manual consumer ACK/redelivery · 0.75 h

### C-W14-05 REL-16 kill one node · 1.25 h

### C-W14-06 Restore/rejoin/recovery observation · 0.75 h

### C-W14-07 REL-15 network/broker interruption · 1 h

### C-W14-08 Validate .NET automatic recovery behavior · 0.75 h

### C-W14-09 Outbox backlog/drain support for E04 · 0.5 h

### C-W14-10 Auditor/review with A/B · 0.5 h

---

## 33.3 Quorum claim

RabbitMQ documents quorum queues as replicated Raft-based queues and says publishers should use confirms while consumers should use manual acknowledgements for data safety.

Allowed report claim:

> Critical RabbitMQ queues were validated in a three-node quorum configuration under one-node loss while a majority remained.

Not allowed:

> The full application is highly available.

---

## 33.4 Exit G5-C

Quorum/reconnect evidence exists from release-grade topology.

---

# 34. Week 15 — Feature freeze, report and evidence
**Planned: 9 h**

## 34.1 Objective

Turn C's platform/reliability work into concise academic proof.

---

## 34.2 Issue pack

### C-W15-01 Freeze topology/retry semantics · 0.5 h

### C-W15-02 Clean-clone infrastructure boot · 1 h

### C-W15-03 Write confirm/ACK section · 1 h

### C-W15-04 Write dedup/Outbox relay section · 1.25 h

### C-W15-05 Write retry/DLQ section · 1 h

### C-W15-06 Write Payment idempotency section · 1.25 h

### C-W15-07 Write observability/security/HA claim boundaries · 1 h

### C-W15-08 Curate REL-05/06/08/11/16 diagrams · 1 h

### C-W15-09 Verify all source/version claims · 0.5 h

### C-W15-10 Final evidence manifest · 0.5 h

---

## 34.3 Report wording rule

Use:

> RabbitMQ transport was treated as at-least-once. Publisher confirms protected publication acceptance while manual consumer acknowledgements transferred responsibility only after local durable processing. Duplicate publication/redelivery remained possible and was handled by stable message identity, durable deduplication and domain idempotency.

Do not use:

> RabbitMQ guarantees exactly once.

---

# 35. Week 16 — Release, live demo and oral defense
**Planned: 9 h**

## 35.1 Objective

C independently demonstrates and defends the messaging/payment reliability model.

---

## 35.2 Issue pack

### C-W16-01 Release platform smoke · 0.75 h

### C-W16-02 Rehearse REL-06 commit-before-ACK demo 3x · 1.25 h

Live sequence:

```text
deliver M
consumer commits
faultpoint pauses before ACK
kill consumer
broker redelivers
dedup hit
one business effect
ACK
```

### C-W16-03 Rehearse Payment timeout-after-effect backup · 1 h

### C-W16-04 Rehearse poison/DLQ proof · 0.75 h

### C-W16-05 Rehearse quorum evidence explanation · 0.75 h

### C-W16-06 Oral defense drill · 2 h

### C-W16-07 Review A/B handoffs · 0.75 h

### C-W16-08 Final tag/config/evidence verification · 0.75 h

### C-W16-09 Backup trace/screenshots/raw broker state · 0.5 h

### C-W16-10 Final secret/release-profile scan · 0.5 h

---

# 36. Questions C must answer from memory

1. What does Payment Service own?
2. What is an `operationKey`?
3. Why isn't `messageId` enough for Payment idempotency?
4. What does same-key conflicting payload mean?
5. Why can a timeout happen after Payment effect?
6. What is `TIMEOUT_AFTER_EFFECT` proving?
7. What is publisher confirm?
8. What is consumer ACK?
9. Why are they orthogonal?
10. Does publisher confirm mean consumer processed?
11. Does consumer ACK tell producer anything?
12. Why ACK after DB commit?
13. What happens if process crashes after commit before ACK?
14. Why is redelivery normal?
15. What does `processed_messages` protect?
16. Why must dedup marker be atomic with business effect?
17. Why dedup alone cannot reject every stale message?
18. What is Transactional Outbox?
19. What failure window does it close?
20. Why can Outbox publish duplicates?
21. Why must `messageId` remain stable on Outbox republish?
22. Why doesn't automatic RabbitMQ client recovery replace Outbox?
23. What happens to publishes during connection loss?
24. What is a poison message?
25. Why not infinite `requeue=true`?
26. What belongs in DLQ metadata?
27. What must happen before redrive?
28. What does retry ownership mean?
29. What is circuit breaker for?
30. Why doesn't breaker resolve UNKNOWN?
31. Why no side-effect hedging?
32. What is prefetch?
33. Why can too-high prefetch hurt recovery?
34. What is a quorum queue?
35. Why use publisher confirms with quorum queues?
36. Why manual ACK with quorum queues?
37. What does REL-16 prove?
38. What doesn't REL-16 prove?
39. What does OpenTelemetry own?
40. Why is telemetry not recovery state?
41. What is `correlationId` versus `traceId`?
42. Why are messaging semantic conventions not treated as business contract?
43. What does Notification failure do to Trip?
44. Which secrets/PII are forbidden in telemetry?
45. What would change in a production-grade deployment?

---

# 37. C ↔ A interface checklist

## Saga / Payment

```text
[ ] A creates stable operationKey
[ ] same Saga step reuses key
[ ] C rejects conflicting same-key payload
[ ] C publishes authoritative Payment result
[ ] A owns Trip transition
[ ] capture after completion only
[ ] capture failure does not regress Trip
```

## Outbox

```text
[ ] A creates semantic Outbox intent in same transaction
[ ] C relay preserves messageId
[ ] confirm required before published mark
[ ] duplicate publication accepted
[ ] consumer dedup/state guard exists
```

---

# 38. C ↔ B interface checklist

## Reserve HTTP resilience

```text
[ ] generic retry does not create new reservation identity
[ ] domain 409/conflict is not breaker failure
[ ] timeout can be UNKNOWN
[ ] B owns reconciliation
[ ] C resilience config does not override business semantics
```

## Matching/Driver messaging

```text
[ ] Driver events authoritative only from Driver
[ ] Matching terminal result durable
[ ] duplicate RideRequested safe
[ ] projection consumers use dedup + version guard
```

---

# 39. C's Definition of Done

## Payment operation

```text
[ ] stable operationKey
[ ] request fingerprint
[ ] unique persistence defense
[ ] duplicate replay
[ ] conflicting-key test
[ ] timeout-after-effect test where relevant
[ ] restart-safe
[ ] authoritative outcome event
```

## Consumer

```text
[ ] schema/version validation
[ ] dedup key
[ ] domain/current-state guard
[ ] local effect + dedup atomic
[ ] Outbox if resulting message required
[ ] commit before ACK
[ ] bounded failure path
[ ] fault proof
```

## Publisher/Outbox relay

```text
[ ] stable messageId
[ ] persistent critical message
[ ] publisher confirm
[ ] unroutable handling where required
[ ] no PUBLISHED before confirm
[ ] restart/duplicate safe
[ ] metrics
```

## Rabbit queue

```text
[ ] declared by code/config
[ ] owner known
[ ] queue type known
[ ] routing known
[ ] retry/DLQ known
[ ] no Management-UI-only setup
```

## Faultpoint

```text
[ ] testing-only
[ ] deterministic
[ ] bounded
[ ] signal to harness
[ ] production profile disabled
```

---

# 40. Common mistakes C must prevent

## Mistake 1 — “publisher confirm = processing complete”

Wrong.

Confirm stops at broker acceptance.

## Mistake 2 — auto-ACK

Can lose effect after consumer crash.

## Mistake 3 — ACK before DB commit

Same risk.

## Mistake 4 — dedup marker committed separately from effect

Can suppress real work.

## Mistake 5 — Outbox marked published before confirm

Can silently lose outgoing intent.

## Mistake 6 — assuming auto-recovery buffers publishes

RabbitMQ .NET client does not.

## Mistake 7 — infinite requeue loop

Creates hot poison loop.

## Mistake 8 — every error goes to retry

Validation/domain conflict may be conclusive.

## Mistake 9 — new Payment key on retry

Creates new logical effect.

## Mistake 10 — circuit breaker treated as business outcome

Breaker only protects resources.

## Mistake 11 — traceId used as idempotency key

Trace is diagnostic.

## Mistake 12 — HA overclaim

Three Rabbit nodes do not make PostgreSQL/Redis HA.

## Mistake 13 — observability stack becomes huge

One coherent stack is enough.

## Mistake 14 — topology configured by hand in RabbitMQ UI

Not reproducible.

---

# 41. Blocker escalation

Immediate:

- ACK-before-commit proposed/observed;
- Outbox row marked published without conclusive broker acceptance;
- duplicate Payment creates second effect;
- new operationKey generated for same Saga step retry;
- automatic recovery assumed to persist outgoing messages;
- poison message loops indefinitely;
- critical queue exists only manually;
- trace/log required to resume business state;
- broker HA test is being reported as full-system HA.

Same day:

- A/C disagree on operationKey semantics;
- B/C retry policies overlap;
- same message type has conflicting producer ownership;
- dedup retention insufficient;
- relay claim semantics allow lost rows.

Template:

```text
Observed:
Boundary:
Message type:
messageId:
operationKey:
DB state:
Outbox state:
Broker state:
ACK/confirm state:
Retry count:
Failure class:
Affected DSI/DSL:
Smallest reproduction:
Decision required:
Owner:
Deadline:
```

---

# 42. Personal risk register

| Risk | Impact | Control |
|---|---|---|
| duplicate Payment effect | critical | operationKey + unique defense + CON-03 |
| ACK-before-commit loss | critical | manual ACK + REL-06 |
| DB/publish dual-write loss | critical | Outbox + REL-04 |
| lost confirm causes duplicate publish | high | stable messageId + REL-05 |
| infinite retry storm | high | finite classification + DLQ |
| poison starves healthy messages | high | delayed retry/DLQ |
| broker reconnect assumed lossless | high | Outbox + REL-15 |
| Payment unknown misclassified | critical | TIMEOUT_AFTER_EFFECT |
| telemetry leaks secrets | high | safe-field policy/SEC |
| Rabbit quorum overclaim | medium | narrow wording |
| C is only messaging expert | high | A/B walkthrough/rerun |

---

# 43. Personal report/defense pack

By W16 C should have:

1. MessageEnvelope diagram.
2. publisher-confirm vs consumer-ACK diagram.
3. consumer commit-before-ACK timeline.
4. processed-message schema.
5. REL-06 evidence.
6. Outbox relay state/timeline.
7. REL-05 evidence.
8. Outbox restart evidence.
9. retry classification table.
10. DLQ/poison evidence.
11. Payment FSM.
12. `operationKey`/request-fingerprint rationale.
13. CON-03 evidence.
14. `TIMEOUT_AFTER_EFFECT` evidence.
15. Saga-Payment integration sequence.
16. Notification independence proof.
17. OTel trace/correlation diagram.
18. safe telemetry fields list.
19. Rabbit reconnect evidence.
20. three-node quorum evidence.
21. release topology.
22. security/config limitations.

---

# 44. C's strongest 90-second explanation

C should be able to say:

> RabbitMQ is treated as an at-least-once transport, not an exactly-once execution engine. A publisher confirm only proves broker-side acceptance, while a consumer acknowledgement transfers responsibility back to RabbitMQ only after the consumer has durably processed the message. Therefore critical consumers commit business state, dedup state and any resulting Outbox intent before ACK. If the process crashes after commit but before ACK, RabbitMQ can redeliver and the stable `messageId` is deduplicated. Required outgoing messages use a Transactional Outbox because RabbitMQ automatic connection recovery does not durably buffer application publications made during an outage. Payment adds another stable identity, `operationKey`, so duplicate or uncertain requests cannot create a second logical charge. Tracing explains the flow, but all recovery decisions come from durable business and messaging state.

Every sentence should map to:

```text
code
DSI/DSL
REL/CON scenario
authoritative final-state assertion
```

---

# 45. Authoritative implementation-learning references

Project Docs 00–04 remain canonical. These sources validate external mechanism behavior.

## RabbitMQ

### Consumer Acknowledgements and Publisher Confirms

https://www.rabbitmq.com/docs/confirms

Use for:

- ACK vs confirm;
- publisher acceptance;
- consumer ownership transfer;
- redelivery reasoning.

### .NET/C# Client API Guide

https://www.rabbitmq.com/client-libraries/dotnet-api-guide

Use for:

- automatic recovery;
- topology recovery;
- connection failure;
- important fact that outgoing publishes are not internally buffered for later delivery.

### Quorum Queues

https://www.rabbitmq.com/docs/quorum-queues

Use for:

- Raft-based replication;
- confirm semantics;
- manual ACK guidance;
- poison/delayed retry;
- final one-node-loss validation.

### Release Information

https://www.rabbitmq.com/release-information

Use for:

- pinned current 4.3.x patch check before final freeze.

---

## .NET resilience

### Introduction to resilient app development

https://learn.microsoft.com/en-us/dotnet/core/resilience/

### HTTP resilience

https://learn.microsoft.com/en-us/dotnet/core/resilience/http-resilience

Use for:

- timeout;
- retry;
- circuit breaker;
- rate/concurrency limiting;
- resilience pipelines.

Business idempotency/reconciliation remains project-owned.

---

## OpenTelemetry

### Messaging semantic conventions

https://opentelemetry.io/docs/specs/semconv/messaging/

Current status:

```text
Development
```

### OpenTelemetry .NET releases

https://github.com/open-telemetry/opentelemetry-dotnet/releases

Current documentation refresh found:

```text
core 1.17.0
```

released July 2026.

---

## Payment idempotency analogy

### Stripe — Idempotent requests

https://docs.stripe.com/api/idempotent_requests

Use as an industry analogy for safe retry with stable operation identity.

It does not define this project's Payment contract.

---

# 46. Weekly operating checklist

At week start:

```text
[ ] previous gate green?
[ ] Payment/messaging boundary?
[ ] stable operation/message identity?
[ ] durable state before failure?
[ ] ACK/confirm distinction?
[ ] retry owner?
[ ] finite failure path?
[ ] exact REL/CON proof?
[ ] reviewer?
```

For every consumer:

```text
[ ] schema valid?
[ ] duplicate possible?
[ ] stale state possible?
[ ] effect + dedup atomic?
[ ] outgoing intent requires Outbox?
[ ] commit before ACK?
[ ] retry/DLQ classification?
```

For every publisher:

```text
[ ] durable intent?
[ ] stable messageId?
[ ] persistent critical message?
[ ] confirm?
[ ] unroutable path?
[ ] duplicate publication safe?
```

Before closing issue:

```text
[ ] happy path
[ ] duplicate path
[ ] crash path
[ ] reconnect/restart path if relevant
[ ] final authoritative state
[ ] evidence
[ ] peer rerun
[ ] actual hours
```

---

# 47. Final north star

> **Whenever C is unsure whether Payment/messaging/platform work is complete, ask: if the connection drops, the message is published twice, the consumer commits then dies before ACK, the Payment effect happens before the response is lost, and the broker later reconnects on another node, does durable state still make every logical effect occur at most once and every unresolved operation visible and recoverable without consulting a trace?**

If not, C's subsystem is not complete.
