# Distributed Ride-Hailing System (DS03)

## Final Technical Report

**Course:** Distributed Systems — DS03 Ride-Hailing Microservices

**Team**

| Member | Student ID | Responsibility |
|---|---|---|
| Nguyen Quang Minh | B23DCVT280 | Trip/Booking, lifecycle, Saga orchestration, compensation, Trip Outbox |
| Nguyen Hoang Khanh | B23DCVT228 | Driver/Location, Matching, reservations, concurrency and Driver lifecycle |
| Le Anh Viet | B23DCVT453 | Payment, messaging/platform, shared Outbox relay, reliability infrastructure |

**Version:** 1.0.0

**Date:** 6 November 2026

**Repository:** <https://github.com/minhqng/ride-hailing-microservices-system>

---

## Abstract

This report presents a distributed ride-hailing platform built around five
independently deployed business services: Identity, Driver, Trip, Matching and
Payment, with an asynchronous Notification worker and an API Gateway. Each
service owns its PostgreSQL database and communicates through HTTP and
RabbitMQ. There is no shared database, no distributed transaction and no
distributed lock. The central engineering question is how such a system keeps
its safety invariants and eventually recovers when requests race, reads are
stale, messages duplicate, responses disappear and processes crash.

The design answers that question with explicit ownership rather than implicit
coordination. Driver reservation is linearized by a guarded PostgreSQL update
and fenced by exact trip, reservation and matching-round identity. Trip owns a
persisted Saga and a transactional Outbox; Payment owns a stable operation key
and explicit `UNKNOWN` reconciliation; Matching owns durable round state and
cancel tombstones; Notification is a non-authoritative, owner-isolated
projection. Transport is at-least-once with idempotent consumers, not
exactly-once. Verification uses real PostgreSQL, RabbitMQ and Redis boundaries.
The final release regression reports 1,341 passing tests across 13 suites with
zero failures and one documented opt-in skip, a 19-check Production overlay
smoke covering a completed ride, cancellation compensation, driver rejection,
notification-outage isolation and crash replay, and controlled performance and
high-availability campaigns with retained raw evidence. The report also states
the limits of those claims: the payment provider is simulated, notifications
are in-app, performance results are single-host measurements and human
rehearsal remains a team activity.

---

## Contents

1. Introduction
2. Requirements and methodology
3. Architecture
4. Domain design
5. Distributed workflow engineering
6. Implementation of DS03 functionality
7. Verification and results
8. Performance and high-availability experiments
9. Discussion and limitations
10. Contribution and attribution
11. Conclusion
12. References
Appendix A. HTTP surface
Appendix B. Evidence index

---

# 1. Introduction

## 1.1 Context

Ride-hailing is a useful vehicle for studying distributed systems because its
business correctness depends on several authorities agreeing about a small set
of facts under adverse conditions. A passenger requests a ride; a matching
component selects a nearby driver; the driver may accept or reject; money is
authorized, captured or voided; both parties expect notifications. Every step
crosses a process and database boundary, and several steps can race.

A naive implementation that looks correct in a single-process demo fails in
ways that are difficult to see: the same driver is assigned to two trips, a
stale release message frees a newer reservation, a duplicated delivery
authorizes a payment twice, a lost HTTP response leaves the system in an
unknown state that one side treats as failure and the other as success, or a
crash between the database commit and the broker publish loses a committed
business intent. These are not edge cases to be patched after the demo; they
are the subject of the project.

## 1.2 Problem statement

The platform must satisfy the DS03 functionality while preserving the
following safety properties:

- one driver is never assigned to two active trips;
- one trip never holds two drivers;
- a stale release never frees a newer reservation;
- a terminal trip never reactivates;
- a payment effect is never applied twice for the same logical operation;
- a committed business change never loses its outgoing message.

It must also make progress under failures:

- pending Outbox rows eventually publish;
- duplicate deliveries are absorbed;
- lost responses remain `UNKNOWN` and are reconciled with the same identity;
- Saga steps complete, compensate or end in an explicit review state;
- a restarted process resumes committed work;
- notification failure cannot block ride truth.

## 1.3 Objectives

1. Implement the DS03 functionality with at least four real business services,
   independent processes, synchronous and asynchronous communication, and
   service-owned databases.
2. Make the distributed correctness mechanisms explicit in code and in
   contracts: stable identities, idempotency, bounded retry, error storage,
   correlation, a real Saga, concurrency control, failure detection and
   recovery.
3. Verify the system with real infrastructure, not mocks, and retain evidence
   that anchors every claim to the source revision that produced it.
4. Produce a reproducible deployment and demonstration path suitable for a
   clean checkout.

## 1.4 Scope and non-goals

The project is a course system, not a production ride-hailing service. It
simulates the payment provider, implements in-app notifications rather than
SMS or push, and measures performance on a single host. It does not claim
exactly-once delivery, multi-region availability or production capacity. The
report distinguishes implemented and verified properties from these explicit
non-goals.

## 1.5 Contributions

- A service architecture with five business services, a worker and a gateway,
  each with its own database and process.
- A reservation design in which Redis is a derived candidate index and never
  an authority, and PostgreSQL OCC is the linearization point.
- A transactional Outbox and consumer deduplication design that closes the
  commit/publish and deliver/effect gaps without claiming exactly-once.
- A persisted Trip Saga with compensation, forward capture recovery and an
  explicit `REQUIRES_REVIEW` outcome.
