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
  Outbox, A-owned): payload fields `tripId` + `matchingRound` (A's five-field
  shape unchanged); the input message identity is `envelope.messageId`
  verbatim (opaque TEXT, never parsed as GUID, never transformed). Unknown
  extra payload fields ignored; a payload-embedded `inputMessageId` is NOT
  required and, if present, is ignored. Same `(tripId, matchingRound)`
  resumes the same round — never a new candidate sequence, never reserve
  execution from broker input. Duplicate/concurrent/crash-before-ACK delivery
  converges on the round PK; stored `input_message_id` is never overwritten
  on replay while UNIQUE(input_message_id) rejects one identity for two
  rounds.
- **Consumed — Driver state/location** (`driver.*.v1`, producer: Driver, B-owned):
  state `{driverId, status, version}` and location
  `{driverId, longitude, latitude, sessionId, sequenceNo, capturedAt}` feed the
  derived Redis projection only (version/freshness/GEO guards stay in the W4 Lua).
  Degraded results requeue boundedly; unknown routing keys are poison (drop loud).
- **Produced — Driver authoritative events** (best-effort after commit, W9
  Outbox closes the dual-write gap; failure never fails the committed domain op):
  `driver.reserved.v1`, `driver.reservation-released.v1`, `driver.available.v1`,
  `driver.offline.v1` with `{driverId, status, version, tripId?, reservationId?}`,
  and `driver.location-accepted.v1` with the accepted sample. Payload wire form
  is explicit camelCase; messageId is deterministic per logical effect
  (win/replay/recovery republish the same identity).
- **Bounds:** transient requeue bounded by the broker acquisition count
  (`x-acquired-count`, RabbitMQ 4.x quorum; headerless redelivery fails closed);
  1 MiB body cap; validated queue/prefetch/retry config; broker outage retries
  with backoff without killing the host.
- **NOT in W8:** Outbox/Inbox, `MatchingFailedV1`/`NO_DRIVER` publication,
  Saga compensation, DLQ/error-store arrival (C/W9 scope).

### Open broker questions for A/C

- [ ] A: confirm `RideRequestedV1` producer payload carries
  `tripId`/`matchingRound` (A's five-field shape unchanged; identity travels
  as `envelope.messageId`, not a payload field) and the
  `trip.ride-requested.v1` routing key.
- [ ] C: converge B's interim consume/publish glue onto the platform
  consumer/publisher base (same exchanges/queues/envelope/ACK discipline);
  confirm DLQ/error-store arrival story for W9.
- [ ] C: broker-enforced maximum message size (B drops >1 MiB on consume).

## What this is NOT

Not reserve behavior, not UNKNOWN classification, not retry policy — W6/W7.
This directory is interface + shapes + mapping only.
