# Trip / Booking Service — Owner A

## Current W7–W10 additions

- W7 cancellation stores exact cleanup obligations in the same transaction as
  terminal state. Late outcomes never reopen cancelled trips.
- W8 supervises three manual-ACK consumers. Trip effects and durable receipts
  share one PostgreSQL transaction; replay and domain guards are separate
  defenses.
- W9 replaces W8 direct publication with a transactional Outbox. Every
  transition that requires an outgoing message (StartMatching →
  `RideRequestedV1`; cancellation/late-terminal cleanup →
  `CancelMatchingRoundV1` / `ReleaseDriverReservationV1`) appends a pending
  `outbox_messages` row in the SAME EF `SaveChangesAsync` as the business
  state. The create path no longer touches RabbitMQ: a broker outage or
  process crash after commit leaves a durable pending intent, and the shared
  C-owned relay claims/publishes/marks it with a deterministic `messageId`.
  `CancelMatchingRoundV1`/`ReleaseDriverReservationV1` go to `ride.commands`;
  `RideRequestedV1` goes to `ride.events`.
- W10 adds the persisted Ride Saga (`ride_sagas`, migration `W10_RideSaga`;
  exactly one authoritative saga per Trip) and the Payment orchestration:
  the first DriverAccepted commits Trip DRIVER_ACCEPTED + Saga
  AUTHORIZE_PENDING + `AuthorizePaymentV1` (`{tripId}:authorize:v1`) in one
  transaction; `PaymentAuthorizedV1` under the exact operationKey/sagaId
  lineage advances to READY_TO_START/AUTHORIZED; terminal DECLINED
  compensates (Trip CANCELLED + Saga COMPENSATING + the exact
  `ReleaseDriverReservationV1`/`CancelMatchingRoundV1` intents) while
  uncertain outcomes (TRANSIENT/TIMEOUT/UNKNOWN) never compensate and keep
  the same key; a cancel-race authorization dispatches the exact
  `VoidPaymentAuthorizationV1` (`{tripId}:void:v1`); completion commits
  `CapturePaymentV1` (`{tripId}:capture:v1`) + Saga CAPTURE_PENDING +
  `MarkDriverTripStartedV1`/`MarkDriverTripCompletedV1` driver commands, and
  capture failure/uncertainty NEVER regresses a COMPLETED Trip (DSI-028 —
  the saga holds CAPTURE_PENDING/REQUIRES_REVIEW instead). Payment outcome
  consumption replaces the W8 `PAYMENT_LINEAGE_UNRESOLVED` skeleton; trips
  without a saga keep it. Trip, saga and the W10 payment/driver intents all
  share one EF unit of work per step; `TripDbContext` translates both Trip
  `Version` and saga `StepVersion` OCC conflicts to
  `TripConcurrencyConflictException`. Payment-side command consumption and
  Driver-side command execution are B/C-owned and pending (their absence is
  never faked with local stubs).
- Trip owns only intent semantics and the `outbox_messages` EF mapping, which
  mirrors C's shared schema (`001_outbox_messages.sql`) exactly. Claim,
  publish-confirm, mark, retry and backlog metrics stay C-owned
  (`outbox.pending`, `outbox.oldest_pending.age`, `outbox.failed_visible`).
  EF migrations create the table; the relay never calls `EnsureSchemaAsync`.
- Apply migrations before enabling `TripMessaging__Enabled=true` (compose:
  `TRIP_MESSAGING_ENABLED=true`). Compose local/CI sets
  `TripDatabase__AutoMigrate=true` (`TRIP_AUTO_MIGRATE`) so the service applies
  EF migrations at startup before `/health/ready` (zero pending migrations)
  is probed; production sets it false and migrates via an external job.
  Supply `RabbitMq__Host`, `RabbitMq__Username`,
  `RabbitMq__Password`; optional `RabbitMq__Port` and `RabbitMq__VirtualHost`.
  Readiness checks migrations and, when enabled, consumer connectivity. The
  relay gates on database readiness plus its own publish-path probe (broker
  reachable and topology declared, verified independently while consumers are
  disconnected), so an unavailable broker leaves rows pending instead of
  burning retry attempts into `FAILED_VISIBLE`, while a consumer-only fault
  cannot stall outgoing delivery.
  Cross-owner W10 integration (Payment consumer/producer legs, Driver command
  consumer) remains pending B/C work. The historical week-by-week notes
  below describe their own scope.

