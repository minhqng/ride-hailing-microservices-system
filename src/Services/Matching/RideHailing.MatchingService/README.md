# Matching Service

Matching owns durable matching rounds, attempts, deterministic candidate
ranking and the derived Redis GEO projection. It never writes the Driver
database; a reservation becomes authoritative only when Driver commits it.

- Database: `matching_db` (role `matching_app`), PostgreSQL through Npgsql.
- Depends on: `ConnectionStrings__Postgres`, `Redis__ConnectionString`,
  `RabbitMq__Host`, `Services__DriverBaseUrl`.
- Contracts: [domain invariant contract](../../../../docs/01-domain-correctness-contract.md)
  and [workflow contract](../../../../docs/02-distributed-workflow-engineering.md).

## Intake and execution

`RideRequestedV1` is a durable intake, not an execution shortcut. The handler
commits the round, pickup, input envelope identity, `auto_execute = TRUE` and
any pending cancellation tombstone in one transaction before ACK.

A hosted `MatchingWorkDispatcher` scans `PROCESSING` rounds from PostgreSQL,
takes a per-round advisory-lock lease, reconciles unresolved attempts with the
same reservation identity, and only then dispatches the next candidate. A
crashed process drops its lease; the next scan resumes the round. There is no
in-memory work queue.

`CancelMatchingRoundV1` is consumed from `matching.commands.q`. The durable
tombstone survives a cancel that overtakes its ride request; the worker starts
no new attempt and reconciles an unresolved effect query-only.

## Candidate pipeline

1. Load or create the round (`PROCESSING`) and persist the pickup center.
2. `GEOSEARCH` nearest-first from Redis with bounded oversampling.
3. Filter by authority-safe eligibility: `AVAILABLE`, fresh location,
   capability policy and prior rejection.
4. Rank deterministically: distance, optional freshness weight, Driver-id
   tie-break.
5. Persist the frozen snapshot and a `READY` attempt ledger with stable
   reservation identities.

Only conclusively rejected attempts advance a stage. An unresolved attempt
blocks new candidates. `NO_DRIVER` is legal only at the final configured stage
whose discovery completed, and its terminal result and
`MatchingFailedV1` Outbox intent commit together.

## Redis projection

`driver:projection:{id}` carries status/version and session-scoped location;
`drivers:geo` is the candidate index. One Lua function applies a version guard
and a per-session sequence/freshness guard atomically and recomputes GEO from
the current values. A foreign session is never auto-accepted. Redis is derived
state: a failure degrades candidate discovery and can waste work, but it can
never grant a reservation.

## HTTP surface

| Method | Route | Purpose |
|---|---|---|
| POST | `/internal/matching/rounds` | Demo/regression trigger, mapped only outside Production |
| GET | `/internal/matching/rounds/{tripId}/{round}/cancellation` | Durable cancellation state |
| POST | `/internal/test/matching/reserve` | Test-only reserve driver, non-Production |
| POST | `/internal/test/matching/...` | Test-only reconciliation controls, non-Production |

Production rounds are created by `RideRequestedV1` and driven by the worker.
The internal test routes never initiate work in Production.

## Configuration

| Key | Meaning |
|---|---|
| `Matching:Stages` | Strictly increasing `S0..Sn` radii and candidate bounds |
| `Matching:LocationFreshnessWindow` | Maximum accepted location age |
| `Matching:Worker:*` | Poll interval, batch size, concurrency and reconcile backoff |
| `DriverClient:TimeoutSeconds` | Driver attempt budget |
| `RabbitMq:PublishingEnabled` | Host the terminal Outbox relay |
| `Services:DriverBaseUrl` | Driver HTTP base address |

A persisted stage missing from the running configuration yields a durable
`STAGE_CONFIGURATION_MISMATCH`; reconciliation exhaustion keeps the attempt
`UNKNOWN` and the round `PROCESSING`. Neither case invents a terminal result.

## Verification

- `tests/Unit/RideHailing.MatchingService.UnitTests` — ranking, eligibility,
  attempts, terminal contracts, projection keys and broker decisions.
- `tests/Integration/RideHailing.MatchingService.IntegrationTests` — real
  PostgreSQL, Redis and RabbitMQ plus real Driver Kestrel endpoints: intake,
  reservation, reconciliation, cancellation, stage progression, migration
  repair, process crash/restart and projection recovery.
