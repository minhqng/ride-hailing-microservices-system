# Distributed Ride-Hailing System (DS03)

Microservices ride-hailing platform (Grab/Uber-like) built to study a distributed-systems question:
**how independent services preserve safety invariants and eventually recover when requests race, reads are stale, messages duplicate, responses disappear, and processes crash.**

> Thesis from `docs/00-project-scope-architecture.md`: ride-hailing is the vehicle, not the goal.
> Correctness = explicit authority + local atomicity + stable identity + guarded transition + durable intent + idempotency + explicit UNKNOWN + eventual recovery.

## 1. Architecture overview

5 authoritative services + 1 worker + 1 gateway. No shared DB, no cross-service ACID.

```text
Clients (Rider/Driver/Test)
  │ HTTP
  ▼
API Gateway (YARP, no DB)
  ├── Identity ── identity_db
  ├── Driver ──── driver_db ──┐
  ├── Trip ────── trip_db     │ ReserveDriver (sync HTTP)
  ├── Matching ── matching_db + Redis GEO ─┘
  ├── Payment ─── payment_db
  └── Notification worker ─── notification_db
              ↕ RabbitMQ (async commands/events)
```

| Component | Owner | DB / State | HTTP | Notes |
|---|---|---|---:|---|
| Gateway | C | none | 8080 | routing, auth forwarding, edge diagnostics |
| Identity | Shared/A | `identity_db` | 8101 | users, roles, auth baseline |
| Driver/Vehicle/Location | B | `driver_db` | 8102 | **exclusive reservation authority (PostgreSQL OCC, never Redis lock)** |
| Trip/Booking | A | `trip_db` | 8103 | Trip FSM, cancellation cleanup, W8 messaging; persisted Saga planned |
| Matching | B | `matching_db` + Redis | 8104 | MatchingRound, deterministic ranking, Redis is derived only |
| Payment Simulator | C | `payment_db` | 8105 | durable `operationKey` idempotency, UNKNOWN reconcile, finite retry |
| Notification Worker | C | `notification_db` | — | async side-effect only, never business truth |

Infra: PostgreSQL 5432, Redis 6379, RabbitMQ AMQP 5672 / UI 15672.
Pinned images: `postgres:18.6`, `rabbitmq:4.3.6-management`, `redis:8.10.2`, obs stack Jaeger/OTel/Prometheus/Grafana via `--profile obs`.

## 2. Tech stack

- .NET 10 (`net10.0`, SDK `10.0.100` via `global.json`), ASP.NET Core minimal APIs
- PostgreSQL 18 (per-service logical DB + isolated role), Redis 8 GEO, RabbitMQ 4 quorum queues
- BuildingBlocks: `RideHailing.Contracts` (shared contract foundation), `RideHailing.Messaging` (`MessageEnvelope<T>`, W3C trace, `ConfirmedPublisher`, `ManualAckConsumer`), `RideHailing.Observability` (OTel)
- Docker Compose (DNS discovery: `postgres`, `redis`, `rabbitmq`, `identity`, `driver`, `trip`, `matching`, `payment`), xUnit Unit/Integration/System tests

## 3. Distributed guarantees (what makes this distributed)

**Authority:** Driver reservation linearizes at guarded PostgreSQL `UPDATE ... WHERE status=AVAILABLE AND version=:v`. Redis may nominate, never reserve. Trip owns FareSnapshot + Saga, Payment owns money effect, Matching owns round state.

**Identities (never substitute):** `tripId`, `matchingRound`, `reservationId` (fencing token), `operationKey`, `messageId`, `correlationId`, `traceId`.

**Safety (never):** 1 driver → 2 trips, 1 trip → 2 drivers, stale release frees newer reservation, duplicate payment effect, terminal trip reactivation.

**Liveness targets:** pending Outbox publishes, UNKNOWN reconciles, Saga progresses/compensates/`REQUIRES_REVIEW`. Payment supports explicit reconciliation now; autonomous Outbox/Saga recovery remains planned.

**Messaging:** at-least-once + `MessageEnvelope<T>` + publisher confirms + manual ACK after commit + bounded retry (3) + `<queue>.dead` + `ride.quarantine.q` + consumer dedup. Timeout = `UNKNOWN`, reconcile with same key, no blind retry, no hedging on side-effects. See `docs/messaging-runtime.md`, `docs/02-distributed-workflow-engineering.md`.

**Workflows:** Trip→Matching `RideRequested` (async), Matching→Driver `ReserveDriver` (sync + UNKNOWN handling), Trip→Payment (async Saga), events→Notification (async). Outbox for dual-write gap, persisted Saga/MatchingRound for crash-resume.

Implemented on main through W08: PG-guarded Driver reservation foundation, Trip lifecycle and single-assignment fencing, durable cancellation cleanup, opt-in Trip publication/consumption with atomic receipt dedup, Payment durable simulator, and the RabbitMQ runtime (publisher confirms, manual ACK, dedup). Planned: Trip persisted Saga, Outbox relay and automatic cleanup dispatch.