## W11 deterministic fault proofs

- `Integration/FaultPoints/` integrates the canonical named faultpoint
  `Saga.AfterPersistBeforeDispatch` (A-W11-01, Doc 02 §27, Doc 05A §19) at
  the durable boundary the Outbox already guarantees: the relay can only see
  rows a committed transaction produced, so the pre-publish seam
  (`OutboxRelayOptions.BeforePublish`) is necessarily after-commit. The gate
  holds the saga's payment dispatches (`AuthorizePaymentV1`,
  `CapturePaymentV1`, `VoidPaymentAuthorizationV1`), writes an observable
  `reached` marker, releases on a harness signal, and never blocks past its
  configured deadline. It never wraps business code, opens no transaction and
  mutates no domain state.
- Activation is double-gated and default-off: `TripFaultPoints:Enabled=true`
  (plus an explicit `TripFaultPoints:Activated` list and
  `TripFaultPoints:SignalDirectory`) AND a non-Production host environment —
  an explicit enable in Production fails startup. There is no HTTP surface;
  the faultpoint can never be activated by an untrusted request.
- The frozen integration contract with C's pending shared W11 fault platform
  (Doc 05C §30) is documented on `TripFaultPointGate`: name, location (relay
  pre-publish seam), scope, activation gates, file signaling protocol and
  bounded wait. C's provider can adopt these semantics without a Trip rewrite;
  Trip will not fork or duplicate a shared framework once it lands.
- `TripW11FaultProcessTests` prove the crash/restart properties across real
  OS process boundaries: REL-04 (SIGKILL at the faultpoint, restart reclaims
  and publishes the SAME messageId through the real broker), REL-07 (kill
  after durable dispatch; the Payment outcome backlogs on the quorum queue
  while the service is down; the SAME saga continues to terminal COMPLETED;
  stale/duplicate late outcomes cannot regress it), REL-17 (late
  DriverReserved/PaymentAuthorized on terminal trips: no regression, exact
  cleanup identities, logically duplicate delivery is a durable
  BusinessReplay), and W11-05 (crash with compensation obligations
  outstanding; the backlogged release confirmation resolves the exact
  reservation identity after restart; the authorized-after-cancel race
  resolves through the exact void key; COMPENSATED only when every leg
  resolved). All synchronization is bounded polling on authoritative
  database/queue/process state — no sleep ever stands in for a barrier.

- Owner: A (reviewer B, backup C)
- Authority: ride lifecycle, FareSnapshot, assignment reference, Saga state, cancellation. Does NOT decide Driver reservability.
- DB: `trip_db` (role `trip_app`)
- Deps: `ConnectionStrings__Postgres`, `RabbitMq__Host`
- Read: Docs 00 §3-4, 01 §6-7, 02 §14, 05A
- Bootstrap: skeleton only. NOT implemented: FSM, FareSnapshot, Outbox, Saga, compensation.
- A-W03-01/02/03/04/05/06: `Domain/Trips/` implements the pure Trip aggregate
  (`Trip`, `TripState`, `FareSnapshot`, `TripAssignment`, `TripLocation`,
  outcome/error model) with legal/illegal/terminal unit tests. No CreateRide
  handler, no idempotency, no messaging. NOT
  implemented: Outbox, Saga, compensation.
- A-W03-07: `Infrastructure/Persistence/Configurations/TripConfiguration.cs`
  maps the Trip aggregate into the `trips` table (ComplexProperty columns,
  canonical state text, application-managed `Version` concurrency token) via
  migration `W03_TripAggregate`. `TripDbContext` translates Trip concurrency
  failures to `TripConcurrencyConflictException`. The database enforces
  assignment atomicity (`CK_trips_assignment_coherence`), state/assignment
  coherence (`CK_trips_state_assignment_coherence`) and non-negative fare.