- Durable Matching rounds, cancellation tombstones and same-identity
  reconciliation of uncertain reservations.
- A recipient-addressed notification inbox that is isolated from ride truth.
- A verification programme with 13 executable suites, real-provider release
  smoke scenarios, controlled contention/throughput/backlog experiments and
  quorum-broker failure tests.
- An evidence process that retains manifests, source anchors, final-state
  assertions and measured samples for every claim.

---

# 2. Requirements and methodology

## 2.1 Course requirements

The DS03 specification requires a ride-hailing system with at least four
functioning business microservices; independent processes; REST or gRPC plus
asynchronous messaging; service-owned databases or isolated schemas; no direct
cross-service production database access; explicit service identities,
addressing and contracts; and support for concurrent requests. Functionally it
requires user registration, login and passenger/driver roles; driver status and
simulated location; ride creation; driver search and selection; accept or
reject; no simultaneous assignment of one driver to two trips; trip lifecycle
through completion or cancellation; fare calculation and simulated payment;
and passenger/driver notifications. Distributed correctness requires event
identity, idempotency and deduplication, bounded retry, error storage or a
dead-letter queue, correlation identifiers, at least one real distributed Saga
or compensation workflow, concurrency control with authoritative final-state
consistency, and failure detection, recovery and restart.

The course also sets a minimum test profile: at least eight functional tests,
two concurrency or conflict tests, two fault or recovery tests, two security
or invalid-data tests and one performance or scalability experiment, plus an
executable demonstration involving at least three services with observable
network communication.

## 2.2 Engineering principles

The implementation follows eight principles. They are stated here because the
code is easier to read when the reason for a guard is visible.

1. **Explicit authority.** Every fact has one owning service. Redis may
   nominate a driver; only the Driver PostgreSQL row can reserve one.
2. **Local atomicity.** A service commits its state change and its outgoing
   intent in one database transaction. Cross-service atomicity is not assumed.
3. **Stable identity.** `tripId`, `matchingRound`, `reservationId`,
   `operationKey`, `messageId`, `correlationId` and `traceId` are distinct
   identities with distinct purposes and are never substituted for one
   another.
4. **Guarded transition.** State changes are conditional on the expected state
   and identity; otherwise they change nothing and return a replay or conflict
   outcome.
5. **Durable intent.** A message that must accompany a committed business
   change is written to the same database as that change.
6. **Idempotency.** A duplicated delivery or retried request must not create a
   second business effect. The receipt is committed with the effect.
7. **Explicit `UNKNOWN`.** A timeout does not mean failure. An uncertain
   outcome is recorded and reconciled against the same identity.
8. **Eventual recovery.** Pending work is durable and resumable; recovery is a
   normal state, not a manual repair script.

## 2.3 Verification methodology

Verification separates five evidence classes:

- **E1 domain proof** — unit tests for aggregates and policies;
- **E2 real-boundary integration proof** — real PostgreSQL, RabbitMQ, Redis
  and HTTP;
- **E3 concurrency proof** — synchronized contention, unique winner and
  authoritative final state;
- **E4 fault/recovery proof** — deterministic fault points, process
  termination, broker loss, restart and reconciliation;
- **E5 experiment proof** — controlled performance campaigns with fixed
  images, resource limits and retained samples.

A claim is accepted only when its evidence names the source commit and tree it
verified. Historical measurements are retained with their original source and
are not relabelled as results for newer code. This rule matters because the
repository's `main` history was rewritten for metadata-only attribution
corrections; the trees were preserved, and the old and new SHAs are recorded
in a machine-readable provenance map.

---

# 3. Architecture

## 3.1 Services and ownership

| Component | Owner | Responsibility | Storage | Port |
|---|---|---|---|---|
| Gateway | C | Routing, JWT forwarding, edge request limits | none | 8080 |
| Identity | Shared/A | Users, roles, RSA-signed JWTs, service grants | `identity_db` | 8101 |
| Driver | B | Driver profile, status, location, exclusive reservation authority | `driver_db` | 8102 |
| Trip | A | Trip lifecycle, fare snapshot, persisted Saga, Outbox | `trip_db` | 8103 |
| Matching | B | Durable matching rounds, candidate ranking, uncertain-outcome reconciliation | `matching_db` + Redis GEO | 8104 |
| Payment | C | Simulated provider, operation-key idempotency, reconciliation | `payment_db` | 8105 |
| Notification | C | Asynchronous projection, per-recipient inbox | `notification_db` | 8106 |

The Gateway is not counted as one of the four business services required by
the course. Identity, Driver, Trip, Matching and Payment are five independently
deployed business services; Notification is a worker.

## 3.2 Communication topology

```text
                         +----------------------+
   Client (Rider/Driver) |  Gateway :8080       |
                         |  YARP + JWT          |
                         +----------+-----------+
                                    |
        +----------------+----------+----------+----------------+
        |                |                     |                |
        v                v                     v                v
  Identity :8101    Driver :8102         Trip :8103      Notification :8106
  identity_db       driver_db            trip_db          notification_db
                         ^                     |
                         | Reserve/Reconcile   | RideRequestedV1 (async)
                         | (sync HTTP)         v
                    Matching :8104  <----------+
                    matching_db + Redis GEO
                         |
                         | DriverReserved / Released
                         v
                      Trip :8103
                         |
                         | Authorize / Capture / Void (async Saga)
                         v
                    Payment :8105
                    payment_db
```

