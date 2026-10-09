# Trip / Booking Service — Owner A

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
