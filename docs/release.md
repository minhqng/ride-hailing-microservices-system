# Release v1.0.1

Status: **final release.** The annotated tag `v1.0.1` points at the commit
that contains this document. The exact commit SHA, tree object and evidence
anchors are recorded in
[evidence/release/v1.0.1/manifest.json](../evidence/release/v1.0.1/manifest.json)
and on the GitHub Release page. `v1.0.0` remains an immutable historical tag.

This document is the deployment and release record. It does not replace the
canonical contracts in Docs 00–03.

## Release identity

| Item | Value |
|---|---|
| Version | `v1.0.1` |
| Tag type | Annotated |
| Release date | 2026-11-18 |
| Base | `v1.0.0` plus the repository cleanup and the matching closure fix |
| Report | [report.pdf](report.pdf) (academic submission; unchanged from `v1.0.0`) |
| Slides | [slides.pdf](slides.pdf) |
| Verification record | [verification.md](verification.md) |

## What ships

- Five business services: Identity, Driver, Trip, Matching, Payment.
- Notification worker and YARP API Gateway.
- Service-owned PostgreSQL databases and roles, RabbitMQ quorum queues and a
  derived Redis GEO index.
- Transactional Outbox, deduplication, persisted Trip Saga, bounded retry,
  dead-lettering, UNKNOWN reconciliation and invariant auditing.
- DS03 functionality: registration/login/roles, driver status and location,
  ride creation, matching, accept/reject, single assignment, lifecycle,
  fare/payment simulation and recipient-addressed notifications.
- Verification suites, evidence bundles, deployment configuration, the final
  report and presentation slides.

## Changes in v1.0.1

- **Matching closure fix.** A matching round that committed one reservation
  now closes every remaining un-dispatched `READY` attempt as `CANCELLED`
  (`NOT_SELECTED`) in the same transaction. A cancellation that arrives after
  the winner committed also closes them. Before this fix, a round with more
  than one available candidate kept an unresolved attempt forever: the
  invariant audit reported `RESERVED_ROUND_HAS_UNRESOLVED_ATTEMPT`, and Trip's
  `CancelMatching` cleanup obligation could never be confirmed, so
  `cleanupPending` stayed true.
- **Pre-authorization cleanup fix.** Cancelling a `DRIVER_RESERVED` ride no
  longer mints a `ReconcilePayment` obligation. Authorization is requested only
  by accept, so no Payment effect can exist at that state; the obligation had
  no path to confirmation and kept `cleanupPending` true forever. A cancel at
  `DRIVER_ACCEPTED` or `READY_TO_START` still reconciles, because an
  authorization outcome may be in flight there.
- **Demo runbook corrections.** D.1 now states and verifies its real
  precondition (exactly one available Driver) and cancels the actual winner
  rather than assuming `$T1`. D.5 and D.6 create their own trips instead of
  depending on the completed Part C trip. D.1 and D.12 wait for durable cleanup
  confirmation instead of asserting after a fixed sleep.
- **New demo scenarios.** D.11 cancels during authorization and proves `VOID`
  instead of `CAPTURE`; D.12 forces a terminal authorization decline and proves
  `CANCELLED` plus an exact `ReleaseDriverReservationV1`.
- **Repository cleanup.** Weekly/team execution artifacts, member diaries and
  duplicate design notes were removed; the canonical Docs 00–03, operating
  guides and final submission record remain.

## Verified results

The release verification record is [verification.md](verification.md). In
summary:

- Full .NET regression: **1,344 passed, 0 failed, 1 documented opt-in skip**
  across 13 executable suites; TRX gate PASS.
- Production overlay smoke: **19/19 checks PASS**, including external
  migrations, a completed ride, cancellation/void, driver rejection,
  notification-outage isolation and reject-commit crash replay.
- Live demo run on a clean development stack: Part C 13 steps plus D.1–D.8,
  D.11, D.12 and a multi-candidate closure probe; every asserted state read
  back from the owning database, Redis or Prometheus.
- Static gates: secret scan PASS, hygiene self-test 24/24, source-anchor
  fixtures 12/12.
- Performance and HA measurements are not part of the published deliverable;
  the release demonstrates functional correctness, not production capacity.
- The repository CI workflow defines the hygiene, build-test, platform-smoke
  and release-smoke gates for any exact-tree checkout; the local gate results
  recorded in the evidence bundle are the verification for this tagged tree.

## Prerequisites

- Docker Engine or Docker Desktop with Compose v2.
- Python 3.10+ and OpenSSL for disposable credential initialization.
- For the Production overlay migration step: .NET 10 SDK on the host, or the
  documented SDK container command. The development stack does not require a
  host SDK.

## Clean-clone deployment (development)

```bash
git clone https://github.com/minhqng/ride-hailing-microservices-system.git
cd ride-hailing-microservices-system
git checkout v1.0.1
cp .env.example .env
python3 scripts/security/init-local-auth.py
docker compose up -d --build
```

Verify readiness:

```bash
for port in 8080 8101 8102 8103 8104 8105 8106; do
  curl -fsS "http://localhost:${port}/health/ready" && echo
done
```