All asynchronous paths use RabbitMQ critical quorum queues. Matching→Driver
reservation is a synchronous HTTP call because the matching round needs an
immediate authority result; an uncertain result becomes `UNKNOWN` and is
reconciled with the same reservation identity. Trip→Payment is asynchronous
through the Outbox, so a Payment outage does not block the Trip commit.

## 3.3 Data ownership and isolation

Each service has a separate logical database and a separate PostgreSQL role.
The bootstrap script creates `identity_db`, `driver_db`, `trip_db`,
`matching_db`, `payment_db` and `notification_db` with owners that cannot read
the other databases. Every service reads its connection string from its own
configuration; no service opens a connection to another service's database.
Integration tests assert ownership boundaries (`DriverDbOwnershipTests`,
`MatchingDbOwnershipTests`, `DbIsolationTests`). Cross-service reads go through
HTTP contracts or messages, never through a shared table.

Redis is owned by Matching but is explicitly derived. It stores candidate
locations and ordering metadata; the Matching worker may use it to nominate
drivers, but it never writes a reservation decision. A lost or stale Redis
projection can waste a matching attempt; it cannot violate the single-assignment
invariant.

## 3.4 Deployment topology

`compose.yaml` defines the development stack; `deploy/compose/compose.release.yaml`
adds fail-closed secret requirements and Production settings. The release
overlay forces Trip messaging on, disables Trip auto-migration and enables the
Payment reconciliation worker. Migrations are applied as an external job against
the Trip-owned database before the overlay starts. Image versions are pinned by
digest. The RabbitMQ configuration uses quorum queues for critical paths; the
HA overlay adds broker clustering and is exercised by the W14 evidence.

## 3.5 Observability

Every service exports OpenTelemetry traces and metrics. W3C trace context and
correlation identifiers propagate across HTTP and RabbitMQ envelopes. The
metrics include Outbox pending/failed counts, consumer outcomes, reservation
conflicts, Payment outcomes and reconciliation results. Observability is
treated as evidence, not recovery state: a dashboard cannot repair a pending
Outbox row, and a missing metric does not make a failed business effect
disappear.

---

# 4. Domain design

## 4.1 Trip lifecycle

Trip is the lifecycle authority. Its canonical states are `CREATED`,
`MATCHING`, `DRIVER_RESERVED`, `DRIVER_ACCEPTED`, `READY_TO_START`,
`IN_PROGRESS`, `COMPLETED`, `CANCELLED` and `MATCH_FAILED`. The terminal set
`COMPLETED`, `CANCELLED` and `MATCH_FAILED` is monotonic: no later event may
reactivate a terminal trip. Every transition is guarded by the expected current
state and the exact assignment generation.

A Trip holds a `FareSnapshot` created from the request; the fare is not
recomputed by downstream services. The Trip read surface exposes the canonical
state, current matching round, coherent assignment reference (both driver and
reservation present or both absent) and, for a failed match, a durable
`failureReason` (`NO_DRIVER_AVAILABLE` or `DRIVER_REJECTED`). Internal Saga and
Outbox state are not exposed.

## 4.2 Driver reservation authority

The Driver row is the only reservation authority. A reservation is a guarded
update equivalent to:

```sql
UPDATE drivers
SET status = 'RESERVED',
    reserved_trip_id = :tripId,
    reservation_id = :reservationId,
    reserved_round = :matchingRound,
    version = version + 1
WHERE driver_id = :driverId
  AND status = 'AVAILABLE'
  AND version = :expectedVersion;
```

The successful commit is the linearization point. A second concurrent caller
sees a different version or a non-available status and loses; it cannot obtain
a second reservation. Release is fenced by the exact `(tripId, reservationId)`
pair, so a stale release cannot free a newer reservation. The `reserved_round`
column preserves the matching-round provenance across restarts, which lets Trip
verify that a `DriverReserved` event belongs to the current round rather than a
legacy or replayed one.

Redis is a candidate index. A stale candidate can cause a rejected reservation
attempt, but the attempt does not assign the driver; the Matching round
continues with the next candidate or reconciles the uncertain outcome.

## 4.3 Matching

Matching owns a durable `MatchingRound` and its attempts. A `RideRequestedV1`
message commits the round, pickup center and auto-execute obligation before it
is acknowledged. A hosted worker leases durable `PROCESSING` rounds from
PostgreSQL, so a crash after acknowledgement does not lose the work. Candidates
are ranked deterministically from the frozen input; ordering does not depend on
randomness or Redis iteration order.

The round advances through explicit stages only after a conclusive result. An
attempt in `UNKNOWN` blocks advancement; the worker reconciles the same
reservation identity against Driver authority instead of starting a second
attempt with a new identity. A `CancelMatchingRoundV1` message records a durable
cancel intent or tombstone, so a cancellation that overtakes its
`RideRequestedV1` is still honoured. A late cancellation after a reservation
leads to an exact release and terminal round state, never a fallback to
processing.

## 4.4 Payment

Payment is a durable simulator with a provider ledger. The logical money effect
is keyed by `operationKey`, not by `messageId`. The first request for a key
creates one operation; a repeated request with the same payload returns the
stored result; a same-key request with a different payload is rejected as an
idempotency conflict. Authorize, Capture and Void each have a stable operation
key derived from the trip and the operation type.

