# Driver / Vehicle / Location Service — Owner B

- Owner: B (reviewer A, backup C)
- Authority: Driver status, current reservation, Trip binding, location ingress. Exclusive assignment authority.
- DB: `driver_db` (role `driver_app`)
- Deps: `ConnectionStrings__Postgres`, `RabbitMq__Host`
- Read: Docs 00 §3-4/§7-8, 01 §4-5, 02 §12-13, 05B

## Implemented (W2–W8)

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
  persistence is Npgsql raw (no EF).
- W6: OCC reserve (`Driver.Reserve` with expected-version fence: same-R replay
  succeeds version-agnostic, stale version → `STALE_VERSION`, foreign/state
  conflict → `DRIVER_NOT_AVAILABLE`), `DriverRepository.TryReserveAsync`
  (row-locked tx: `SELECT .. FOR UPDATE` → decide on fresh row → write;
  linearization point is lock acquisition), `POST /drivers/{id}/reserve`
  (200 won/replayed, 409 conflict codes, 404 unknown driver, 400 validation,
  static 503 dependency), migration `003` (non-empty reserve/active identity
  CHECK), CON-01 harness + proof (N=2/10/50/100, final-DB oracle, first E01 data).
- W7: exact-token release (`POST /drivers/{id}/release`: exact token frees,
  coherent AVAILABLE is an idempotent cleanup no-op, live foreign reservation
  conflicts with `STALE_RESERVATION` and is never freed — DSI-004/033),
  version-fenced `POST /drivers/{id}/online|offline` (`{expectedVersion}`
  required; stale writers fence with `STALE_VERSION`, replays no-op
  version-agnostic — Race B closure, DSR-004), read-only reservation truth
  `GET /internal/drivers/{id}/reservations/{rid}` (200 with the canonical
  outcome value incl. `NOT_FOUND`; only transport is 503, caller stays
  UNKNOWN), CON-02 barrier proof (exactly one of OFFLINE/RESERVED wins),
  CON-04 sequence proof (late stale release cannot free the newer owner).

- W8: authoritative event publication `Application/Messaging/DriverEventPublisher`
  (confirms + mandatory routing, serialized gate, deterministic per-effect
  `messageId`), `DriverEventContracts/DriverEventMapper` (`DriverReservedV1`,
  `DriverAvailableV1`, `DriverOfflineV1`, `DriverReservationReleasedV1`,
  `DriverLocationAcceptedV1` on the frozen `RideTopology` routes). Publication
  is post-commit best-effort; Outbox atomicity remains W9.

- W8 round-lineage hardening (this branch): `DriverReservedV1` carries
  `matchingRound` (Doc 02 §21.3) from durable provenance — `reserved_round`
  and `ReservationRound` on the aggregate, recorded at the reserve win,
  replayed on restart, and cleared together with the reservation refs on
  release / trip start. The round arrives with the Matching reserve request
  and is unknown-omitted on the wire (`[JsonIgnore(WhenWritingNull)]`), so
  Trip's presence gate still parks genuinely legacy payloads as
  `NeedsReview:RESERVATION_ROUND_REQUIRED` while verified events flow.

## NOT implemented (later weeks)

- W9: Outbox-atomic event publication (currently post-commit best-effort),
  inbound `driver.commands.q` consumer (release/mark-trip-started/completed —
  no production path to `ON_TRIP` yet).`matchingRound` on `DriverReservedV1`
  has landed on this branch (see W8 hardening above); Trip's park path stays
  conservatism for payloads that omit the field.
