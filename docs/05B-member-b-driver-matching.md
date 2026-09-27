# DS03 DISTRIBUTED RIDE-HAILING SYSTEM — MEMBER B EXECUTION PLAYBOOK
## Driver Authority · Vehicle/Location · Redis GEO · Matching · OCC · UNKNOWN Reconciliation · Performance

> **Document:** `docs/05B-member-b-driver-matching.md`  
> **Course:** Distributed Systems  
> **Topic:** DS03 — Ride-hailing Microservices System simulating Grab/Uber  
> **Type:** Individual Execution Playbook / 16-week chronological delivery guide  
> **Status:** **PROPOSED FREEZE v2.0 — REVIEW CANDIDATE**  
> **Baseline date:** **2026-09-26**  
> **Member:** **B**  
> **Primary role:** Driver/Vehicle/Location Service · authoritative Driver state · reservation OCC · fencing · Redis GEO derived projection · Matching Service · MatchingRound/MatchingAttempt · candidate ranking · UNKNOWN reconciliation · concurrency proofs · performance experiments  
> **Secondary role:** reviewer for Trip assignment semantics, Saga compensation fencing, RabbitMQ consumer behavior, invariant-auditor correctness and experiment methodology  
> **Planning horizon:** 16 weeks  
> **Physical planning envelope:** **~161 focused hours**  
> **Average:** ~10.1 focused h/week  
> **Heavy weeks:** W6–W7 and W13  
> **Normative hierarchy:** `00 → 01 → 02 → 03 → 04 → 05B`  
> **Execution rule:** this playbook refines task order, theory, implementation detail, test strategy and evidence for Member B. It MUST NOT redefine authority, invariants, delivery semantics or release gates frozen in Docs 00–04.

> **Member B north star.**  
> **Candidate discovery may be stale, requests may race, and reserve responses may disappear. The Driver authority must still commit at most one legal current reservation, stale cleanup must never damage newer work, and Matching must never “move on” while an earlier remote effect is still unknown.**

---

# 0. How B should use this playbook

At the start of every issue, B should answer:

1. Which authoritative state decides the result?
2. Which part is only a derived/read-model hint?
3. What is the linearization point?
4. What stable identity survives retry/restart?
5. What happens if the response disappears after commit?
6. What stale input can arrive later?
7. What final database assertion proves the invariant?
8. What experiment/test makes the race reproducible?

Each week follows:

```text
objective
→ theory/knowledge
→ issue pack
→ implementation order
→ race/failure windows
→ tests/evidence
→ review/handoff
→ exit criteria
```

Do not use a faster cache/read path to bypass authoritative correctness.

---

# 1. Member B role contract

## 1.1 What B owns

| Area | B owns | B must not silently own |
|---|---|---|
| Driver aggregate | `OFFLINE / AVAILABLE / RESERVED / ON_TRIP`, current reservation, version | Trip lifecycle |
| Vehicle | operational association/capability baseline | fare/product policy |
| Location ingress | coordinate/session/sequence/freshness validation | production navigation/maps |
| Reservation | OCC/conditional mutation, same-reservation idempotency | Trip assignment authority |
| Fencing | exact `reservationId` release/start/complete guards | best-effort Driver-ID-only cleanup |
| Redis GEO | derived candidate projection, GEO index, freshness/status/version metadata | Driver authority |
| Matching | durable `MatchingRound` + attempts, candidate discovery/ranking | direct Driver DB writes |
| UNKNOWN | reserve timeout classification and same-identity reconciliation | treating timeout as failure |
| Concurrency evidence | barriers, synchronized races, DB final-state oracles | screenshot-only evidence |
| Performance | E01–E04 methodology, k6/load harness, run metadata | production-scale overclaim |
| Report | Driver/Matching/OCC/Redis/performance sections | rewriting canonical Docs 00–03 alone |

---

## 1.2 B's canonical correctness contract

B is primary owner or co-owner of:

```text
DSI-001  Exclusive Driver Reservation
DSI-002  Driver State/Reservation Coherence
DSI-003  Legal Driver Transition
DSI-004  Guarded Reservation Release
DSI-005  Reserve Idempotency
DSI-006  Monotonic Accepted Location
DSI-007  Projection Version Non-regression

DSI-015  One Terminal Matching Outcome per Round
DSI-016  Current-Round Guard
DSI-017  Unresolved Attempt Blocks Next Candidate
DSI-018  Attempt Identity Stability
DSI-019  Deterministic Candidate Ordering
DSI-020  Eligibility Before Ranking
DSI-021  Stage Monotonicity
DSI-022  No Stage Advance on UNKNOWN
DSI-023  No Endless Fresh Retry of Conclusive Candidate

DSI-030  Driver–Trip Assignment Agreement
DSI-031  No New Driver Before UNKNOWN Resolved
DSI-033  Exact Cleanup Identity
```

B is primary for liveness:

```text
DSL-001  Reservation Reconciliation Terminates
DSL-003  Matching Round Terminates
```

and co-owns:

```text
DSL-002  Pre-start Reservation Cleanup
```

---

## 1.3 B's five defense anchors

B must be able to explain without opening code:

### 1. Authority versus derived read model

```text
Redis says candidate
Driver PostgreSQL decides reservation
```

Stale Redis may waste work.

It must not violate safety.

### 2. Optimistic concurrency / conditional reservation

Two or 100 callers can observe a Driver as available.

Only one guarded mutation may become authoritative.

### 3. Linearization point

The winning Driver reservation becomes authoritative at the successful Driver database commit, not at:

```text
Redis lookup
Matching selection
HTTP request send
client UI
```

### 4. UNKNOWN remote outcome

Timeout means:

```text
caller stopped waiting
```

not:

```text
remote definitely failed
```

Therefore Matching must reconcile the same `reservationId`.

### 5. Fencing

A delayed:

```text
Release(R_old)
```

must not free:

```text
R_new
```

---

# 2. Theory B must master

## 2.1 Optimistic concurrency

EF Core supports optimistic concurrency by tracking a concurrency token and failing an update if the database value changed since the row was read. This is useful only if the application maps the conflict into a legal domain result instead of blindly retrying until overwrite succeeds.

For Driver:

```text
expected state/version
+
guarded update
```

creates one winner.

---

## 2.2 PostgreSQL as the final concurrency authority

PostgreSQL's default transaction isolation is `READ COMMITTED`.

Therefore this pattern is unsafe:

```text
SELECT status='AVAILABLE'
→ application pauses
→ unconditional UPDATE
```