- A-W04 (deterministic CreateRide core + thin POST /trips adapter): `Application/Trips/CreateRide/`
  (`CreateRideCommand/Handler/Result/Error`, request fingerprint, deterministic
  P0 fare `p0-w4-v1`/VND via Haversine, FareSnapshot creation) with Rider-scoped
  idempotency (`trip:create:<rider-D>`, opaque exact key, replay-before-active
  rule) and `TripStatePolicy.ActiveForRiderBooking` (DSI-013). `Trip.Create`
  takes the single application-supplied creation timestamp (no domain clock).
  `Infrastructure/Persistence/` adds `RequestIdempotencyRecord` (composite PK
  `PK_request_idempotency`, FK `FK_request_idempotency_trips_resource_id`
  RESTRICT) plus partial unique index `UX_trips_one_active_per_rider`, via
  migration `W04_CreateRideIdempotency`. `Api/CreateRideEndpoint.cs`
  (`MapTripEndpoints`) is a thin POST /trips adapter: Rider identity consumed
  from the authenticated ClaimsPrincipal (`Api/RiderIdentity.cs`), opaque
  Idempotency-Key handling, transport mapping, with local concurrent correctness
  proofs. Trip does NOT implement JWT issuance/validation or Gateway
  authentication.
- A-W02-01/07/08: `Api/` freezes Trip HTTP transport shapes
  (`CreateRide`, `GetTrip`, `CancelRide`, `TripLocationDto`, `TripRoutes`,
  `TripIdentityHeaders`) + stable-identity representation. Since W4/W5 the
  behavior-backed routes POST /trips, GET /trips/{id} and POST
  /trips/{id}/cancel are registered in `Program.cs`; Driver-actor lifecycle
  routes remain future (not Trip-owned transport identity).
- A-W05 (local lifecycle + first read/cancel REST slice, no broker): `Trip`
  owns StartMatching/current-round fencing (round 1), replay/stale/conflict
  intake for reservation/accept/payment/matching-failure, validated Driver
  rejection with guarded-cleanup exposure (no state change), the pre-start
  cancellation matrix with `TripCompensation` requirement representation,
  authorization-gated Start and terminal Complete, and explicit-timestamp
  `UpdatedAt`. `Application/Trips/Lifecycle/` coordinates the intakes
  (rider-scoped StartMatching/Cancel plus system-shaped reservation, decision,
  payment, matching-failure and progress intakes). `Integration/Matching/`
  freezes the `RideRequestedV1` payload shape + example fixture for B/C
  review (no publish path). Migration `W05_TripLifecycle` adds
  `current_matching_round`/`updated_at` with round/assignment database
  defense. No RabbitMQ runtime, Outbox, Inbox, Saga or W6+ reservation
  orchestration in Trip.
- Boundary: Trip references no Driver/Matching/Payment service or DB; no Redis;
  no EF/Outbox/Saga in `Api/`; nothing Trip-domain added to BuildingBlocks.
- A-W02-02/04/05/06: `Infrastructure/Persistence/TripDbContext.cs` (DbContext
  shell, NO DbSet — W2 model has no entities; `Api/` DTOs are never mapped) +
  Npgsql registration in `Program.cs` reading ONLY `ConnectionStrings:Postgres`
  (fail-fast when missing; production never auto-migrates). Migration
  `W02_TripPersistenceBaseline` is intentionally EMPTY (Up/Down no-ops).
  Design-time commands need an ephemeral `ConnectionStrings__Postgres` value
  (model scaffolding opens no connection; never commit a real one):
  `dotnet tool restore`,
  `ConnectionStrings__Postgres=... dotnet ef migrations list/script/add`.
  No `IDesignTimeDbContextFactory`: normal host discovery works.
  Integration tests (`tests/Integration/RideHailing.TripService.IntegrationTests/`)
  boot the real app via `WebApplicationFactory<Program>` against disposable
  Testcontainers PostgreSQL 18.6 and override ONLY the connection string via
  `UseSetting`; they prove boot, /health/live, /health/ready, migration from
  empty DB, latest-migration history, and DbContext connectivity (component
  proof only — NOT least-privilege/cross-DB proof, which needs C bootstrap).

## W12 observability, security hardening and the Trip-side core gate

### What A instrumented (A-W12-01/02)

- `Integration/Observability/TripDiagnostics.cs` is the single Trip-owned
  ActivitySource (`RideHailing.Trip`) and meter (`RideHailing.Trip`), riding
  the SAME OpenTelemetry pipeline as C's shared messaging instruments (one
  registry; Program.cs just adds the source/meter). Domain spans:
  `RideHailing.Trip/CreateRide`, `RideHailing.Trip/CancelRide` (HTTP
  decisions) and `RideHailing.Trip/ApplyAuthority` (consumer-side authority
  facts). The span context at intent-creation time becomes the durable
  `traceparent` inside `outbox_messages.headers_json`, so every relayed
  payment/release/cancel command stays a child of the decision that caused
  it across process restarts and publication delay.
