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