because another transaction may change the row between statements.

B should use:

- one conditional UPDATE;
- concurrency token;
- unique/partial-unique DB defense where useful;
- short transaction.

PostgreSQL supports unique indexes and partial unique indexes to enforce uniqueness on a subset of rows. This is valuable defense-in-depth, but the schema constraint must match the actual business invariant.

---

## 2.3 Redis GEO is a candidate index, not business truth

`GEOADD` stores geospatial members; `GEOSEARCH` finds members within a circle/box and can order/filter by distance.

Redis GEO answers:

> Which Drivers are plausible nearby candidates?

It does not answer:

> Is this Driver still legally reservable now?

That answer remains Driver Service/PostgreSQL.

---

## 2.4 Redis Functions for atomic projection updates

Redis Functions execute atomically on the Redis server.

That makes them useful for:

```text
compare version/freshness
→ update metadata
→ GEOADD/ZREM
```

as one short operation.

But atomic Redis projection does **not** create cross-service authority.

---

## 2.5 Stable reservation identity

For one logical reserve attempt:

```text
reservationId = R
```

must remain stable across:

- HTTP retry;
- reconciliation;
- service restart.

New `reservationId` means new logical attempt.

---

## 2.6 UNKNOWN is a state, not an exception label

If Matching sends:

```text
Reserve(D,R)
```

and times out, possible realities include:

```text
request never arrived
request arrived but did not commit
request committed and response was lost
response is delayed
```

Therefore:

```text
timeout → UNKNOWN
```

not:

```text
timeout → REJECTED
```

---

## 2.7 Why next candidate must wait

If D1 reserve is UNKNOWN and Matching immediately tries D2:

```text
D1 may already be reserved
D2 may also succeed
```

The system can create two Drivers for one Trip.

Therefore:

```text
UNKNOWN D1
→ reconcile D1
→ only then continue
```

---

## 2.8 Matching determinism

For a fixed:

```text
candidate snapshot
eligibility rules
ranking config
```

the ranking should be deterministic.

This makes:

- debugging;
- replay;
- experiments;
- defense

far stronger.

---

## 2.9 Performance methodology

k6's arrival-rate executors use an open model, where iterations start independently of response duration. This is appropriate when the question is:

> What happens under a fixed offered rate?

The closed model can reduce offered load as the SUT slows, creating coordinated-omission-style bias.

B must distinguish:

```text
offered load
achieved throughput
latency
dropped iterations
domain conflicts
system errors
```

---

# 3. B's recommended code shape

```text
src/
├─ DriverService/
│  ├─ Api/
│  │  ├─ DriverStatus/
│  │  ├─ ReserveDriver/
│  │  ├─ ReleaseReservation/
│  │  ├─ TripLifecycle/
│  │  └─ Location/
│  │
│  ├─ Domain/
│  │  ├─ Drivers/
│  │  ├─ Vehicles/
│  │  └─ Locations/
│  │
│  ├─ Application/
│  │  ├─ Reserve/
│  │  ├─ Release/
│  │  ├─ StartTrip/
│  │  ├─ CompleteTrip/
│  │  └─ Location/
│  │
│  ├─ Infrastructure/
│  │  ├─ Persistence/
│  │  ├─ Outbox/
│  │  └─ Messaging/
│  └─ Observability/
│
└─ MatchingService/
   ├─ Application/
   │  ├─ RideRequested/
   │  ├─ CandidateDiscovery/
   │  ├─ Reservation/
   │  └─ Reconciliation/
   ├─ Domain/
   │  ├─ MatchingRound.cs
   │  ├─ MatchingAttempt.cs
   │  └─ Ranking/
   ├─ Infrastructure/
   │  ├─ Persistence/
   │  ├─ RedisProjection/
   │  ├─ DriverClient/
   │  └─ Outbox/
   └─ Observability/

tests/
├─ DriverService.UnitTests/
├─ DriverService.IntegrationTests/
├─ MatchingService.UnitTests/
├─ MatchingService.IntegrationTests/
└─ SystemTests/
```

---

# 4. B's persistence model

## 4.1 Driver

Conceptual:

```text
drivers
- driver_id
- user_id
- status
- version

- reserved_trip_id?
- reservation_id?
- active_trip_id?

- vehicle_id?
- created_at
- updated_at
```

---

## 4.2 Vehicle

Minimal P0:

```text
vehicles
- vehicle_id
- driver_id
- plate
- vehicle_type
- active
```

No over-modeling.

---

## 4.3 MatchingRound

```text
matching_rounds
- trip_id
- matching_round
- input_message_id

- status
- current_stage
- cancel_requested

- ranking_config_version
- candidate_snapshot_hash
- selected_driver_id?
- selected_reservation_id?
- result_message_id?

- created_at
- updated_at
```

Suggested constraints:

```text
UNIQUE(trip_id, matching_round)
UNIQUE(input_message_id)
```

---

## 4.4 MatchingAttempt

```text
matching_attempts
- trip_id
- matching_round
- ordinal
- driver_id
- reservation_id
- state
- last_error_code?
- created_at
- updated_at
```

Suggested:

```text
UNIQUE(reservation_id)
UNIQUE(trip_id, matching_round, ordinal)
```

---

# 5. B's Redis projection shape

Conceptual metadata:

```text
driver:projection:{driverId}
  derivedStatus
  driverAggregateVersion
  longitude
  latitude
  locationSessionId
  sequenceNo
  capturedAt
```

Geospatial index:

```text
drivers:geo
```

Projection rule:

```text
state/version update
+
location freshness update
+
GEOADD/ZREM
```

performed atomically where practical via one short Redis Function.

---

# 6. B's contract map

## 6.1 Driver-produced authoritative events

```text
DriverAvailableV1
DriverOfflineV1
DriverReservedV1
DriverReservationReleasedV1
DriverTripStartedV1
DriverTripCompletedV1
DriverLocationAcceptedV1
```

Only Driver Service can authoritatively publish Driver state events.

---

## 6.2 Matching-produced events

```text
MatchingFailedV1
```

Matching does **not** publish:

```text
DriverReservedV1
```

because Matching does not own reservation truth.

---

## 6.3 Matching inputs

```text
RideRequestedV1
CancelMatchingRoundV1
Driver state/location events for Redis projection
```

---

# 7. B's primary evidence map