- Metrics (dot names; a Prometheus exporter renders the Doc 05A §20.2
  underscore forms): `trip.created`, `trip.cancelled`,
  `trip.transition.rejected{reason}`, `saga.compensation`,
  `saga.requires_review` are counters incremented ONLY after the owning
  transaction commits — idempotent replays, create-race reconciliation and
  redelivered receipts (durable receipt replay) never increment, and
  infrastructure failures (OCC conflicts, broker outage) are not business
  rejections. `saga.active{step}` is an ObservableGauge fed by
  `TripMetricsObserver`, a background service that re-runs one indexed
  GROUP BY over `ride_sagas` (default every 15 s; `TripMetrics:RefreshSeconds`),
  so after a restart the gauge re-derives from durable state instead of a
  lost in-memory count. Outbox backlog gauges (`outbox.pending`,
  `outbox.oldest_pending.age`, `outbox.failed_visible`) stay C-owned shared
  instruments refreshed by the shared relay from durable rows; Trip adds no
  duplicate measurement.
  The same successful query supplies `saga.oldest_pending.age` in seconds since
  the oldest non-terminal Saga was created. A successful empty query emits zero
  backlog and age; unavailable observations retain the last snapshot and never
  invent a zero. Counts and age have no business-identity labels.
- Cardinality contract: only bounded vocabularies are dimensions
  (`reason` from a fixed rejection-class set, `step` from the six canonical
  saga steps). tripId/sagaId/reservationId/operationKey/messageId are span
  and log fields, never labels. riderId, credentials and payloads never
  appear in tags; W12 telemetry tests assert this on emitted signals.

### How to verify A's mechanisms yourself (B/C handoff, A-W12-08)

Reproduce commands (from the repo root; `dotnet` on PATH or the documented
SDK location):

```bash
# Trip unit + full integration suites (real PostgreSQL + RabbitMQ via
# Testcontainers): telemetry semantics, W3C propagation, authz negatives,
# auditor execution and the W09–W11 fault proofs.
dotnet test tests/Unit/RideHailing.TripService.UnitTests
dotnet test tests/Integration/RideHailing.TripService.IntegrationTests

# The one-command focused views:
dotnet test tests/Integration/RideHailing.TripService.IntegrationTests \
  --filter "FullyQualifiedName~TripW12"
```

Review checklist for B and C:

1. Trip lifecycle: `Trip`/`TripStatePolicy` is the only state authority.
   Terminal states never revive (DSI-010); `current_matching_round` fences
   every reservation lineage (DSI-009/016, DB CHECK-enforced).
2. Saga lineage: one `ride_sagas` row per trip (UNIQUE); steps
   AUTHORIZE_PENDING → AUTHORIZED → CAPTURE_PENDING → CAPTURED, or
   compensation RELEASE_PENDING/VOID_PENDING → COMPENSATED; operation keys
   are frozen `{tripId:D}:authorize|capture|void:v1` (DSI-034, DB
   CHECK-enforced) and never re-minted on retry.
3. Outbox atomicity: every external obligation is staged by
   `TripOutboxWriter` on the SAME DbContext/transaction as the business
   transition (A-W09). There is no direct required publish after
   SaveChanges anywhere in Trip.
4. Relay/confirm/retry: the C-owned shared relay claims → publishes
   (confirmed) → marks. Trip gates cycles on its own DB readiness and an
   independent broker publish-path probe (`TripOutboxRelayWorker`).
5. Restart recovery: a killed process leaves PENDING rows and a durable
   saga; the relay republishes the same messageId and the saga resumes from
   `step_version` (W11 process-kill proofs: `TripW11FaultProcessTests`).
6. Compensation exactness: releases confirm against the exact
   (driver, reservation, round) cleanup intent — `StaleIdentity` facts park
   as NeedsReview and never mutate the Trip (`TripMessageHandler`).
7. Auditor: `TripInvariantAuditor` (test project) runs read-only AUD-01..08
   SQL; AUD-01/02/04 are NOT RUN without B's Driver database — that is an
   honest dependency, not a green cell. Drive a scenario and print a report
   from `TripW12InvariantAuditorTests`.
8. Authorization: endpoints consume an already-authenticated principal;
   401/403/404-ownership fail-closed behavior and no-outbox-effect on
   rejection are proven in `TripW12AuthzNegativeTests` plus the W4/W5 HTTP
   suites. End-to-end JWT issuance/validation is C/platform work
   (SYSTEM_AUTH_PENDING).