The operation and its provider effect are committed durably. A lost response
leaves the operation `UNKNOWN`, and a reconciliation worker resolves it against
the provider ledger using the same key. The worker never starts a second
financial operation and never invents a new key. In the release overlay the
worker is enabled; the Payment simulator HTTP routes are Development-only, so a
Production deployment cannot silently exercise a test control plane.

## 4.5 Notifications

Notification is a non-authoritative projection. Trip and Driver commit
`NotificationRequestedV1` intents in their Outboxes with the business change.
The Notification worker consumes the intent, writes one inbox row per
recipient and deduplicates by deterministic notification identity and a global
source-message fingerprint. A reused `messageId` with changed recipient or
payload is treated as poison rather than as a second notification.

The read API is owner-isolated from the JWT `sub` claim: a client-supplied user
id is never accepted, and a foreign or unknown notification id returns the same
not-found response so existence is not disclosed. The worker is asynchronous
and non-blocking; a stopped worker delays notification delivery but cannot
block the Trip, Driver or Payment commit.

## 4.6 Identity and access control

Identity issues short-lived RSA-signed JWTs for users and separate grants for
service clients (`matching`, `trip`). Gateway and services validate the
signature, issuer, audience, lifetime and algorithm. Role checks distinguish
Rider, Driver and Service. Driver routes bind the authenticated actor to the
Driver-owned profile; a Driver cannot act on another Driver's reservation.
Service permissions are explicit (`matching:reserve`, `trip:release`), so a
service token cannot perform an unrelated action.

Input boundaries limit request bodies (16 KiB at Identity, 64 KiB at the
Gateway and bounded limits at the Driver and Trip endpoints) and validate
payload shapes before persistence. The release configuration requires every
secret explicitly; a secretless render fails closed.

---

# 5. Distributed workflow engineering

## 5.1 Transactional Outbox

A business change and its outgoing message are written in one local
transaction. A relay claims pending rows with a lease, publishes with publisher
confirms, and marks the row `PUBLISHED` only after the broker confirms. A crash
before the publish leaves the row pending; a crash after the publish but before
the mark leaves a duplicate that the consumer's deduplication absorbs. This
closes the committed-intent loss window without requiring a distributed
transaction.

The relay is bounded: it has a batch size, a claim lease, a poll interval, a
maximum attempt count and a retry delay. Failures are visible in metrics and
the row remains retryable; there is no infinite tight loop.

## 5.2 At-least-once consumption and deduplication

Consumers use manual acknowledgement after a durable local effect. The effect
and a `processed_messages` receipt are committed in the same database
transaction; the acknowledgement follows the commit. A crash between commit and
ACK produces a redelivery that finds the receipt and skips the effect. This is
at-least-once transport with idempotent processing. The project does not claim
exactly-once delivery, because a broker cannot guarantee that a consumer
processed a message exactly once; it guarantees that the observable business
effect occurs once.

## 5.3 Persisted Saga and compensation

Trip owns a persisted `RideSaga`. On the first accepted assignment it commits
the `AUTHORIZE_PENDING` step and the Payment intent in the same transaction.
When authorization succeeds, the Saga advances to `AUTHORIZED`; completion
advances to `CAPTURE_PENDING`; capture success reaches `COMPLETED`. A
cancellation after authorization commits a compensation path that releases the
reservation and voids the authorization with a stable void key. A capture whose
outcome is unknown remains `CAPTURE_PENDING` and is reconciled; if the
authority cannot resolve it within the bounded policy, the Saga ends in
`REQUIRES_REVIEW` rather than guessing.

A driver rejection is a terminal `MATCH_FAILED` with `DRIVER_REJECTED`. It
commits the exact cleanup obligations — `CancelMatchingRoundV1` and
`ReleaseDriverReservationV1` — in the Trip Outbox, and it does not start a
payment. The response reports `CleanupPending` while a cleanup obligation is
unconfirmed; it never claims that the remote release already happened.

## 5.4 Lost responses and `UNKNOWN`

Every side-effecting call can lose its response. The design records the
operation as `UNKNOWN` and reconciles it with the same identity:

- Matching→Driver reserve: the same `reservationId` is queried against Driver
  authority; a conclusive result is accepted, and an unresolved result keeps
  the attempt blocked.
- Payment authorize/capture/void: the same `operationKey` is reconciled
  against the provider ledger.
- A timeout is not treated as a failure and is not retried with a new
  identity.

## 5.5 Retry, dead letters and quarantine

Delivery attempts are bounded. A message that exceeds the retry policy is
routed to a dead-letter queue; malformed or contradictory messages can be
quarantined. The topology smoke verifies the exchanges, queues, bindings and
dead-letter targets. Retry is not a substitute for idempotency: a redelivery
must still be safe.

## 5.6 Crash and restart recovery

Fault points make failure deterministic. Trip, Matching, Driver and Payment
processes can be stopped at defined points; the tests then restart them and
assert that pending work resumes, duplicates are absorbed and the final
authority state is correct. The W11 evidence records real process crash/restart
proofs, and the release smoke Scenario D stops the Trip process after a reject
commit and verifies an exact replay after restart.

## 5.7 Invariant auditor

The repository includes a Trip invariant auditor and an independent B12 audit
suite. The auditor reads Trip, Driver, Matching and Payment authority state and
checks the cross-service agreements: terminal trips have no active assignment,
a reserved driver points at an existing active trip, matching rounds and
reservations agree, and payment effects are unique per operation key. The
auditor is exercised in the performance and release gates, not only in unit
tests.