```text
evidence/member-b/
├─ w04-redis-projection/
├─ w05-ranking/
├─ w06-contention/
├─ w07-unknown-fencing/
├─ w08-rabbit-integration/
├─ w09-matching-outbox/
├─ w11-fault-proofs/
├─ w13-performance/
├─ w14-recovery-ha/
└─ final/
```

---

# 8. B's primary test ownership

## Functional

```text
FUN-02 Driver online/offline
FUN-03 Location accepted/discoverable
FUN-05 Matching happy path
FUN-11 Matching no Driver
```

## Concurrency

```text
CON-01 N Trips contend for one Driver
CON-02 GoOffline vs Reserve
CON-04 Stale compensation vs newer reservation
CON-05 Cancel vs Reserve in-flight (shared A)
CON-06 Duplicate Matching execution
```

## Reliability

```text
REL-01 reserve response lost after Driver commit
REL-02 Matching crash after remote reserve commit
REL-03 cancellation + unknown cleanup (shared A)
REL-12 stale Driver projection event
REL-13 stale location
REL-14 Redis unavailable
REL-15 backlog/recovery support
REL-16 RabbitMQ quorum validation support
REL-17 stale Driver event after terminal Trip review
```

## Performance

```text
PERF/E01 contention curve
PERF/E02 offered-load baseline
PERF/E03 async consumer scaling
PERF/E04 outage/backlog/recovery
```

---

# 9. Capacity map

| Week | Planned B hours | Primary focus |
|---:|---:|---|
| W1 | 8 | architecture/concurrency comprehension |
| W2 | 9 | Driver/Matching foundations |
| W3 | 9 | Driver FSM/location |
| W4 | 10 | Redis projection |
| W5 | 10 | deterministic Matching |
| W6 | 13 | OCC/contention |
| W7 | 12 | fencing/UNKNOWN |
| W8 | 10 | RabbitMQ integration |
| W9 | 10 | durable Matching/Outbox |
| W10 | 9 | Saga-facing Driver commands |
| W11 | 11 | deterministic fault campaign |
| W12 | 9 | observability/security/Core Gate |
| W13 | 14 | performance campaign |
| W14 | 8 | recovery/HA validation |
| W15 | 9 | freeze/report/evidence |
| W16 | 10 | release/demo/defense |
| **Total** | **161 h** | — |

---

# 10. Week 1 — Understand authority, race and failure semantics
**Planned: 8 h**

## 10.1 Objective

B can explain:

- Driver as reservation authority;
- Redis as derived view;
- OCC;
- fencing;
- MatchingRound;
- UNKNOWN;
- performance proof methodology.

---

## 10.2 Required project reading

Read:

```text
00:
authority boundaries
CAP-by-operation
OCC
fencing
Redis read model
Matching architecture

01:
DSI-001..007
DSI-015..023
DSI-030/031/033
DSL-001/003
DSR-001..008
DSR-015/016

02:
ReserveDriver sync boundary
MatchingRound persistence
reconciliation
Redis projection
retry ownership

03:
CON-01..06
REL-01..03/12..16
PERF/E01..E04

04:
B ownership and W6/W7/W13 critical path
```

External:

- EF Core optimistic concurrency;
- PostgreSQL transaction isolation;
- Redis GEO basics;
- k6 open vs closed model.

---

## 10.3 Issue pack

### B-W01-01 Build Driver authority map · 1.5 h

For each action:

```text
GoOnline
GoOffline
Reserve
Release
StartTrip
CompleteTrip
```

write:

```text
precondition
linearization point
postcondition
conflict
duplicate behavior
stale behavior
```

### B-W01-02 Draw Driver FSM and race matrix · 1.5 h

Include:

```text
Reserve vs Reserve
GoOffline vs Reserve
Release(old) vs Reserve(new)
```

### B-W01-03 Build Matching attempt lifecycle · 1.25 h

```text
READY → IN_FLIGHT → RESERVED / REJECTED / UNKNOWN
```

### B-W01-04 Write UNKNOWN explanation · 0.75 h

One page:

```text
why timeout ≠ failure
why same reservationId
why next candidate waits
```

### B-W01-05 Build performance question map · 1 h

E01–E04:

```text
question
independent variable
correctness oracle
```

### B-W01-06 GitHub/evidence setup · 0.75 h

### B-W01-07 A/C walkthrough · 1.25 h

---

## 10.4 Exit

B can answer:

> If Redis says D is available, two Trips race, one reserve response times out, and an old release arrives later, why can the authoritative final Driver state still remain legal?

without opening docs.

---

# 11. Week 2 — Driver and Matching service foundations
**Planned: 9 h**

## 11.1 Objective

Both services independently build/migrate/test with owned persistence.

---

## 11.2 Issue pack

### B-W02-01 Create DriverService project · 1 h

### B-W02-02 Create MatchingService project · 1 h

### B-W02-03 Driver PostgreSQL schema/migration baseline · 1 h

### B-W02-04 Matching PostgreSQL schema/migration baseline · 1 h

### B-W02-05 Redis connection/Testcontainers fixture · 1 h

### B-W02-06 Driver PostgreSQL integration fixture · 1 h

### B-W02-07 Health endpoints · 0.5 h

### B-W02-08 Freeze internal reserve/reconcile API shape with A/C · 1.25 h

### B-W02-09 Verify no cross-service DB access · 0.75 h

### B-W02-10 CI smoke · 0.5 h

---

## 11.3 Foundation rule

Matching may depend on:

```text
Driver HTTP contract
Redis
Matching DB
RabbitMQ
```

It may **not** depend on Driver DB credentials.

---

## 11.4 Exit

Clean clone:

```text
Driver migration
Matching migration
Redis fixture
health
CI
```

green.

---

# 12. Week 3 — Driver FSM, Vehicle and location ingress
**Planned: 9 h**

## 12.1 Objective

Create local authoritative Driver state before Redis/Matching.

---

## 12.2 Issue pack

### B-W03-01 Implement Driver aggregate · 1.5 h

States:

```text
OFFLINE
AVAILABLE
RESERVED
ON_TRIP
```

### B-W03-02 Implement Vehicle baseline · 0.75 h

Only necessary properties.

### B-W03-03 Implement GoOnline/GoOffline · 1 h

### B-W03-04 Implement Driver state coherence tests · 1 h

DSI-002/003.

### B-W03-05 Implement location DTO/validation · 1 h

Validate:

```text
finite values
latitude
longitude
sessionId
sequenceNo
capturedAt
```

### B-W03-06 Implement monotonic accepted location contract · 1 h

### B-W03-07 Integration persistence tests · 1 h

