# Distributed Ride-Hailing System

**Distributed platform with durable Payment simulation and a reusable RabbitMQ runtime. Team integration and acceptance remain required.**

Thesis: ride-hailing is the vehicle to study a deeper distributed-systems question — how independent services preserve safety invariants and eventually recover when requests race, reads are stale, messages duplicate, responses disappear, and processes crash. See `docs/00-project-scope-architecture.md`.

## Architecture overview

Five authoritative services (Identity, Driver/Vehicle/Location, Trip/Booking, Matching, Payment Simulator) + Notification Worker + API Gateway. PostgreSQL is per-service ACID authority (logical DBs on one local server); RabbitMQ is async broker; Redis GEO is Matching-owned derived read model only. Planned (not yet implemented): PostgreSQL-guarded Driver reservation (never a Redis lock) and Trip-owned persisted Saga. See Docs 00–02.

Payment implements durable operation-key idempotency, same-key UNKNOWN reconciliation and finite retry. The messaging library implements confirms, manual ACK and bounded retry/error transfer. Persisted Saga integration, Outbox/processed-message atomic dedup and general faultpoint orchestration remain later milestones. Links: `docs/01-domain-correctness-contract.md`, `docs/02-distributed-workflow-engineering.md`.

## Repository layout

- `src/Gateway|Services|Workers|BuildingBlocks` — 7 deployables + 3 libs
- `tests/Unit|Integration|System` — xUnit unit and real-infrastructure integration suites
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

Platform foundation: Compose core/optional obs profiles, isolated per-service DB roles, bounded owned-DB readiness, OTel traces/metrics and CI smoke (playbook `docs/05C-member-c-payment-messaging-platform.md` §§21–23; run `python scripts/test/platform-smoke.py` on a disposable project). Payment provides a Development-only deterministic simulator with PostgreSQL persistence (see `src/Services/Payment/RideHailing.PaymentService/README.md`, Doc 01 §10). Integration messages use the canonical `MessageEnvelope<T>` with W3C trace headers and code-declared RabbitMQ topology with persistent delivery (Doc 02 §§5–6; run `python scripts/test/topology-smoke.py` on a disposable broker).


## Payment and messaging integration

- [Payment simulator runtime](src/Services/Payment/RideHailing.PaymentService/README.md): replay/conflict semantics, development retry/reconcile routes and crash verification.
- [RabbitMQ runtime](docs/messaging-runtime.md): host lifecycle, publisher confirms, manual ACK, failure handling and trace propagation.

The simulator endpoints are Development-only. Mobile integration still needs the team-owned authenticated Trip/Saga flow; a successful simulator call does not establish that end-to-end workflow.