---

# 6. Implementation of DS03 functionality

## 6.1 Registration, login and roles

Identity exposes `POST /auth/register`, `POST /auth/login` and
`POST /connect/token` for service clients. Registration validates username
length, password length (minimum 12 characters) and the role (`Rider` or
`Driver`). Passwords are stored as salted hashes. Login returns a short-lived
RSA-signed JWT with `sub`, `role` and expiry. The Gateway forwards the token and
strips client-supplied identity headers (`X-Driver-Id`, `X-Rider-Id`,
`X-User-Id`, `X-Role`), so a caller cannot inject an identity at the edge.

## 6.2 Driver availability and simulated location

A Driver authenticates, creates a profile bound to its Identity user id, goes
online and posts location samples. Status transitions are version-guarded.
Location samples carry a device session and monotonically increasing sequence
number; stale or duplicate samples are rejected. The accepted projection is
published asynchronously and projected into Redis GEO for candidate discovery.

## 6.3 Ride creation and idempotency

`POST /trips` accepts pickup and dropoff coordinates and an
`Idempotency-Key` header. The rider identity comes from the JWT, never from the
body. The same key with the same request returns the original trip; the same
key with a different request is a conflict. The Trip row and the
`RideRequestedV1` Outbox intent are committed in one transaction. If the
broker is unavailable, the committed intent remains pending and the client can
retry with the same key; the system never creates a second logical ride.

## 6.4 Matching and assignment

Matching consumes `RideRequestedV1`, freezes the candidate snapshot and ranks
drivers deterministically. It reserves through the Driver HTTP authority. A
successful reservation produces `DriverReservedV1` with the exact trip,
reservation and matching-round identity. Trip records the assignment only for
the current round. A no-driver result is terminal `MATCH_FAILED` with
`NO_DRIVER_AVAILABLE`; there is no fabricated assignment and no rematch after a
terminal rejection.

## 6.5 Accept and reject

The Driver lifecycle routes are authenticated:

```text
POST /trips/{tripId}/accept
POST /trips/{tripId}/reject
POST /trips/{tripId}/start
POST /trips/{tripId}/complete
```

Each request supplies `driverId` and `reservationId`. The endpoint resolves the
Driver profile through the Driver-owned directory and verifies that it belongs
to the authenticated actor; the aggregate then fences the exact current
assignment. A wrong driver, stale reservation or wrong state changes nothing.

Acceptance starts the persisted Saga and the `AuthorizePayment` intent in the
same commit. Rejection records `MATCH_FAILED/DRIVER_REJECTED`, commits the
cleanup intents and a rejection notification, and starts no payment. The
response distinguishes a terminal rejection from a cleanup obligation that is
still pending.

## 6.6 Trip lifecycle

Start requires an authorized ride and moves the Trip to `IN_PROGRESS`; the
Driver authority moves to `ON_TRIP`. Completion moves the Trip to `COMPLETED`,
starts capture, and commands the Driver to complete. Cancellation is allowed
before start; after authorization it commits a void and an exact release. The
Saga and the Trip state are the authority; the HTTP response is an
acknowledgement of the current durable state, not a promise about remote
effects.

## 6.7 Fare and payment

Fare is computed from the frozen request by a deterministic calculator and
stored as a snapshot. Acceptance requires a positive payable amount; a fare that
cannot produce one is rejected before any mutation. Payment authorization,
capture and void are durable operations keyed by stable operation keys. The
release smoke verifies one authorize and one capture for a completed ride, and
one authorize and one void for a cancellation after authorization.

## 6.8 Notifications

Trip and Driver Outboxes emit `NotificationRequestedV1` for ride requested,
driver assigned, driver rejected, trip completed and trip released events. The
Notification worker resolves the recipient from the intent, writes an inbox row
with a deterministic identity and exposes the JWT-scoped read API. The release
smoke verifies that both roles can read their own inbox and that a stopped
worker is caught up exactly once after restart.

---

# 7. Verification and results

## 7.1 Test strategy

Tests are layered by the risk they prove:

- unit tests for aggregates, policies, identity extraction and message
  contracts;
- component tests with a real PostgreSQL database and real HTTP;
- integration tests with real RabbitMQ and Redis, using disposable containers;
- system/fault tests with real process boundaries and fault points;
- Python gates for Compose, topology, security, performance and release.

The suite is not counted by file size. A test that discovers zero cases or is
silently skipped cannot be used as a proof. The TRX verifier enforces the
required suites, a minimum passing count and the single allowed opt-in skip.

## 7.2 Full regression results

The release verification ran the complete solution in Release mode:

| Suite | Passed | Failed | Skipped |
|---|---:|---:|---:|
| NotificationWorker.UnitTests | 13 | 0 | 0 |
| NotificationService.IntegrationTests | 16 | 0 | 0 |
| B12Audit.Tests | 20 | 0 | 0 |
| PaymentService.UnitTests | 38 | 0 | 0 |
| PaymentService.IntegrationTests | 37 | 0 | 0 |
| Platform.IntegrationTests | 76 | 0 | 0 |
| MatchingService.UnitTests | 115 | 0 | 0 |
| DriverService.IntegrationTests | 120 | 0 | 0 |
| DriverService.UnitTests | 132 | 0 | 0 |
| MatchingService.IntegrationTests | 202 | 0 | 0 |
| TripService.IntegrationTests | 246 | 0 | 0 |
| TripService.UnitTests | 326 | 0 | 0 |
| IdentityService.IntegrationTests | 0 | 0 | 1 |
| **Total** | **1341** | **0** | **1** |