Closed on this branch (W08 round-lineage hardening): `DriverReservedV1` now carries `matchingRound` from durable Driver-side provenance — a `reserved_round` column recorded on the reserve win, preserved across restart/replay, and cleared with the reservation refs on release/trip start. The round rides the Matching→Driver reserve request and the Driver→Trip event, so Trip no longer parks verified deliveries solely for a missing round; the `NeedsReview:RESERVATION_ROUND_REQUIRED` path remains only for legacy/deploy-skew payloads that omit the field. Payment outcomes remain a durable simulator skeleton awaiting Saga operation lineage.

## 4. Prerequisites

- Docker + Compose
- No host .NET needed — build/test via `mcr.microsoft.com/dotnet/sdk:10.0` container
- `cp .env.example .env` (all secrets are local-dev placeholders, never commit real ones)

## 5. Quick start

```bash
cp .env.example .env
docker compose up -d --build
curl http://localhost:8080/health/live
curl http://localhost:8101/health/live
curl http://localhost:8102/health/live
curl http://localhost:8103/health/live
curl http://localhost:8104/health/live
curl http://localhost:8105/health/live
# RabbitMQ UI: http://localhost:15672 (ride_app / ride_dev_pw)
docker compose ps
docker compose --profile obs up -d  # optional: Jaeger 16686, Prometheus 9090, Grafana 3000, OTel 4317/4318
docker compose down
```

Payment simulator (Development-only, not mobile API):

```bash
curl -X POST http://localhost:8105/simulator/operations \
  -H 'Content-Type: application/json' \
  -d '{"operationKey":"trip-demo:authorize:v1","tripId":"11111111-1111-1111-1111-111111111111","type":"Authorize","amountMinor":12000,"currency":"VND","mode":"Success"}'
curl http://localhost:8105/simulator/operations/{id}
curl -X POST http://localhost:8105/simulator/operations/{id}/reconcile
```

Details: `src/Services/Payment/RideHailing.PaymentService/README.md`.

### Enable Trip W8 messaging

Apply Trip EF migrations through `W08_TripMessageReceipts` against the Trip-owned
database first; startup does not auto-migrate. With the .NET SDK and the owned
connection string supplied as `ConnectionStrings__Postgres`:

```bash
dotnet tool restore
dotnet ef database update --project src/Services/Trip/RideHailing.TripService
# Set TRIP_MESSAGING_ENABLED=true in .env, then recreate Trip:
docker compose up -d --build trip
curl http://localhost:8103/health/ready
```

Messaging is disabled by default. When enabled, readiness includes migrations and
broker consumers. Authenticated POST /trips publishes RideRequested; a 503
MATCHING_PUBLICATION_PENDING means the ride was saved and the caller should retry
the same Idempotency-Key/body. W8 still has a DB/publish gap until the W9 Outbox.
The production authenticated client flow depends on shared Identity/Gateway integration.

## 6. Common commands

```bash
bash scripts/test/quick.sh              # build + test in SDK container
docker compose config                   # validate compose
python scripts/test/platform-smoke.py   # platform smoke (disposable project only)
python scripts/test/topology-smoke.py   # RabbitMQ topology smoke (disposable broker only)
python scripts/test/payment-smoke.py    # payment kill-restart + replay proof (disposable only)
bash scripts/dev/up.sh
bash scripts/dev/down.sh
# scripts/dev/reset.sh is for deliberately resetting disposable development state.
```

## 7. Repository layout

```text
src/Gateway|Services|Workers|BuildingBlocks — 7 deployables + 3 libs
tests/Unit|Integration|System — xUnit + real-infra suites
deploy/postgres|rabbitmq|redis|otel|prometheus|grafana — pinned configs
scripts/dev|test|fault|evidence — reproducible ops
docs/00..05A/B/C + messaging-runtime.md — canonical specs
compose.yaml, .env.example, RideHailing.Distributed.slnx
```

## 8. Docs reading order

`docs/README.md`: `00` scope/architecture → `01` correctness contract → `02` workflow/reliability → `03` verification/demo → `04` team plan → `05A/B/C` member playbooks. Priority: 00–03 > 04 > 05. Code wins only for exact patch/config after approved freeze.

## 9. Team workflow

Branches: `main`, `feature/<issue>-<name>`, `fix/<issue>-<name>`, `experiment/<id>-<name>`. Conventional commits (`feat:`, `fix:`, `test:`, `docs:`, `refactor:`, `chore:`, `perf:`), small PRs, 1 reviewer min. No long-lived member branches. See `CONTRIBUTING.md`, Doc 04 §3.3.

## 10. Status & roadmap

Implemented on main through W08: compose core/obs profiles, per-service DB roles, bounded readiness, OTel traces/metrics, CI smoke definitions, Payment durable simulator, RabbitMQ confirms/manual ACK, Driver reservation foundation, Trip lifecycle/assignment fencing, durable cancellation cleanup and Trip atomic consumer receipts.

Verified on main (CI run 38912031301, reproduced locally on identical tree): **851 tests passed, 0 failed, 1 env-gated skip**, using real PostgreSQL 18.6 and RabbitMQ 4.3.6 for messaging proofs. The Matching-style integration sink is a test fixture; this is not a complete production dispatch demo.

Next: human peer review, Trip Saga persistence,
Outbox relay, automatic cleanup/reconciliation dispatch, faultpoint orchestration,
and the end-to-end authenticated Trip/Saga flow. (`DriverReservedV1` matchingRound
alignment has landed on this branch; see §3.)
