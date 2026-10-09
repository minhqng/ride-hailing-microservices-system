# Driver HTTP seam — freeze note (B-W02-08, landed W6/W7)

- **Status:** LANDED on the Driver side (W6 reserve, W7 release/reconcile);
  A/C sign-off still open on the questions below.
- **Owner:** B (Matching side). Driver-side endpoints land in W6/W7.
- **Canonical source:** Doc 02 §12.5–12.6, §19.3–19.4, §19.6–19.7.

## Landed contracts (W6/W7)

### Reserve — `POST drivers/{driverId}/reserve`

Body: `{ "tripId": "<uuid>", "reservationId": "<uuid>", "expectedVersion": <int> }`
(caller-observed version; stale fence yields `STALE_VERSION`, never overwrite).
`reservationId` is stable per logical attempt across retry/reconcile/restart.

| HTTP | Meaning | Client maps to |
|---|---|---|
| 200 `{status: RESERVED, version, ...}` | authoritative commit | `Reserved(version)` |
| 409 `{code}` | conclusive conflict (e.g. `DRIVER_NOT_AVAILABLE`, `STALE_VERSION`) | `Rejected(code)` — must NOT trip breaker |
| 404 | driver unknown (fresh attempt) | `Rejected(DRIVER_NOT_FOUND)` — EXCEPTION: same-`reservationId` retry receiving 404 is a transport anomaly (wrong server/route, since a correct server answers idempotent success); caller must reconcile, not advance |
| 5xx / timeout / connection drop | transport uncertainty | **throw** → caller classifies UNKNOWN, reconciles same id |

Rules: no generic retry (`02 §19`), no hedging (`02 §19.7`), 409 is not
infrastructure failure (`02 §19.4`), never assume success on unavailability
(`02 §19.6`).

### Version read — `GET drivers/{driverId}`

200 `{version, status, ...}` for the read-then-attempt OCC dance; 404 means
the driver is unknown (conclusive: start no attempt).

### Release — `POST drivers/{driverId}/release`

Body: `{ "tripId": "<uuid>", "reservationId": "<uuid>" }` (exact token, no
version — token equality on the locked row is the complete guard). Exact
token frees (200), coherent AVAILABLE is an idempotent cleanup no-op (200),
a live foreign reservation conflicts (409 `STALE_RESERVATION`).

### Reconcile — `GET internal/drivers/{driverId}/reservations/{reservationId}`

200 `{outcome}` where outcome ∈ `OWNED_BY_THIS_RESERVATION`,
`NOT_COMMITTED_OR_AVAILABLE`, `RESERVED_BY_OTHER`, `OFFLINE`, `ON_TRIP`,
`NOT_FOUND` (`02 §12.6`). Transport failure ⇒ caller treats as
`DEPENDENCY_UNAVAILABLE` and stays UNKNOWN. Unknown outcome string ⇒ throw
(drift fails loud).
A BARE 404 (no domain body) is NEVER a conclusive `NOT_FOUND`: it may be a
wrong BaseAddress, proxy, or missing route (DSI-031 — advancing on it could
double-reserve). The client throws; `NOT_FOUND` arrives only as a 200 outcome.

## Open questions for A/C

- [ ] A: auth story for `/internal/*` (02 §3066: not unprotected public).
- [ ] A: confirm the landed 409 code set (`DRIVER_NOT_AVAILABLE`,
  `STALE_VERSION`, `STALE_RESERVATION`) suffices for Trip-side branching.
- [ ] A: reconcile for a reservation whose driver row was since removed is
  currently `NOT_FOUND` (conclusive failure — may advance). B's rationale:
  no delete API exists and vehicles hold `RESTRICT`, so a missing row means
  no effect could have committed; operator-delete stays out of threat model.
  Confirm or supply the canonical rule.
- [ ] C: attempt-timeout/total-deadline budgets vs `02 §20` (operational
  tuning, not frozen; W7 uses bounded counts).

## Broker seam (B-W08)

Consumed (Matching) and produced (Driver) broker contracts. Shared platform
primitives (exchanges/queues/envelope/topology/retry-DLQ mechanics) are
C-owned; B consumes the frozen shape and owns only its endpoint mapping.

- **Topology:** frozen `RideTopology` (`ride.events` topic; `matching.ride-requested.q`,
  `matching.driver-projection.q` quorum). B declares idempotently, never redefines.
- **Envelope:** shared `MessageEnvelope` (V1, validated) + trace headers.
  ACK happens only after the durable local effect commits.
- **Consumed — `RideRequestedV1`** (`trip.ride-requested.v1`, producer: Trip
  Outbox, A-owned): payload uses A's `RideRequestedV1` shape (`tripId`, `matchingRound`,
  `pickup`, `dropoff`, `requestedAt`). The durable input identity comes from
  envelope `messageId`; an optional legacy payload `inputMessageId` must agree. Same `(tripId, matchingRound)`
  resumes the same round — never a new candidate sequence, never reserve
  execution from broker input. Duplicate/concurrent/crash-before-ACK delivery
  converges on the round PK.
- **Consumed — Driver state/location** (`driver.*.v1`, producer: Driver, B-owned):
  state `{driverId, status, version}` and location
  `{driverId, longitude, latitude, sessionId, sequenceNo, capturedAt}` feed the
  derived Redis projection only (version/freshness/GEO guards stay in the W4 Lua).
  Degraded results use confirmed bounded retry; unknown routing keys transfer to the error queue.
- **Produced — Driver authoritative events** (best-effort after commit, W9
  Outbox closes the dual-write gap; failure never fails the committed domain op):
  `driver.reserved.v1`, `driver.reservation-released.v1`, `driver.available.v1`,
  `driver.offline.v1` with `{driverId, status, version, tripId?, reservationId?}`,
  and `driver.location-accepted.v1` with the accepted sample. Payload wire form
  is explicit camelCase; messageId is deterministic per logical effect
  (win/replay/recovery republish the same identity).
- **Bounds:** the shared C `ManualAckConsumer` owns validation, ACK, retry and
  confirmed error transfer. `MaxRedeliveries` is the total handler-call budget
  (1..4, default 3); the platform caps retries at three. Original routing keys
  survive direct-to-queue retries. Broker cancellation/channel loss faults
  `Completion`; the Matching supervisor disposes and reconnects with backoff.
- **Error handling:** sanitized failures are confirmed to
  `matching.ride-requested.q.dead` / `matching.driver-projection.q.dead`
  before ACK. Failed handoff leaves the original unacknowledged.
- **Later scope:** atomic Outbox/Inbox and `MatchingFailedV1` publication
  remain W9. The W8 round consumer commits round intake only; the production
  Trip start/publish and Matching candidate pipeline are separate A/B work.

### Cross-owner integration

`BrokerConsumeTests.Trip_Contract_Through_Shared_Publisher_Converges_With_Envelope_Identity`
uses A's actual Trip aggregate/payload, C's confirmed publisher/manual consumer,
real RabbitMQ and B's PostgreSQL round ledger. This proves wire compatibility
and duplicate convergence; it is not proof of a running Trip API producer,
full dispatch workflow, or human A/B review approval.

## What this is NOT

Not reserve behavior, not UNKNOWN classification, not retry policy — W6/W7.
This directory is interface + shapes + mapping only.