The TRX gate reported `13` executed suites, `1341` passed, `0` failed and one
`NotExecuted` opt-in Identity theory. The dedicated Identity negative lane
executes those cases separately. The build produced zero errors and 19
pre-existing analyzer warnings.

## 7.3 Course minimum mapping

The repository exceeds the course minimum. Functional proof includes ride
creation over HTTP and components, lifecycle, driver agreement, rejection,
payment persistence and notification inbox tests. Concurrency proof includes
driver reserve contention, create-ride concurrency, trip concurrency, payment
concurrency, atomic dispatch fencing, matching attempt uniqueness and
reconciliation races. Fault proof includes deterministic fault processes,
crash-after-reserve recovery, lost reserve responses, Outbox relay, payment
recovery and reconciliation, Redis outage and backlog recovery. Security proof
includes dedicated Identity negatives, driver authorization exposure, database
ownership, message contracts and API boundary tests. Performance proof includes
the W13 campaigns and the W14 backlog experiment.

## 7.4 Concurrency and contention

The W13 E01 experiment drives synchronized authenticated reserve attempts at
1, 10, 25, 50 and 100 contenders. Across three repetitions there were 558 real
HTTP requests, 15 winners and 543 expected domain conflicts. Every point had
exactly one durable winner and one reserve intent, followed by an exact
authenticated release and an independent authority read. The result is not a
latency SLA; it is a correctness result under contention.

## 7.5 Production end-to-end smoke

The release smoke runs the Production Compose overlay in an isolated project.
It applies 11 Trip migrations externally, starts the stack and executes
authenticated journeys through the Gateway:

- a completed ride with authorization, capture and four recipient
  notifications;
- a cancellation after authorization with a void and exact driver release;
- a driver rejection with terminal `MATCH_FAILED/DRIVER_REJECTED`, exact
  release, no payment, and a successful subsequent ride;
- a notification-worker outage during a ride, with delivery caught up once
  after restart;
- a reject commit followed by a Trip process stop/restart, with an exact
  replay.

The final authority audit saw six Trips in `COMPLETED`, `CANCELLED` and
`MATCH_FAILED`, one available Driver with no reservation, six matching rounds,
unique Payment operation keys with matching provider effects and 25 unique
recipient notifications. The 19 checks all passed and teardown preserved an
unrelated sentinel container.

## 7.6 Security results

The security gates cover authentication, authorization, identity spoofing,
input size and validation, and secret handling. The dedicated Identity lane
passes seven negative cases; the platform security gate runs 55 real HTTP
checks. The notification inbox returns 401 for unauthenticated access and 404
for foreign or unknown ids; a client-supplied user id is ignored. The Driver
guard rejects a Driver acting on another Driver's profile and a service token
without the required permission. The secret scan finds no high-confidence
secret in 1,340 tracked paths; a release render without secrets fails closed.

## 7.7 Evidence integrity

Each evidence bundle contains a manifest with the source commit, tree,
command, environment and artifact hashes. Historical bundles are not relabelled
when code changes; a new source requires a new run. The final release record
maps the release commit to the evidence used for this report. The repository
hygiene policy keeps transient logs and agent output out of Git; only curated
results, final-state assertions and manifests are retained.

---

# 8. Performance and high-availability experiments

## 8.1 E01 — Driver contention

Three repetitions at each contender count; latency excludes pre-connection.

| Contenders | Requests across 3 runs | Median P50 (ms) | Median P95 (ms) | P95 range (ms) |
|---:|---:|---:|---:|---:|
| 1 | 3 | 7.1 | 7.1 | 6.9–7.5 |
| 10 | 30 | 14.7 | 20.4 | 19.8–27.9 |
| 25 | 75 | 30.3 | 38.7 | 38.6–44.6 |
| 50 | 150 | 23.4 | 39.5 | 37.6–47.8 |
| 100 | 300 | 46.3 | 76.7 | 67.9–77.1 |

The N=1 point has only three observations and its P95 is not a population
estimate. Latencies include expected conflict responses. The experiment is a
contention correctness proof with a latency observation, not a capacity claim.

## 8.2 E02 — Authenticated public intake

Twelve 30-second constant-rate runs used ten disposable Rider accounts plus a
preflight Rider. All 16,661 requests were classified with zero dropped
iterations, system errors or authentication failures.

| Offered requests/s | Requests | Successes | Expected conflicts | Median actual/s | P50 (ms) | P95 (ms) |
|---:|---:|---:|---:|---:|---:|---:|
| 10 | 903 | 429 | 474 | 10.03 | 4.3 | 15.7 |
| 25 | 2,253 | 488 | 1,765 | 25.02 | 4.0 | 13.0 |
| 50 | 4,502 | 475 | 4,027 | 50.03 | 3.8 | 12.0 |
| 100 | 9,003 | 699 | 8,304 | 100.02 | 3.5 | 11.4 |

The workload has no driver fleet; it measures authenticated intake and
no-driver closure, not completed-ride throughput. Higher offered rates produce
a larger share of fast expected conflicts, so falling aggregate latency is not
evidence that full rides scale faster.