### B-W03-08 FUN-02 baseline · 0.75 h

### B-W03-09 A review Driver state contract · 1 h

---

## 12.3 Important coordinate detail

Application-level geographic coordinates may accept latitude in `[-90,90]`.

Redis `GEOADD` itself indexes longitude `[-180,180]` and latitude approximately `[-85.05112878,85.05112878]`, so B must decide whether:

- reject Redis-nonindexable polar latitude at ingress for P0; or
- accept authoritative location but exclude from GEO projection.

For course simplicity, freeze one documented behavior rather than discovering it during demo.

---

## 12.4 Exit

Driver FSM and location freshness work without Redis yet.

---

# 13. Week 4 — Redis GEO derived projection
**Planned: 10 h**

## 13.1 Objective

Create a fast candidate read model that cannot become reservation authority.

---

## 13.2 Theory

Redis `GEOSEARCH` queries members added through `GEOADD` inside a radius/box. It is suitable for nearby candidate search, but stale projection is expected in an eventually consistent design.

Redis Functions execute atomically and are appropriate for short compare-and-update projection logic.

---

## 13.3 Issue pack

### B-W04-01 Define Redis projection keys/schema · 1 h

### B-W04-02 Implement Driver state projection consumer · 1.25 h

### B-W04-03 Implement location projection consumer · 1.25 h

### B-W04-04 Implement short atomic Redis Function · 1.5 h

Conceptually:

```text
compare driverAggregateVersion
compare location freshness/session
update metadata
GEOADD or ZREM
```

### B-W04-05 Implement DSI-007 stale-version test · 1 h

```text
v10 RESERVED
then v9 AVAILABLE
→ remains v10
```

### B-W04-06 Implement DSI-006 stale-location test · 1 h

```text
seq20
then seq19
→ seq20 remains current
```

### B-W04-07 FUN-03 integration · 1 h

### B-W04-08 Redis unavailable/degraded behavior spike · 0.75 h

### B-W04-09 Evidence/review · 1.25 h

---

## 13.4 Redis rule

Never write code where:

```text
Redis says AVAILABLE
→ mark reservation successful
```

Correct:

```text
Redis says plausible candidate
→ call Driver authority
```

---

## 13.5 Exit

`REL-12/13` early versions green.

---

# 14. Week 5 — Deterministic Matching candidate pipeline
**Planned: 10 h**

## 14.1 Objective

Matching can reproducibly decide whom to try, without yet weakening Driver authority.

---

## 14.2 Issue pack

### B-W05-01 Implement `GEOSEARCH` candidate discovery · 1.25 h

### B-W05-02 Implement eligibility filter · 1 h

Filter:

- derived AVAILABLE;
- fresh location;
- capability;
- not conclusively rejected this round.

### B-W05-03 Implement deterministic ranking · 1.25 h

Suggested baseline:

```text
distance ASC
freshness penalty if used
stable driverId tie-break
```

### B-W05-04 Implement bounded candidate snapshot · 1 h

### B-W05-05 Implement MatchingRound aggregate · 1.5 h

### B-W05-06 Implement MatchingAttempt ledger · 1 h

### B-W05-07 Determinism tests · 1 h

Same snapshot/config:

```text
same ordered candidates
```

### B-W05-08 No-candidate behavior · 0.75 h

### B-W05-09 Metrics · 0.5 h

```text
candidate_count
geo_query_duration
stale_filtered
```

### B-W05-10 A review round/current-assignment semantics · 0.75 h

---

## 14.3 B must not add

- H3;
- PostGIS;
- map route ETA;
- ML ranking;
- fairness optimizer

unless P0 proves a gap.

---

## 14.4 Exit

Matching knows:

```text
who to try
```

but cannot claim:

```text
who is assigned
```

without Driver reserve.

---

# 15. Week 6 — OCC reservation and flagship contention proof
**Planned: 13 h**

## 15.1 Objective

Prove DSI-001 under real concurrent contention.

This is B's first major DS milestone.

---

## 15.2 Theory

EF Core optimistic concurrency can cause a save failure when the tracked concurrency token no longer matches the database row. PostgreSQL can also enforce unique constraints/partial unique indexes as persistence-level defense.

The key project rule:

```text
conflict → domain conflict
```

not:

```text
conflict → blind retry until overwrite
```

---

## 15.3 Issue pack

### B-W06-01 Implement `ReserveDriver` · 2 h

Precondition:

```text
status == AVAILABLE
expected version/current condition valid
```

Success:

```text
status = RESERVED
reservedTripId = T
reservationId = R
version++
```

### B-W06-02 Implement same-reservation idempotent replay · 1 h

If current:

```text
T/R exactly same
```

return canonical success.

### B-W06-03 Implement different-reservation conflict · 0.75 h

### B-W06-04 Add DB defense constraint/index · 1 h

Choose only a constraint that faithfully represents frozen invariant.

### B-W06-05 Build synchronized CON-01 harness · 2 h

Use:

```text
Barrier
Task.WhenAll
explicit start release
```

### B-W06-06 Run N=2/10/50/100 · 1.5 h

### B-W06-07 Query Driver DB final state · 1 h

Assert:

```text
exactly one current reservation
```

### B-W06-08 Add audit support · 0.75 h

### B-W06-09 Repeat high-contention case · 1 h

At least several controlled runs.

### B-W06-10 A review winner semantics · 0.75 h

### B-W06-11 Evidence + first E01 data · 1.25 h

---

## 15.4 Critical interpretation

For N=100:

```text
1 winner
99 conflicts
```

can be the ideal correctness result.

Do not classify business conflict as system error.

---

## 15.5 Exit G1-B

```text
DSI-001/005
CON-01
```

green on real PostgreSQL.

---

# 16. Week 7 — Fencing, GoOffline race and UNKNOWN reconciliation
**Planned: 12 h**

## 16.1 Objective

Close B's hardest correctness problems:

- stale cleanup;
- state race;
- lost reserve response.

---

## 16.2 Issue pack

### B-W07-01 Implement exact `ReleaseDriverReservation` · 1 h

Guard:

```text
tripId
reservationId
current Driver state
```

### B-W07-02 Implement stale release no-op/conflict · 0.75 h

### B-W07-03 CON-04 stale release proof · 1.25 h

```text
R1
release R1
R2
late release R1
→ R2 survives
```

### B-W07-04 CON-02 GoOffline vs Reserve · 1.25 h

Use synchronized barrier.

