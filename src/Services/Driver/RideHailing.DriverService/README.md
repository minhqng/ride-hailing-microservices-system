# Driver / Vehicle / Location Service — Owner B

- Owner: B (reviewer A, backup C)
- Authority: Driver status, current reservation, Trip binding, location ingress. Exclusive assignment authority.
- DB: `driver_db` (role `driver_app`)
- Deps: `ConnectionStrings__Postgres`, `RabbitMq__Host`
- Read: Docs 00 §3-4/§7-8, 01 §4-5, 02 §12-13, 05B

## Implemented (W2+W3)

- W2: `Infrastructure/Persistence/Migrations/001_driver_baseline.sql`
  (`drivers`, `vehicles`, idempotent), PG/ownership fixtures.
- W3: `Domain/Drivers/Driver` FSM (OFFLINE/AVAILABLE/RESERVED/ON_TRIP,
  DSI-002 coherence, same-reservation idempotency, stale-token no-mutate),
  `Domain/Vehicles/Vehicle`, `Application/Drivers` GoOnline/GoOffline handlers,
  `Application/Location` ingress validation + `LocationAcceptor` (DSI-006,
  seq-first/capturedAt tie-break), `Infrastructure/Persistence/DriverRepository`
  (Npgsql raw, no EF), `Api/DriverEndpoints` (FUN-02: create/get/online/offline/location).
- Decisions: ingress accepts lat [-90,90] (polar valid at authority;
  `LocationValidator.IsGeoIndexable` ±85.05112878 reserved for W4 GEO projection);
  Domain throws `DriverDomainException(Code)`, API maps Invalid*→400 else 409;
  persistence is Npgsql raw (EF Core deferred to W6 OCC).

## NOT implemented (later weeks)

- W4: Redis GEO projection/Function. W6: OCC/conditional Reserve + CON-01.
- W7: fencing persistence, UNKNOWN reconciliation. W8+: RabbitMQ/Outbox.
