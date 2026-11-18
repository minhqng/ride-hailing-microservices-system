# Trip Service

Trip is the authority for the ride lifecycle. It owns the `Trip` aggregate, the
fare snapshot, the current Driver assignment reference, the persisted
`RideSaga`, and the transactional Outbox that carries every outgoing command.

- Database: `trip_db` (role `trip_app`), PostgreSQL through EF Core.
- Depends on: `ConnectionStrings__Postgres`, `RabbitMq__Host`,
  `Services__DriverBaseUrl`, `Services__MatchingBaseUrl`.
- Contracts: [domain invariant contract](../../../../docs/01-domain-correctness-contract.md)
  and [workflow contract](../../../../docs/02-distributed-workflow-engineering.md).

## Authority and state machine

Trip does not decide Driver reservability; Driver PostgreSQL is the sole
reservation authority. Trip records the authoritative result and fences it
against the current matching round and assignment identity.

```text
CREATED -> MATCHING -> DRIVER_RESERVED -> DRIVER_ACCEPTED
        -> READY_TO_START -> IN_PROGRESS -> COMPLETED
```

`CANCELLED` and `MATCH_FAILED` are terminal. Terminal states never reactivate;
late facts create cleanup intentions only. `MATCH_FAILED` stores a durable
`failure_reason` (`NO_DRIVER_AVAILABLE` or `DRIVER_REJECTED`).

The Saga owns cross-service progress: `AUTHORIZE_PENDING` → `AUTHORIZED` →
`CAPTURE_PENDING` → `CAPTURED`, or compensation through
`RELEASE_PENDING`/`VOID_PENDING` → `COMPENSATED`, with `REQUIRES_REVIEW` when
authority cannot be resolved. Operation keys are stable per trip and purpose
(`{tripId}:authorize|capture|void:v1`).

## HTTP surface

| Method | Route | Actor |
|---|---|---|
| POST | `/trips` | Rider (Idempotency-Key required) |
| GET | `/trips/{tripId}` | Rider |
| POST | `/trips/{tripId}/cancel` | Rider |
| POST | `/trips/{tripId}/accept` | Driver |
| POST | `/trips/{tripId}/reject` | Driver |
| POST | `/trips/{tripId}/start` | Driver |
| POST | `/trips/{tripId}/complete` | Driver |

The endpoints consume an already-authenticated principal; Gateway and the
shared security building block perform JWT validation. Driver routes resolve
the caller's Driver profile through the Driver service and never trust a
client-supplied actor identity.

## Durable messaging

Every external obligation is appended to `outbox_messages` in the same EF unit
of work as the business transition. The shared relay claims, publishes with
publisher confirms, and marks the intent only after the broker accepts it.
`RideRequestedV1` goes to `ride.events`; matching cancellation, Driver release
and Driver lifecycle commands go to `ride.commands`. Payment outcomes and
Driver facts arrive through manual-ACK consumers; Trip effects and message
receipts commit in one transaction.

The Saga can replay the same `messageId` after a crash and resumes from the
durable `step_version`. A completed Trip never regresses when capture is
uncertain; the Saga records the unresolved step for review instead.

## Configuration

| Key | Meaning |
|---|---|
| `TripMessaging:Enabled` / `TRIP_MESSAGING_ENABLED` | Starts consumers and the Outbox relay |
| `TripDatabase:AutoMigrate` / `TRIP_AUTO_MIGRATE` | Applies EF migrations at startup (local/CI only) |
| `RabbitMq:Host`, `RabbitMq:Hosts`, `RabbitMq:Username`, `RabbitMq:Password` | Broker connection |
| `TripFaultPoints:Enabled`, `Activated`, `SignalDirectory` | Opt-in, non-Production fault points |
| `Services:DriverBaseUrl`, `Services:MatchingBaseUrl` | Driver directory and cleanup endpoints |

Readiness requires an owned database and zero pending migrations; when
messaging is enabled it also checks consumer connectivity. The relay gates on
database readiness and an independent publish-path probe, so a broker outage
leaves rows `PENDING` instead of burning retry attempts.

## Verification

- `tests/Unit/RideHailing.TripService.UnitTests` — state transitions, fare,
  idempotency fingerprint, Saga transitions and outbox intents.
- `tests/Integration/RideHailing.TripService.IntegrationTests` — real
  PostgreSQL and RabbitMQ through Testcontainers: persistence, migrations,
  messaging, Outbox relay, Saga, fault points, process crash/restart,
  authorization negatives, telemetry and the invariant auditor.
- `scripts/test/quick.sh` runs the complete solution suite;
  `scripts/test/release-smoke.py` exercises the production overlay end to end.
