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
| Trip/Booking | A | `trip_db` | 8103 | Trip FSM, transactional Outbox, persisted Saga, exact compensation and forward recovery |
| Matching | B | `matching_db` + Redis | 8104 | MatchingRound, deterministic ranking, Redis is derived only |
| Payment Simulator | C | `payment_db` | 8105 | durable `operationKey` idempotency, UNKNOWN reconcile, finite retry |
| Notification Worker | C | `notification_db` | — | async side-effect only, never business truth |

Infra: PostgreSQL 5432, Redis 6379, RabbitMQ AMQP 5672 / UI 15672.
Pinned images: `postgres:18.6`, `rabbitmq:4.3.6-management`, `redis:8.10.2`, obs stack Jaeger/OTel/Prometheus/Grafana via `--profile obs`.

## 2. Tech stack

- .NET 10 (`net10.0`, SDK `10.0.100` via `global.json`), ASP.NET Core minimal APIs
- PostgreSQL 18 (per-service logical DB + isolated role), Redis 8 GEO, RabbitMQ 4 quorum queues
- BuildingBlocks: `RideHailing.Contracts` (shared contract foundation), `RideHailing.Messaging` (`MessageEnvelope<T>`, W3C trace, `ConfirmedPublisher`, `ManualAckConsumer`, shared Outbox and `processed_messages` dedup primitives), `RideHailing.Observability` (OTel)
- Docker Compose (DNS discovery: `postgres`, `redis`, `rabbitmq`, `identity`, `driver`, `trip`, `matching`, `payment`), xUnit Unit/Integration/System tests

## 3. Distributed guarantees (what makes this distributed)

**Authority:** Driver reservation linearizes at guarded PostgreSQL `UPDATE ... WHERE status=AVAILABLE AND version=:v`. Redis may nominate, never reserve. Trip owns FareSnapshot + Saga, Payment owns money effect, Matching owns round state.

**Identities (never substitute):** `tripId`, `matchingRound`, `reservationId` (fencing token), `operationKey`, `messageId`, `correlationId`, `traceId`.

**Safety (never):** 1 driver → 2 trips, 1 trip → 2 drivers, stale release frees newer reservation, duplicate payment effect, terminal trip reactivation.

**Liveness targets:** pending Outbox publishes, UNKNOWN reconciles, Saga progresses/compensates/`REQUIRES_REVIEW`. Payment and Trip both host confirmed Outbox relays; Trip resumes workflow from persisted Saga state after restart.

**Messaging:** at-least-once + `MessageEnvelope<T>` + publisher confirms + manual ACK after commit + bounded retry (3) + `<queue>.dead` + `ride.quarantine.q` + consumer dedup. Timeout = `UNKNOWN`, reconcile with same key, no blind retry, no hedging on side-effects. See `docs/messaging-runtime.md`, `docs/02-distributed-workflow-engineering.md`.

**Workflows:** Trip→Matching `RideRequested` (async), Matching→Driver `ReserveDriver` (sync + UNKNOWN handling), Trip→Payment (async Saga), events→Notification (async). Outbox for dual-write gap, persisted Saga/MatchingRound for crash-resume.

This combined completion branch includes Member C's shared transactional Outbox/dedup runtime, Payment command consumer and result Outbox, fault platform, Notification projection and HA tooling together with Member A's Trip Outbox, persisted Saga, compensation/forward-recovery workflow, deterministic crash proofs, auditor and Trip/Saga dashboard.

Closed on this branch: `DriverReservedV1` carries durable `matchingRound` provenance; Trip atomically commits lifecycle/Saga changes with stable Outbox intents; Payment outcomes are fenced by matching `sagaId` and `operationKey`; pre-pivot failure performs exact compensation while capture uncertainty after completion recovers forward without regressing the terminal Trip.

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

### Enable Trip Outbox/Saga messaging

Apply Trip EF migrations through `W10_RideSaga` against the Trip-owned
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
broker consumers. Authenticated POST /trips atomically stages `RideRequestedV1`
in the Trip Outbox; a 503 `MATCHING_INTENT_PENDING` means the ride was saved but
the durable matching intent has not committed, so retry the same key/body.
The production authenticated client flow depends on shared Identity/Gateway integration.

For a local visual walkthrough, run `scripts/demo/member-a-mock-workflow.ps1`
and open the provisioned Grafana dashboard at
`http://localhost:3000/d/ride-trip-saga/trip-saga-and-outbox`.

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

Implemented through Member A W12 and Member C W16: compose core/obs profiles, per-service DB roles, OTel, confirmed/manual-ACK messaging, Driver reservation foundation, Trip lifecycle/assignment fencing, transactional Trip Outbox, persisted Ride Saga, exact compensation, post-pivot forward recovery, deterministic crash/restart proofs and invariant auditing.

Member C W07–W16 implementation is present on this branch: bounded retry/DLQ, shared Outbox/dedup, Payment messaging, deterministic faultpoints, Notification projection, observability dashboards, endpoint-list reconnect, HA/release harnesses and final report. See `evidence/member-c/w12-w16-completion.md` for the strict split between implemented code and live proofs still requiring Docker.

Member A completion verification on this working tree: Trip unit **299/299**, W9/W10 real-infrastructure **31/31**, W11 faultpoint **11/11**, W11 real process crash/restart **5/5**, and W12 telemetry/authz/auditor **19/19**; Release solution build is clean with 0 warnings/errors.

Remaining system boundary: Identity is still a skeleton, so the production authenticated client walkthrough and independent peer reruns are not claimed. No full-system HA claim is made; the tested claim is Trip Outbox/Saga recovery under the recorded broker/process scenarios.