### B-W07-05 Persist MatchingAttempt before reserve call · 1.25 h

State:

```text
IN_FLIGHT
reservationId
```

must exist before dangerous remote effect.

### B-W07-06 Implement reserve timeout classification → UNKNOWN · 1 h

### B-W07-07 Implement Driver reservation reconciliation API · 1 h

### B-W07-08 Matching same-identity reconcile loop · 1.25 h

### B-W07-09 REL-01 response-loss fault path · 1.25 h

### B-W07-10 Assert no next candidate while UNKNOWN · 0.75 h

### B-W07-11 A/C review retry/breaker semantics · 0.75 h

### B-W07-12 Evidence · 0.75 h

---

## 16.3 .NET resilience rule

Modern .NET HTTP resilience supports retry, timeout and circuit breaker.

B must not blindly enable generic retry/hedging for `ReserveDriver`.

For side-effecting reserve:

```text
same reservationId
application-owned reconcile semantics
```

must dominate framework convenience.

---

## 16.4 Circuit breaker classification

Do not count expected:

```text
409 DriverNotAvailable
```

as infrastructure failure that opens breaker.

Breaker should reflect:

- timeouts;
- connection failures;
- 5xx/transient system failures.

---

## 16.5 Exit

```text
DSI-004
DSI-017
DSI-018
DSI-022
DSI-031
DSL-001
```

have executable proof.

---

# 17. Week 8 — RabbitMQ integration
**Planned: 10 h**

## 17.1 Objective

Move Matching/Driver projection workflow onto real broker while preserving durable state.

---

## 17.2 Issue pack

### B-W08-01 Consume `RideRequestedV1` · 1 h

Lookup/create same:

```text
(tripId, matchingRound)
```

### B-W08-02 Resume existing round on duplicate delivery · 1 h

### B-W08-03 Consume Driver state events for Redis projection · 1 h

### B-W08-04 Consume location event projection path · 0.75 h

### B-W08-05 Publish Driver authoritative events through shared producer/outbox contract · 1 h

### B-W08-06 Manual ACK/dedup integration review with C · 0.75 h

### B-W08-07 CON-06 duplicate Matching execution · 1.25 h

Multiple consumers / duplicate delivery:

```text
one MatchingRound
```

### B-W08-08 Trace context propagation verification · 0.75 h

### B-W08-09 Real broker happy matching path · 1.25 h

### B-W08-10 Evidence/review · 1.25 h

---

## 17.3 RabbitMQ semantics B must know

RabbitMQ explicitly states publisher confirms and consumer ACKs are orthogonal: confirms cover publisher→broker, ACKs cover consumer processing. B should not infer business completion from either alone. citeturn238740search0

---

## 17.4 Exit

Duplicate `RideRequestedV1` does not create a second logical round/attempt sequence.

---

# 18. Week 9 — Durable Matching result + Driver/Matching Outbox
**Planned: 10 h**

## 18.1 Objective

Make Driver authoritative facts and Matching terminal results survive crash/publish gaps.

---

## 18.2 Issue pack

### B-W09-01 Add Driver Outbox events · 1 h

For:

```text
Available
Offline
Reserved
Released
OnTrip
TripCompleted
```

where required by workflow.

### B-W09-02 Driver state + Outbox same transaction · 1 h

### B-W09-03 Matching `NO_DRIVER + Outbox(MatchingFailed)` atomic · 1 h

### B-W09-04 Add Matching result message identity · 0.75 h

### B-W09-05 Integrate shared relay semantics with C · 0.75 h

### B-W09-06 Matching terminal durability test · 1 h

Primary DSI-035.

### B-W09-07 Duplicate publication consumer safety review · 0.75 h

### B-W09-08 Resume round after Matching restart · 1 h

### B-W09-09 REL-02 crash-after-reserve automation base · 1.25 h

### B-W09-10 Evidence/review · 1.5 h

---

## 18.3 Terminal rule

`NO_DRIVER` is legal only if:

```text
all bounded attempts are conclusive
no UNKNOWN/IN_FLIGHT remains
```

Commit:

```text
MatchingRound NO_DRIVER
+
Outbox MatchingFailed
```

together.

---

## 18.4 Exit

Matching does not forget an authoritative terminal decision or unresolved attempt after restart.

---

# 19. Week 10 — Driver lifecycle commands for Saga
**Planned: 9 h**

## 19.1 Objective

Complete Driver-side workflow integration for release/start/complete without weakening fencing.

---

## 19.2 Issue pack

### B-W10-01 Implement `MarkDriverTripStarted` exact guard · 1 h

Requires:

```text
current reservation identity matches
```

### B-W10-02 Implement `MarkDriverTripCompleted` guard · 1 h

Only current active Trip.

### B-W10-03 Make duplicate Start/Complete replay-safe · 1 h

### B-W10-04 Implement release after Payment failure · 1 h

Still exact `reservationId`.

### B-W10-05 Late stale command tests · 1 h

### B-W10-06 FUN-08 Driver side integration · 1 h

### B-W10-07 FUN-09 Driver side integration · 1 h

### B-W10-08 REL-09 compensation support · 1 h

### B-W10-09 A review Driver–Trip agreement · 1 h

---

## 19.3 Exit

Driver ends happy ride:

```text
AVAILABLE
```

and compensation cannot corrupt a newer reservation.

---

# 20. Week 11 — Deterministic fault campaign
**Planned: 11 h**

## 20.1 Objective

Turn B's timing-sensitive correctness claims into reproducible proofs.

---

## 20.2 Issue pack

### B-W11-01 Integrate `Driver.AfterReserveCommitBeforeResponse` · 1 h

### B-W11-02 Automate REL-01 · 1 h

### B-W11-03 Automate REL-02 Matching crash after remote reserve · 1.5 h

### B-W11-04 Automate CON-02 barrier · 1 h

### B-W11-05 Automate CON-04 stale release barrier · 1 h

### B-W11-06 Automate REL-12 stale projection · 0.75 h

### B-W11-07 Automate REL-13 stale location · 0.75 h

### B-W11-08 Redis outage REL-14 · 1 h

### B-W11-09 Remove arbitrary sleeps · 1 h

### B-W11-10 Run key cases 5 consecutive passes · 1 h

### B-W11-11 Evidence + peer rerun · 1 h

---

## 20.3 Fault proof rule

Every timing-sensitive test needs:

```text
known barrier
known process/fault state
bounded wait
final DB/projection assertion
```

No:

```text
sleep(5000)
hope
```

