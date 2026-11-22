# Driver Service

Driver is the exclusive authority for Driver state and reservation. A
reservation linearizes in one short PostgreSQL transaction, and no other
service may grant or release it.

- Database: `driver_db` (role `driver_app`), PostgreSQL through Npgsql.
- Depends on: `ConnectionStrings__Postgres`, `RabbitMq__Host`.

## Authority and state machine

```text
OFFLINE <-> AVAILABLE
AVAILABLE -> RESERVED -> ON_TRIP -> AVAILABLE
RESERVED -> AVAILABLE (exact release only)
```

The aggregate guards state/reservation coherence: only `RESERVED` may carry a
trip, reservation and matching-round identity. Reservation writes use
row-locked, version-fenced compare-and-set: `SELECT ... FOR UPDATE` serializes
contenders, the aggregate re-validates the fresh row, and the commit that
writes the reservation plus its Outbox intent is the linearization point.

Reserve and release are idempotent for the same reservation identity. A stale
version, a foreign live reservation, or an incoherent end state returns a
conclusive conflict and never mutates the row. Release returns success only
for the exact current token or an already coherent `AVAILABLE` end state.

## HTTP surface

| Method | Route | Purpose |
|---|---|---|
| POST | `/drivers` | Create a Driver profile |
| GET | `/drivers/{id}` | Read state, version and current reservation |
| POST | `/drivers/{id}/online` | `OFFLINE -> AVAILABLE` with expected version |
| POST | `/drivers/{id}/offline` | `AVAILABLE -> OFFLINE` with expected version |
| POST | `/drivers/{id}/reserve` | Matching reserves an exact trip and round |
| POST | `/drivers/{id}/release` | Exact-token release |
| POST | `/drivers/{id}/location` | Ingress a simulated location sample |
| GET | `/internal/drivers/{id}/reservations/{reservationId}` | Identity-aware reservation reconciliation |

A structured `404 {"code":"DRIVER_NOT_FOUND"}` is authoritative absence. A bare
404 is transport uncertainty and must not be read as "no effect committed".

## Broker contracts

`driver.commands.q` consumes `MarkDriverTripStartedV1`,
`MarkDriverTripCompletedV1` and `ReleaseDriverReservationV1`. Each command is
executed under a message advisory lock with a durable receipt, an exact
Driver/trip/reservation/round guard, lifecycle history and an outgoing event
intent in the same transaction. ACK follows commit; identical retries return
the original receipt.

Driver state events (`DriverReservedV1`, `DriverAvailableV1`,
`DriverOfflineV1`, `DriverReservationReleasedV1`) and location events
(`DriverLocationAcceptedV1`) are written to the Outbox in the same transaction
as the domain change. `DriverReservedV1` carries the matching round recorded at
the reserve win, so one logical reservation publishes one stable round across
replay and restart.

## Configuration

| Key | Meaning |
|---|---|
| `RabbitMq:CommandsEnabled` | Consume Driver lifecycle commands (default `true`) |
| `RabbitMq:PublishingEnabled` | Host the Outbox relay (intent persistence is unaffected) |
| `RabbitMq:Host`, `RabbitMq:Hosts`, `RabbitMq:Username`, `RabbitMq:Password` | Broker connection |
| `Location:MaxFutureSkew`, `Location:MaxPastAge` | Location freshness policy |
| `DriverFaultPoints:*` | Opt-in, non-Production fault control endpoints |

Location samples outside the freshness window are rejected. A valid sample is
published as a derived projection input; Redis failure never blocks the
Driver authority path.

## Verification

- `tests/Unit/RideHailing.DriverService.UnitTests` — state machine, coherence,
  OCC/fencing, idempotency, event mapping and metric semantics.
- `tests/Integration/RideHailing.DriverService.IntegrationTests` — real
  PostgreSQL and RabbitMQ: persistence, migrations, reserve/release races,
  lifecycle commands, Outbox publication, broker consumption and process
  crash/restart.
- `tests/Audit/RideHailing.InvariantAudit.Tests` — cross-service invariant audit.
