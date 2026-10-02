# Distributed Ride-Hailing System

**Status: repository bootstrap / foundation**

Thesis: ride-hailing is the vehicle to study a deeper distributed-systems question — how independent services preserve safety invariants and eventually recover when requests race, reads are stale, messages duplicate, responses disappear, and processes crash. See `docs/00-project-scope-architecture.md`.

## Architecture overview

Five authoritative services (Identity, Driver/Vehicle/Location, Trip/Booking, Matching, Payment Simulator) + Notification Worker + API Gateway. PostgreSQL is per-service ACID authority (logical DBs on one local server); RabbitMQ is async broker; Redis GEO is Matching-owned derived read model only. Planned (not yet implemented): PostgreSQL-guarded Driver reservation (never a Redis lock) and Trip-owned persisted Saga. See Docs 00–02.

Planned (not yet implemented): Saga, Outbox, OCC, UNKNOWN reconciliation, fencing, idempotency, retry/DLQ, faultpoints. Links: `docs/01-domain-correctness-contract.md`, `docs/02-distributed-workflow-engineering.md`.

## Repository layout

- `src/Gateway|Services|Workers|BuildingBlocks` — 7 deployables + 3 libs
- `tests/Unit|Integration|System` — xUnit skeletons
- `deploy/postgres|rabbitmq|redis|otel|prometheus|grafana|jaeger|compose`
- `scripts/dev|test|fault|evidence`, `evidence/`, `.github/`, `compose.yaml`

## Services and ownership

| Service | Owner | DB | Port |
|---|---|---|---|
| Gateway | C | none | 8080 |
| Identity | Shared/A | identity_db | 8101 |
| Driver | B | driver_db | 8102 |
| Trip | A | trip_db | 8103 |
| Matching | B | matching_db (+Redis) | 8104 |
| Payment | C | payment_db | 8105 |
| Notification | C | notification_db | — |

Infra: PostgreSQL 5432, Redis 6379, RabbitMQ AMQP 5672 / UI 15672. Pinned: `postgres:18.6`, `rabbitmq:4.3.6-management`, `redis:8.10.2` (verified via manifest).

## Prerequisites

Docker + Compose. .NET 10 SDK only via `mcr.microsoft.com/dotnet/sdk:10.0` container (no host install required). Copy env: `cp .env.example .env`.

## Quick start

```bash
cp .env.example .env
docker compose up -d --build
curl http://localhost:8080/health/live
curl http://localhost:8101/health/live
curl http://localhost:8102/health/live
curl http://localhost:8103/health/live
curl http://localhost:8104/health/live
curl http://localhost:8105/health/live
# RabbitMQ UI: http://localhost:15672
docker compose ps
docker compose down
```

## Common commands

- Build/test (SDK container): `bash scripts/test/quick.sh`
- Compose validate: `docker compose config`
- See `CONTRIBUTING.md` for branches/commits/PRs.

## Docs reading order

`docs/README.md`: 00 → 01 → 02 → 03 → 04 → 05A/B/C.

## Team contribution workflow

`main`, `feature/<issue>-<name>`, `fix/<issue>-<name>`, `experiment/<id>-<name>`. Small PRs, conventional prefixes, one reviewer minimum; critical paths per Doc 04 §3.3.

## Current implementation status

W2 foundation: Compose core/optional obs, isolated DB roles, bounded owned-DB readiness, OTel traces/metrics and CI smoke. See `docs/member-c-week2.md` for issue coverage and pending peer review. W3 adds the Development-only Payment simulator, migration and state/persistence verification; see `docs/member-c-week3.md`. Broker bindings belong to W4.