---

## 20.4 Exit

A/C can run B's critical failures without B's debugger.

---

# 21. Week 12 — Observability, security and Core Release Gate
**Planned: 9 h**

## 21.1 Objective

Make Driver/Matching behavior explainable and close B's P0 defects before benchmark week.

---

## 21.2 Issue pack

### B-W12-01 Instrument Driver metrics · 0.75 h

```text
reserve_success
reserve_conflict
reserve_unknown
stale_release_blocked
```

### B-W12-02 Instrument Matching metrics · 1 h

```text
round_duration
candidate_count
attempt_count
unknown_count
reconcile_count
no_driver
```

### B-W12-03 Redis projection metrics · 0.75 h

```text
stale_state_reject
stale_location_reject
projection_latency
```

### B-W12-04 Trace Reserve/reconcile path · 0.75 h

### B-W12-05 Driver authz/actor spoofing negative tests · 1 h

### B-W12-06 Location invalid-data tests · 0.75 h

### B-W12-07 Run B full regression · 1.25 h

### B-W12-08 Run auditor · 0.75 h

### B-W12-09 G4 review · 1 h

### B-W12-10 Cross-explain B mechanisms to A/C · 1 h

---

## 21.3 B's G4 checklist

```text
[ ] FUN-02/03/05/11
[ ] CON-01/02/04/05/06
[ ] REL-01/02/03/12/13/14
[ ] DSI-001..007 evidence
[ ] DSI-015..023 evidence
[ ] DSI-030/031/033 evidence
[ ] DSL-001/003 evidence
[ ] no stateless Matching shortcut
[ ] no Redis authority shortcut
[ ] no blind reserve retry
[ ] no S0/S1 B-owned defect
```

If red:

```text
W13 starts with repair
```

not performance.

---

# 22. Week 13 — Performance campaign
**Planned: 14 h**

## 22.1 Objective

B coordinates the project's controlled experiments without sacrificing correctness.

---

## 22.2 Experiment preparation · 2 h

Freeze:

```text
commit SHA
hardware
container resources
dataset seed
service replicas
DB pool
Rabbit prefetch
retry/timeouts
```

Create experiment manifest template.

---

## 22.3 PERF/E01 — contention curve · 3 h

Run:

```text
N=1
10
25
50
100
```

Same Driver.

Metrics:

```text
winner count
conflict count
system errors
P50/P95
DB/CPU
```

Correctness:

```text
winner = 1
AUD-01 PASS
```

---

## 22.4 PERF/E02 — controlled arrival-rate API · 3 h

Use k6 `constant-arrival-rate` for offered-load question.

k6 documents this executor as open-model: iteration starts are independent of SUT response duration. citeturn540733search3

Suggested rates after baseline:

```text
10
25
50
100 req/s
```

Record:

```text
configured rate
achieved rate
P50/P95
dropped_iterations
domain conflicts
system errors
CPU/memory/DB
```

k6 documents `dropped_iterations` as scheduled iterations that could not begin, commonly because VU allocation was insufficient or the SUT slowed enough to occupy available VUs. citeturn540733search1

---

## 22.5 PERF/E03 — async consumer scaling · 2.5 h

Independent variable:

```text
consumers = 1,2,4
```

Keep fixed:

```text
prefetch
DB pool
message size
arrival rate
hardware
```

Metrics:

```text
messages/s
queue depth
processing latency
redelivery
DB load
CPU
```

Correctness after each:

```text
auditor green
```

---

## 22.6 Analysis · 2 h

Do not report only average.

Use:

```text
P50
P95
sample count
run count
variance/range
```

Repeat major configs 3–5 measured runs where feasible.

Separate:

```text
expected domain conflict
from
system failure
```

---

## 22.7 Evidence packaging · 1.5 h

Raw output stays available.

Graphs/tables are derived.

Never keep only screenshots.

---

## 22.8 Exit

E01–E03 report-grade raw artifacts exist and correctness auditor stays green.

---

# 23. Week 14 — Backlog/recovery and RabbitMQ HA support
**Planned: 8 h**

## 23.1 Objective

Complete E04 and support final broker recovery validation.

---

## 23.2 PERF/E04 / REL-15 · 4 h

Question:

> Can accumulated durable work drain after a controlled outage?

Record:

```text
λ
T
peak backlog B
μ after recovery
time-to-first-success
time-to-steady/zero backlog
```

Compare:

```text
B ≈ λT
T_drain ≈ B/(μ-λ)
```

with measured behavior.

---

## 23.3 REL-16 quorum support · 1.5 h

RabbitMQ quorum queues are replicated queues based on Raft and require a quorum of members to operate; when the leader node fails and a quorum remains, a follower can be elected leader. citeturn238740search3

B helps verify:

- application reconnect;
- no Driver/Matching invariant violation;
- auditor after recovery.

---

## 23.4 Redis recovery check · 1 h

Confirm projection can rebuild/recover without Driver authority loss.

---

## 23.5 Report analysis · 1 h

Write:

- observed bottleneck;
- difference from simple E04 approximation;
- limitations.

---

## 23.6 Final review · 0.5 h

---

# 24. Week 15 — Feature freeze, report and evidence
**Planned: 9 h**

## 24.1 Objective

Freeze architecture and convert B's work into concise academic evidence.

---

## 24.2 Issue pack

### B-W15-01 Remove dead Matching experiments · 0.5 h

### B-W15-02 Clean-clone regression · 1 h

### B-W15-03 Write Driver authority/OCC section · 1.25 h

### B-W15-04 Write Redis derived-state section · 1 h

### B-W15-05 Write Matching/UNKNOWN section · 1.25 h

### B-W15-06 Write performance methodology/results · 1.5 h

### B-W15-07 Curate CON-01/REL-01 diagrams · 0.75 h

### B-W15-08 Validate measured claims vs raw data · 1 h

### B-W15-09 Final evidence manifest · 0.75 h

---

## 24.3 Report wording rules

Use:

> Redis was used as a derived geospatial candidate index. Stale candidates could increase failed reservation attempts, but Driver/PostgreSQL remained the final reservation authority.

Use:

> Under N synchronized contenders on the recorded environment, exactly one reservation remained current.

Do not use:

> Redis guarantees assignment consistency.

Do not use:

> The system scales to production.

---

# 25. Week 16 — Release, live demo and defense
**Planned: 10 h**

## 25.1 Objective

B independently demonstrates and explains the project's strongest concurrency proof.

---