## 8.3 E03 — Asynchronous intake replicas

Nine runs produced 3,600 unique durable intakes and 3,960 deliveries/ACKs,
including 360 deliberate duplicate deliveries without duplicate durable rounds.
Every replica consumed work; broker consumer counts and telemetry identities
matched the real containers.

| Consumers | Unique intakes | Deliveries/ACKs | Median observed ACK/s | Proxy P95 (ms) |
|---:|---:|---:|---:|---:|
| 1 | 1,200 | 1,320 | 17.5 | 43.2 |
| 2 | 1,200 | 1,320 | 18.2 | 42.5 |
| 4 | 1,200 | 1,320 | 17.5 | 42.4 |

The offered rate was 20 unique messages/s; accidental duplicates raise physical
deliveries to about 22/s. The observed rates include management-visibility
delay and are conservative. At this load the experiment does not establish a
saturation bottleneck or a scaling benefit from additional replicas.

## 8.4 E04 — Backlog recovery

Three report-grade runs reset volumes, created a real offline Driver with
authenticated ownership and drove confirmed AMQP input through autonomous
Matching `NO_DRIVER` work during a broker outage.

| Run | Unique confirms | Input/s | Outage (s) | Service/s | First success (s) | Steady (s) | Quiescence (s) |
|---:|---:|---:|---:|---:|---:|---:|---:|
| 1 | 36 | 0.493 | 30.397 | 2.105 | 3.002 | 10.761 | 14.927 |
| 2 | 34 | 0.500 | 30.006 | 2.969 | 3.039 | 7.244 | 10.817 |
| 3 | 34 | 0.493 | 30.438 | 3.459 | 3.086 | 6.413 | 10.468 |

All 104 unique messages plus 3 same-identity duplicates were acknowledged; all
104 unique rows reached `NO_DRIVER`, with exactly 104 result Outbox effects,
unchanged Driver authority and no fabricated reservation. The drain model is
approximate and the result is a bounded study point, not maximum capacity.

## 8.5 High availability

The HA evidence runs a two-outage broker scenario and a primary-loss scenario.
In the primary-loss run the leading broker is killed while
`matching.commands.q` is active; all 19 critical quorum queues retain three
members and two online members with live leaders across three journeys,
returning to three members after restoration. Application process identities
remain unchanged. In the full-broker-stop run, new authorization intents remain
`PENDING` with null publish timestamps while the broker is down and complete
after restoration; a corrected conservation test obtains 24 persistent AMQP
confirmations, observes all 24 undelivered messages after restoration and
verifies 12 Payment effects, 12 dedup receipts and 12 confirmed result Outbox
rows.

These runs demonstrate bounded recovery for the recorded single-host quorum
topology. They do not establish whole-system or multi-region HA.

## 8.6 Threats to validity

- Single host and fixed resource limits; results do not transfer directly to
  other hardware.
- Fixed container images and a frozen configuration; a different deployment
  may behave differently.
- The intake experiments deliberately exclude a driver fleet; they measure
  admission and closure, not full ride throughput.
- The simulator is not a real payment network; its durable idempotency is
  real, but provider latency and failure modes are modelled.
- The HA evidence covers broker membership and recovery, not network
  partitions or multi-region consensus.

---

# 9. Discussion and limitations

The platform's correctness comes from refusing to hide uncertainty. A timeout
is recorded as `UNKNOWN`, not converted into a success or a failure. A duplicate
delivery is absorbed by a receipt committed with the effect, not by a
best-effort check. A stale release is rejected by exact identity, not by a
timestamp heuristic. A committed business change carries its message in the
same transaction, not in a second best-effort call. These choices add code and
operational complexity, but they make the failure windows explicit.

The main trade-off is availability versus consistency at the reservation
boundary. The Driver authority chooses consistency: a reservation conflict is a
normal, fast rejection, and the Matching round must reconcile or try another
candidate. This is appropriate for the single-assignment invariant. The Payment
and Notification paths choose availability with explicit uncertainty: a Payment
outage does not block the Trip commit, and a Notification outage does not block
ride truth; both are recovered asynchronously.

The system does not claim exactly-once delivery. It claims at-least-once
transport with idempotent processing. It does not claim that a distributed
transaction spans services; it uses local atomicity plus durable intent and
compensation. It does not claim that Redis is consistent with PostgreSQL; it
treats Redis as a derived index that may waste work but cannot grant authority.

Limitations are stated in the release notes: the provider is simulated,
notifications are in-app, performance numbers are single-host measurements, and
the human rehearsal/oral defense is a course-process activity. These are not
hidden behind the green test count; they are part of the submission.

---

# 10. Contribution and attribution

| Member | Student ID | Responsibility |
|---|---|---|
| Nguyen Quang Minh | B23DCVT280 | Trip/Booking, lifecycle, Saga orchestration, compensation, Trip Outbox |
| Nguyen Hoang Khanh | B23DCVT228 | Driver/Location, Matching, reservations, concurrency and Driver lifecycle |
| Le Anh Viet | B23DCVT453 | Payment, messaging/platform, shared Outbox relay, reliability infrastructure |

