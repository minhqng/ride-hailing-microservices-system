# Trip / Booking Service — Owner A

- Owner: A (reviewer B, backup C)
- Authority: ride lifecycle, FareSnapshot, assignment reference, Saga state, cancellation. Does NOT decide Driver reservability.
- DB: `trip_db` (role `trip_app`)
- Deps: `ConnectionStrings__Postgres`, `RabbitMq__Host`
- Read: Docs 00 §3-4, 01 §6-7, 02 §14, 05A
- Bootstrap: skeleton only. NOT implemented: FSM, FareSnapshot, Outbox, Saga, compensation.
- P2 (A-W02-01/07/08): `Api/` freezes Trip HTTP transport shapes
  (`CreateRide`, `GetTrip`, `CancelRide`, `TripLocationDto`, `TripRoutes`,
  `TripIdentityHeaders`) + stable-identity representation. No business routes
  registered in `Program.cs` (no fake behavior); `Domain/`, `Application/`,
  `Infrastructure/`, `Observability/` remain W3+ (`.gitkeep`).
- Boundary: Trip references no Driver/Matching/Payment service or DB; no Redis;
  no EF/Outbox/Saga in `Api/`; nothing Trip-domain added to BuildingBlocks.
- P3 (A-W02-02/04/05/06): `Infrastructure/Persistence/TripDbContext.cs` (DbContext
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
