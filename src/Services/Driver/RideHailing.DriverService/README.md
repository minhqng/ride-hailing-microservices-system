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
  was post-commit best-effort in W8; W9 authoritative state events now use the
  shared transactional Outbox (see below). Location samples remain best-effort.

- W8 round-lineage hardening (this branch): `DriverReservedV1` carries
  `matchingRound` (Doc 02 §21.3) from durable provenance — `reserved_round`
  and `ReservationRound` on the aggregate, recorded at the reserve win,
  replayed on restart, and cleared together with the reservation refs on
  release / trip start. The round arrives with the Matching reserve request
  and is unknown-omitted on the wire (`[JsonIgnore(WhenWritingNull)]`), so
  Trip's presence gate still parks genuinely legacy payloads as
  `NeedsReview:RESERVATION_ROUND_REQUIRED` while verified events flow.

## W9 transactional publication

Reserve, exact release, online and offline mutations persist their authoritative
state and canonical event intent on the same connection/transaction. Failed
intent insertion rolls back the state change. Exact effect replay leaves both
aggregate version and existing intent identity unchanged. The shared relay uses
publisher confirms and republishes the stored envelope messageId after uncertain
publication; delivery remains at least once.

`RabbitMq:PublishingEnabled` controls relay hosting, not intent persistence.
Schema startup applies the Driver baseline and shared Outbox schema. Authoritative
writes fail closed if the required schema is absent. The shared relay declares
the canonical topology through the B-owned hosting adapter.

Historical pre-W9 reservations have no guaranteed Outbox intent. Same-R replay
does not invent a missing historical intent; rollout/backfill or authority-based
projection rebuild needs lead coordination. New W9 effects are covered by
[W9 evidence](../../../../evidence/member-b/w09-outbox/README.md).

## W10 lifecycle and compensation commands

The Driver host consumes `driver.commands.q` through C's `ManualAckConsumer`.
`RabbitMq:CommandsEnabled` defaults to `true`; set the container environment
`RabbitMq__CommandsEnabled=false` to suspend ingress during rollout/rollback.
The worker waits for Driver migrations and shared Outbox schema before consuming.
`RabbitMq:PublishingEnabled` independently controls the relay.

All commands use the canonical V1 envelope and an object payload containing
nonempty UUIDs `driverId`, `tripId`, `reservationId`:

| Command | Exchange / routing key | Guard |
|---|---|---|
| MarkDriverTripStartedV1 | ride.commands / driver.mark-trip-started.v1 | exact current RESERVED assignment |
| MarkDriverTripCompletedV1 | ride.commands / driver.mark-trip-completed.v1 | current ON_TRIP plus recorded original reservation |
| ReleaseDriverReservationV1 | ride.commands / driver.release-reservation.v1 | exact current RESERVED driver/trip/reservation and matchingRound |

New broker Release execution additionally requires a positive integer `matchingRound`, matching
the generation recorded at the reserve win. A's `TripOutboxIntents.ReleaseReservation`
emits this four-field contract. Wrong round is durably `Rejected` with
`RESERVATION_ROUND_MISMATCH`; a legacy reservation without round is rejected with
`RESERVATION_ROUND_REQUIRED`. No historical round is inferred or backfilled.
An AVAILABLE/OFFLINE/ON_TRIP Driver yields `Rejected:RELEASE_PROVENANCE_REQUIRED`,
never an unproven compensation success. Missing Driver is `NotFound:DRIVER_NOT_FOUND`.
Exact command-message retries return their original durable receipt, even after
new reservations. This includes identical committed Phase1 three-field receipts:
receipt lookup precedes new-execution validation, with no new effect or guessed
round. Unseen three-field commands are rejected. HTTP W7 Release retains its
existing exact-token/no-op contract.

Migration `005_driver_lifecycle.sql` adds `driver_lifecycle_effects` and
`driver_command_receipts`. Lifecycle effects are append-only through the
application; STARTED/COMPLETED rows retain the original reservation, version,
event ID and timestamp across restarts and later trips. Live RESERVED fields
still clear at Start. Historical trip/reservation identities cannot be reused
for a fresh reservation on that Driver. Legacy ON_TRIP without ledger provenance
fails with `LIFECYCLE_PROVENANCE_REQUIRED`; operators must reconcile from
authoritative evidence, never fabricate a reservation ID.

Command transaction order is message advisory lock, receipt semantic check,
Driver row lock, exact guard, state/version, outgoing event intent, lifecycle
history, final receipt, commit. Release shares its existing guarded operation
and Outbox writer inside that transaction. ACK follows commit. Reused message
IDs with changed semantic content are poison; infrastructure failures follow
the platform's bounded retry and visible dead-letter policy. Rejected valid
commands are retained in receipts (`disposition`, `reason`) for owner review.

Lifecycle event payloads include the original driver/trip/reservation, version,
and ON_TRIP/AVAILABLE status. Command correlation, causation, Saga/operation and
active trace headers flow into the stored Outbox envelope. Historical replay
returns its original version/event ID without touching the current Driver.
No immediate publish follows the transaction.

Command ACK means the disposition committed. `Applied` means Driver changed and
its authoritative release event is durable. Trip compensation is confirmed only
when Trip consumes that exact Driver event and resolves its owned cleanup; ACK or
Driver availability alone cannot prove this. A new message on an already released
reservation requires reconciliation with the original event/receipt rather than
guessing historical success. A/B operators inspect `driver_command_receipts`
(`message_id`, `disposition`, `reason`, `event_message_id`) and pending/failed Outbox
rows; A must keep rejected/missing/no-provenance cleanup unresolved for review.
No automatic cross-service receipt reconciliation worker is supplied.

B-owned compensation proof uses A's production Release builder, real PostgreSQL,
RabbitMQ, exact Trip intake and actual broker/database restarts. Full REL-09 still
requires C's Payment command/outcome path and combined A persisted-Saga validation;
human A/C agreement review remains external. See
[W10 evidence](../../../../evidence/member-b/w10-lifecycle/README.md).

## W12 reconciliation-safety repair

Two authority answers were hardened for Matching's recovery path:

1. `GET /internal/drivers/{id}/reservations/{rid}` is identity-aware. A durable
   `driver_lifecycle_effects` row for the exact `(driver_id, reservation_id)`
   with phase `STARTED` or `COMPLETED` returns
   `OWNED_BY_THIS_RESERVATION`, regardless of the Driver's current ON_TRIP or
   AVAILABLE status. The live row clears `reservation_id` when the ride starts,
   so status alone cannot prove that the reservation never committed. A
   different reservation/trip is never inferred from status alone; the current
   status branch still answers `ON_TRIP`/`OFFLINE`/`RESERVED_BY_OTHER`.
2. `GET /drivers/{id}` and `POST /drivers/{id}/reserve` return a structured
   `404 {"code":"DRIVER_NOT_FOUND"}` for authoritative absence. A bare 404
   (routing/proxy/transport, empty or foreign body) carries no domain
   provenance; Matching must keep the attempt ambiguous instead of concluding
   that no remote effect exists.

Both additions preserve status codes, OCC/version behaviour, same-reservation
idempotency, exact lifecycle identity and release fencing. Proofs:
`DriverRaceFencingTests` (historical ownership) and
`DriverNotFoundProvenanceTests` (real Kestrel bodies). See the
[W12 reconciliation-safety evidence](../../../../evidence/member-b/w12-reconciliation-safety/README.md).