The detailed commit and PR mapping is recorded in
`docs/07-contribution-mapping.md`, the attribution correction record in
`docs/08-credit-balance-correction-2026-11-06.md`, and the machine-readable
old-to-new map in `evidence/integration/credit-balance-2026-11-06/`. The
responsibility table describes ownership of the implemented capabilities; it
is not a claim that every historical commit was originally authored by the
displayed Git author. Two metadata-only rewrites preserved every tree object
and every patch, and their provenance is retained through archive tags and the
attribution map.

---

# 11. Conclusion

The project implements the DS03 ride-hailing workflow as five independently
deployed business services with service-owned databases, synchronous and
asynchronous communication, a persisted Saga, a transactional Outbox,
idempotent consumers, explicit `UNKNOWN` reconciliation, bounded retry and
dead-letter handling, and a non-blocking notification projection. The final
source passes 1,341 tests across 13 suites with zero failures, a 19-check
Production overlay smoke, security and hygiene gates, and controlled
contention, intake, backlog and quorum-broker experiments. The evidence is
anchored to source revisions and retained with its limitations. The system is
therefore a verified course release, not a production service; the report and
the release notes state that boundary explicitly.

---

# 12. References

1. Microsoft. *ASP.NET Core documentation — minimal APIs, authentication and
   Kestrel limits.* <https://learn.microsoft.com/aspnet/core/>
2. Microsoft. *Entity Framework Core documentation — migrations and
   transactions.* <https://learn.microsoft.com/ef/core/>
3. PostgreSQL Global Development Group. *PostgreSQL 18 documentation —
   transactional isolation and row locking.* <https://www.postgresql.org/docs/18/>
4. RabbitMQ. *Quorum queues and publisher confirms.*
   <https://www.rabbitmq.com/docs/quorum-queues>
5. RabbitMQ. *Consumer acknowledgements and dead-lettering.*
   <https://www.rabbitmq.com/docs/confirms>
6. Redis. *Geospatial indexing.* <https://redis.io/docs/latest/commands/geoadd/>
7. OpenTelemetry. *Trace context propagation.*
   <https://opentelemetry.io/docs/concepts/context-propagation/>
8. YARP. *Reverse proxy configuration.*
   <https://microsoft.github.io/reverse-proxy/>
9. xUnit.net. *Testing framework documentation.* <https://xunit.net/>
10. Testcontainers for .NET. *Disposable integration-test infrastructure.*
    <https://dotnet.testcontainers.org/>
11. k6. *Load-testing documentation.* <https://grafana.com/docs/k6/latest/>
12. Internal contracts: `docs/00-project-scope-architecture.md`,
    `docs/01-domain-correctness-contract.md`,
    `docs/02-distributed-workflow-engineering.md`,
    `docs/03-verification-experiments-demo.md`.

---

# Appendix A. HTTP surface

| Method | Path | Actor | Purpose |
|---|---|---|---|
| POST | `/auth/register` | anonymous | Register Rider or Driver |
| POST | `/auth/login` | anonymous | Issue user JWT |
| POST | `/connect/token` | service client | Issue service grant |
| POST | `/drivers` | Driver JWT | Create the actor's Driver profile |
| POST | `/drivers/{id}/online` | Driver JWT | Set AVAILABLE |
| POST | `/drivers/{id}/offline` | Driver JWT | Set OFFLINE |
| POST | `/drivers/{id}/location` | Driver JWT | Submit a location sample |
| POST | `/drivers/{id}/reserve` | Matching service | Reserve for an exact trip |
| POST | `/drivers/{id}/release` | Trip service | Release an exact reservation |
| GET | `/drivers/{id}` | Driver or service | Read Driver state |
| POST | `/trips` | Rider JWT | Create a ride |
| GET | `/trips/{id}` | Rider JWT | Read the rider's trip |
| POST | `/trips/{id}/accept` | Driver JWT | Accept the exact assignment |
| POST | `/trips/{id}/reject` | Driver JWT | Reject the exact assignment |
| POST | `/trips/{id}/start` | Driver JWT | Start an authorized ride |
| POST | `/trips/{id}/complete` | Driver JWT | Complete an in-progress ride |
| POST | `/trips/{id}/cancel` | Rider JWT | Cancel before start |
| GET | `/notifications/me` | Rider or Driver JWT | Read own inbox |
| GET | `/notifications/{id}` | Owner JWT | Read one owned notification |
| POST | `/notifications/{id}/read` | Owner JWT | Mark one owned notification read |

# Appendix B. Evidence index

| Bundle | Content |
|---|---|
| `evidence/team/ds03-functional-closure/` | Reject/notification closure, 1,341-test regression, release-smoke A–D |
| `evidence/team/w16-integration/` | Release candidate assembly, exact-tree CI, automated rehearsals |
| `evidence/team/w15-integration/` | Feature freeze, reports and source mapping |
| `evidence/team/w14-integration/` | Quorum HA, primary loss, E04 backlog recovery |
| `evidence/team/w13-integration/` | E01–E04 performance campaigns, samples and analysis |
| `evidence/team/w12-integration/` | Matching recovery, observability and G4 acceptance |
| `evidence/team/w11-integration/` | Deterministic fault campaign |
| `evidence/member-a/` | Trip/Saga/Outbox bundles |
| `evidence/member-b/` | Driver/Matching bundles, rehearsals and freeze |
| `evidence/member-c/` | Payment/messaging reliability bundles |
| `evidence/integration/` | Attribution provenance and remediation mapping |
| `evidence/release/v1.0.0/` | Final release manifest and verification anchors |