## 25.2 Issue pack

### B-W16-01 Re-run release CON-01/REL-01 evidence · 1 h

### B-W16-02 Rehearse live contention 3x · 1.5 h

Recommended:

```text
10 or 50 contenders
→ one Driver
→ one winner
→ N-1 conflicts
→ AUD-01 PASS
```

### B-W16-03 Rehearse reserve-response-loss demo · 1.25 h

```text
Driver commits R
response suppressed
Matching UNKNOWN
no D2
same R reconcile
```

### B-W16-04 Rehearse Redis stale-event explanation · 0.75 h

### B-W16-05 Oral defense drill · 2 h

### B-W16-06 Review A/C demo handoff · 0.75 h

### B-W16-07 Final performance table validation · 0.75 h

### B-W16-08 Release tag/evidence verification · 0.75 h

### B-W16-09 Clean smoke · 1.25 h

---

# 26. Questions B must answer from memory

1. Who owns Driver reservation?
2. Why not Redis?
3. What is the Driver linearization point?
4. What is optimistic concurrency?
5. Why can two callers read AVAILABLE but only one commit?
6. What does a zero-row conditional update mean?
7. Why not blindly retry a concurrency conflict?
8. What is a persistence constraint doing here?
9. Why use `reservationId`?
10. Why is `reservationId` reused for retry?
11. What is fencing?
12. How can stale Release damage a new reservation?
13. How is stale Release prevented?
14. What happens in GoOffline vs Reserve?
15. What is MatchingRound?
16. Why persist MatchingAttempt before remote reserve?
17. What is `UNKNOWN`?
18. Why timeout ≠ failure?
19. Why cannot Matching try D2 while D1 is UNKNOWN?
20. How does reconcile use same reservation identity?
21. Why is deterministic ranking valuable?
22. Why eligibility precedes ranking?
23. Why is Redis state allowed to be stale?
24. What does Redis Function atomicity protect?
25. What does it **not** protect?
26. Why not use Redlock for Driver assignment?
27. What happens if Redis is down?
28. What happens if Driver Service is down?
29. What does CON-01 prove?
30. Why are 99 conflicts at N=100 not 99 system errors?
31. What does REL-01 prove?
32. What does REL-02 prove?
33. What is final-state oracle?
34. Why use barriers instead of sleeps?
35. Why use real PostgreSQL?
36. Why use open arrival-rate model?
37. What is coordinated omission?
38. What are dropped iterations?
39. Why report P50/P95?
40. Why rerun auditor after load?
41. What does E04 formula approximate?
42. What does RabbitMQ quorum test prove?
43. What does it not prove?
44. What are the system's scalability limitations?
45. What would you change first for real production scale?

---

# 27. B ↔ A interface checklist

Before accepting Trip boundary:

```text
[ ] Trip supplies stable tripId
[ ] matchingRound meaning frozen
[ ] stale round cannot become current
[ ] Trip accepts DriverReserved only if current/legal
[ ] Trip cancellation remains terminal
[ ] exact cleanup carries reservationId
[ ] Trip does not infer Driver authority from Redis
[ ] Driver event duplication is safe
[ ] late DriverReserved after cancellation triggers cleanup/no-op, not reactivation
[ ] AUD-04 can compare final Driver–Trip agreement
```

---

# 28. B ↔ C messaging/reliability checklist

```text
[ ] MessageEnvelope IDs understood
[ ] manual ACK after durable effect
[ ] publisher confirms enabled
[ ] consumer dedup durable
[ ] Driver/Matching Outbox follows same semantics
[ ] retry owner per boundary
[ ] domain conflict excluded from infrastructure failure where appropriate
[ ] poison messages bounded/DLQ
[ ] trace context diagnostic only
```

---

# 29. B's Definition of Done

## Driver state feature

```text
[ ] authority named
[ ] legal transition
[ ] stale transition behavior
[ ] duplicate behavior
[ ] DB guard/constraint
[ ] real PostgreSQL test
[ ] reviewer
```

## Matching feature

```text
[ ] round persisted
[ ] attempt persisted
[ ] candidate order deterministic
[ ] no direct Driver DB access
[ ] UNKNOWN path
[ ] restart path
[ ] terminal result durable
[ ] final-state assertion
```

## Redis feature

```text
[ ] derived-only
[ ] version/freshness guard
[ ] atomic projection mutation
[ ] stale test
[ ] outage behavior
[ ] no business invariant delegated to Redis
```

## Performance experiment

```text
[ ] question/hypothesis
[ ] independent variable
[ ] controlled variables
[ ] fixed commit/config
[ ] raw outputs
[ ] sample/run count
[ ] latency percentiles
[ ] conflict vs error split
[ ] auditor after run
[ ] limitation statement
```

---

# 30. Common mistakes B must prevent

## Mistake 1 — Redis as source of truth

Wrong:

```text
Redis AVAILABLE → assignment success
```

Correct:

```text
Redis candidate → Driver reserve
```

## Mistake 2 — read then unconditional update

Wrong:

```text
SELECT AVAILABLE
UPDATE RESERVED
```

without guard.

Correct:

```text
conditional/versioned authoritative mutation
```

## Mistake 3 — blindly retry OCC conflict

That can turn loser into later winner and change operation meaning.

## Mistake 4 — timeout means failure

Wrong:

```text
timeout → try next Driver
```

Correct:

```text
UNKNOWN → same R reconcile
```

## Mistake 5 — generate new reservationId on retry

Turns one logical attempt into another.

## Mistake 6 — stale release by Driver ID only

Can free newer reservation.

## Mistake 7 — stateless Matching

Can forget committed remote effect after crash.

## Mistake 8 — ranking algorithm too complex

ML/H3/PostGIS do not improve P0 correctness proof.

## Mistake 9 — performance before Core Gate

Benchmarking incorrect system wastes time.

## Mistake 10 — reporting conflict as generic error

Contention conflict is an expected semantic outcome.

## Mistake 11 — tuning several variables simultaneously

Cannot infer causal bottleneck.

## Mistake 12 — using one benchmark run

Not robust enough for report-grade claim.

---

# 31. Blocker escalation

Immediate:

- two active reservations observed;
- stale release frees newer reservation;
- Matching advances while attempt UNKNOWN;
- Matching restart generates new reservation identity for unresolved attempt;
- Redis proposed as reservation authority;
- CON-01 only passes when requests are serialized;
- performance run ends with auditor failure.

Same day:

- A/B disagree on matchingRound;
- C retry policy would auto-retry reserve without B's identity semantics;
- Driver event ownership unclear;
- Redis projection schema cannot reject stale version.

Template:

```text
Observed:
Expected contract:
Driver authoritative row:
MatchingRound:
MatchingAttempt:
reservationId:
Redis projection:
Network/fault:
Affected DSI/DSL:
Smallest reproduction:
Decision required:
Owner:
Deadline:
```

---

# 32. Personal risk register

| Risk | Impact | Control |
|---|---|---|
| double Driver reservation | critical | OCC + DB guard + CON-01 |
| GoOffline overwrites reservation | critical | guarded transition + CON-02 |
| stale release frees new work | critical | reservation fencing + CON-04 |
| timeout treated as failure | critical | UNKNOWN + REL-01 |
| Matching restart skips unresolved effect | critical | durable attempt + REL-02 |
| Redis becomes authority by convenience | critical | code/review rule |
| stale projection regresses | high | version/freshness function |
| ranking nondeterministic | medium | stable ordering/tie-break |
| performance overclaim | medium | environment/raw data/claim discipline |
| B is only OCC expert | high | A backup/rerun |

---

# 33. Personal report/defense pack

By final week:

1. Driver FSM.
2. DSI-001..007 summary.
3. MatchingRound/Attempt state diagram.
4. DSI-015..023 summary.
5. Redis authority-vs-projection diagram.
6. GEO candidate flow.
7. OCC linearization timeline.
8. CON-01 raw results.
9. GoOffline vs Reserve timeline.
10. fencing/stale-release timeline.
11. UNKNOWN reserve timeline.
12. REL-01 evidence.
13. REL-02 evidence.
14. stale projection evidence.
15. stale location evidence.
16. Redis outage behavior.
17. E01 contention table.
18. E02 arrival-rate table.
19. E03 consumer scaling table.
20. E04 backlog/recovery analysis.
21. auditor results.
22. limitations.

---

# 34. B's strongest 90-second explanation

B should be able to say conceptually:

> Matching uses Redis GEO only to obtain nearby candidates quickly. Redis is eventually consistent and may be stale, so every candidate still passes through Driver Service, whose PostgreSQL state is the reservation authority. Competing reservations use guarded optimistic concurrency so only one current reservation can commit. Each reserve attempt has a stable `reservationId`, which provides idempotency, fencing and a reconciliation identity. If the HTTP response is lost after the Driver commit, Matching records the attempt as `UNKNOWN` and must not try another Driver until the same reservation is reconciled. Matching persists its round and attempts so a process restart resumes the unresolved work instead of recomputing from stale Redis. The same invariants are then tested with synchronized races, deterministic lost-response faults and final database auditing.

B must map every sentence to:

```text
code
DSI/DSL
CON/REL scenario
DB assertion
evidence
```

---

# 35. Authoritative implementation-learning references

Project Docs 00–04 remain canonical. These sources validate external technology semantics.

## EF Core concurrency

Microsoft — Handling Concurrency Conflicts  
https://learn.microsoft.com/en-us/ef/core/saving/concurrency

Use for:

- concurrency tokens;
- optimistic conflict behavior;
- why application must resolve conflict explicitly.

## PostgreSQL

PostgreSQL 18 — Transaction Isolation  
https://www.postgresql.org/docs/18/transaction-iso.html

Use for:

- `READ COMMITTED`;
- MVCC;
- concurrency reasoning.

PostgreSQL 18 — Unique Indexes  
https://www.postgresql.org/docs/18/indexes-unique.html

PostgreSQL 18 — CREATE INDEX / partial unique indexes  
https://www.postgresql.org/docs/18/sql-createindex.html

Use for:

- uniqueness defense;
- subset uniqueness.

## Redis GEO

Redis — GEOADD  
https://redis.io/docs/latest/commands/geoadd/

Redis — GEOSEARCH  
https://redis.io/docs/latest/commands/geosearch/

Use for:

- coordinate/index behavior;
- nearby candidate query.

## Redis Functions

Redis — Functions  
https://redis.io/docs/latest/develop/programmability/functions-intro/

Use for:

- atomic short server-side projection compare/update.

## .NET resilience

Microsoft — HTTP resilience  
https://learn.microsoft.com/en-us/dotnet/core/resilience/http-resilience

Use for:

- retry;
- timeout;
- circuit breaker;
- why framework policy is subordinate to business idempotency/UNKNOWN semantics.

## RabbitMQ

RabbitMQ — Consumer ACKs and Publisher Confirms  
https://www.rabbitmq.com/docs/confirms

RabbitMQ — Quorum Queues  
https://www.rabbitmq.com/docs/quorum-queues

Use for:

- ACK/confirm mental model;
- final broker HA experiment.

## k6

Grafana k6 — Open and Closed Models  
https://grafana.com/docs/k6/latest/using-k6/scenarios/concepts/open-vs-closed/

Grafana k6 — Constant Arrival Rate  
https://grafana.com/docs/k6/latest/using-k6/scenarios/executors/constant-arrival-rate/

Grafana k6 — Dropped Iterations  
https://grafana.com/docs/k6/latest/using-k6/scenarios/concepts/dropped-iterations/

Use for:

- offered-load methodology;
- coordinated omission;
- dropped-iteration interpretation.

---

# 36. Weekly operating checklist

Every week start:

```text
[ ] previous gate green?
[ ] current authoritative DB fact?
[ ] Redis/view involved?
[ ] stable reservation identity?
[ ] race/failure window?
[ ] final DB oracle?
[ ] exact CON/REL/PERF scenario?
[ ] reviewer assigned?
```

For every Driver/Matching issue:

```text
[ ] can two callers race?
[ ] can same request duplicate?
[ ] can response disappear?
[ ] can stale old command arrive?
[ ] can process restart?
[ ] can Redis be stale/down?
[ ] can final state still be audited?
```

Before close:

```text
[ ] real dependency used where semantics matter
[ ] deterministic concurrency/fault path
[ ] final authority assertion
[ ] no hidden sleep
[ ] evidence saved
[ ] peer can rerun
[ ] actual hours recorded
```

---

# 37. Final north star

> **Whenever B is unsure whether Driver/Matching work is complete, ask: if Redis is stale, 100 Trips race, the winning response disappears, Matching crashes, and an old release arrives after a newer reservation, can the system still prove from durable state that exactly one current Driver assignment is legal and that no unresolved remote effect was skipped?**

If not, the Driver/Matching subsystem is not complete.