The development stack uses `TRIP_AUTO_MIGRATE=true` so a fresh volume can
start without a separate migration job. `.env` and generated keys are
gitignored; never commit them.

## Production release overlay

The overlay must be started and exercised, not only rendered. The automated
form is `python3 scripts/test/release-smoke.py`; the manual form is:

1. Use a clean clone or clean worktree at the release commit. Choose an
   isolated Compose project so no development volume or container is touched:

   ```bash
   export COMPOSE_PROJECT_NAME=ride-release-<unique>
   ```

2. Supply every mandatory secret explicitly. The overlay fails closed when any
   of the eight database/broker passwords or the four authentication values is
   absent: `POSTGRES_PASSWORD`, `IDENTITY_DB_PASSWORD`, `DRIVER_DB_PASSWORD`,
   `TRIP_DB_PASSWORD`, `MATCHING_DB_PASSWORD`, `PAYMENT_DB_PASSWORD`,
   `NOTIFICATION_DB_PASSWORD`, `RABBITMQ_PASSWORD`,
   `AUTH_PUBLIC_KEY_BASE64`, `AUTH_PRIVATE_KEY_BASE64`,
   `AUTH_MATCHING_SECRET`, `AUTH_TRIP_SECRET`.

   `python3 scripts/security/init-local-auth.py` creates a disposable local
   key pair and client secrets in ignored `.env`; release operators replace
   every value with environment-specific secrets. Never commit or print them.

   Before starting the overlay, run the release preflight
   (`pwsh scripts/evidence/release-check.ps1` or the equivalent
   `scripts/test/release-smoke.py` secret checks). It rejects the known
   development placeholders (`*_dev_pw` and the `.env.example` authentication
   values) as well as missing values, so a copied development `.env` cannot be
   mistaken for a release configuration.

3. Start PostgreSQL, Redis and RabbitMQ, then apply Trip migrations externally
   from the empty database (the overlay disables Trip auto-migration):

   ```bash
   dotnet tool restore
   ConnectionStrings__Postgres="Host=127.0.0.1;Port=5432;Database=trip_db;Username=trip_app;Password=$TRIP_DB_PASSWORD" \
   Auth__PublicKeyBase64="$AUTH_PUBLIC_KEY_BASE64" \
   dotnet ef database update \
     --project src/Services/Trip/RideHailing.TripService \
     --startup-project src/Services/Trip/RideHailing.TripService \
     -c TripDbContext --configuration Release
   ```

4. Start the overlay:

   ```bash
   docker compose -f compose.yaml -f deploy/compose/compose.release.yaml up -d --build
   ```

   The overlay forces `TripMessaging__Enabled=true`,
   `TripDatabase__AutoMigrate=false` and `ASPNETCORE_ENVIRONMENT=Production`,
   and enables the Payment reconciliation worker. Payment's simulator routes
   exist only in Development, so a Production deployment has no remote decline
   control; the decline path is verified by the Payment integration suite
   against the real PostgreSQL provider simulator.

5. Wait for `/health/ready` on the gateway (`:8080`), identity (`:8101`),
   driver (`:8102`), trip (`:8103`), matching (`:8104`), payment (`:8105`) and
   notification (`:8106`), then run real authenticated journeys.

6. Tear down only the isolated project:

   ```bash
   docker compose -f compose.yaml -f deploy/compose/compose.release.yaml down -v
   ```

## Upgrade and rollback

`v1.0.1` is a patch release on top of `v1.0.0`. To roll back a deployment,
check out `v1.0.0` and redeploy with a separate Compose project; do not move or
delete either tag. Database migrations are forward-only for this course
release; restore the disposable volume or apply the documented migration
procedure when rebuilding an environment.

## Security

- No credentials are committed. `.env`, `secrets/` and generated keys are
  ignored.
- The release overlay fails closed without every mandatory secret.
- JWT validation enforces signature, issuer, audience, lifetime and algorithm.
- Driver routes bind the authenticated actor to the Driver-owned profile.
- Notification reads are owner-isolated from the JWT subject.
- Request bodies and message sizes are bounded.
- The secret scanner runs in CI and reports no high-confidence secret.

## Known limitations

- Payment is a simulated provider with durable idempotency and reconciliation;
  it is not connected to a real card network or bank.
- Notifications are an in-app inbox; there is no SMS, email or mobile push.
- HA evidence covers a single-host RabbitMQ quorum scenario, not whole-system
  or multi-region high availability.
- Performance results describe the recorded single-host workloads; they are
  not production capacity claims.
- Human team rehearsal, oral defense and peer sign-off are course-process
  activities performed by the team. The repository provides the executable
  demo and verified technical evidence.

## Release checklist

- [x] Source freeze, service documentation and the final technical report
- [x] Fail-closed release configuration and secret scan
- [x] Exact-tree CI and local release gates
- [x] Automated technical rehearsals and authority journeys
- [x] Evidence manifests and source anchors
- [x] Full regression and Production overlay smoke on the release source
- [x] Clean-clone deployment check
- [x] `v1.0.1` annotated tag and GitHub Release
- [ ] Human team rehearsal, oral defense and peer sign-off (team activity)
