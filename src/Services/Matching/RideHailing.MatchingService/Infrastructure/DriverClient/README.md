# Driver HTTP seam

Typed client used by Matching to read Driver state, reserve, release and
reconcile a reservation. This directory contains contracts and mapping only;
reservation behavior belongs to the Driver service and the reserve workflow
belongs to `Application/Reserve`.

## Reserve — `POST /drivers/{driverId}/reserve`

Body: `{ "tripId": "<uuid>", "reservationId": "<uuid>", "expectedVersion": <int>, "matchingRound": <int> }`.

`reservationId` is stable across retry, reconcile and restart. `matchingRound`
is the durable matching generation; Driver records it write-once at the win so
`DriverReservedV1` carries the same value across replay. A non-positive round
is a 400 and is never normalized.

| HTTP | Meaning | Client mapping |
|---|---|---|
| 200 `{status: RESERVED, version, ...}` | Authoritative commit | `Reserved(version)` |
| 409 `{code}` | Conclusive conflict (`DRIVER_NOT_AVAILABLE`, `STALE_VERSION`) | `Rejected(code)` |
| 404 structured `DRIVER_NOT_FOUND` | Authoritative absence | `Rejected(DRIVER_NOT_FOUND)` |
| 5xx, timeout, connection drop | Transport uncertainty | Throw; caller keeps `UNKNOWN` and reconciles the same id |

## Version read — `GET /drivers/{driverId}`

Returns the current version and status for the read-then-reserve OCC dance.
A structured `DRIVER_NOT_FOUND` is conclusive. A bare 404 is transport
uncertainty.

## Release — `POST /drivers/{driverId}/release`

Body: `{ "tripId": "<uuid>", "reservationId": "<uuid>" }`. The exact token is
the complete guard. Exact release frees the Driver; a coherent `AVAILABLE`
state is an idempotent no-op; a different live reservation is
`409 STALE_RESERVATION` and is never freed.

## Reconcile — `GET /internal/drivers/{driverId}/reservations/{reservationId}`

The 200 body carries one canonical outcome:
`OWNED_BY_THIS_RESERVATION`, `NOT_COMMITTED_OR_AVAILABLE`,
`RESERVED_BY_OTHER`, `OFFLINE`, `ON_TRIP` or `NOT_FOUND`. Transport failure
leaves the caller `UNKNOWN`; an unknown outcome string fails loud.

A bare 404 is never a conclusive `NOT_FOUND`; `NOT_FOUND` arrives only as a
200 outcome. Advancing on a transport failure could double-reserve.

## Broker seam

Matching consumes `RideRequestedV1` from `trip.ride-requested.v1` and Driver
state/location events from `driver.*.v1`. Both use the shared validated
`MessageEnvelope` with trace headers; ACK happens only after the durable local
effect commits. Driver authoritative events are written by Driver's Outbox and
are not produced by Matching.

Transient requeue is bounded by the broker acquisition count. Unknown routing
keys and bodies over 1 MiB are poison and are dropped loudly.
