# Matching Service — Owner B

- Owner: B (reviewer A, backup C)
- Authority: MatchingRound/Attempt, ranking, Redis projection use. Never writes Driver DB. Must never access `driver_db`.
- DB: `matching_db` (role `matching_app`)
- Deps: `ConnectionStrings__Postgres`, `Redis__ConnectionString`, `RabbitMq__Host`, `Services__DriverBaseUrl`
- Read: Docs 00 §8, 01 §8-9, 02 §12-13, 05B
- Bootstrap: skeleton only. NOT implemented: GEOSEARCH ranking, broker wiring, terminal NO_DRIVER marking.
- W7: durable reserve workflow on the frozen W2 attempt/round tables
  (`Infrastructure/Persistence/MatchingAttemptRepository`: write-ahead
  `IN_FLIGHT` persist, guarded transitions, PK-convergent duplicate workers,
  DSI-017 unresolved guard): `Application/Reserve/MatchingReserveOrchestrator`
  (serial executor: persist → read Driver version → reserve same-R with
  bounded STALE retry → Reserved/Rejected/Unknown; reconcile loop with same R
  and bounded exhaustion to visible `NeedsReview`; advance only on conclusive
  outcomes; round stays PROCESSING unless RESERVED — NO_DRIVER needs W9
  Outbox). `HttpDriverClient` targets the landed W6 reserve route with the
  observed version; reconcile query maps the 7 canonical outcomes.
  REL-01 proven via transport stub over real driver_db + matching_db.

## NOT implemented (later weeks)

- W5 bulk pipeline (GEOSEARCH ranking/eligibility over Redis) remains absent
  (reverted from main; W7 consumes explicit candidate lists, not the pipeline).
- W8+: RabbitMQ wiring (W4 updater is invoked directly; broker consumer comes
  later). W9: terminal publication/Outbox.

## Implemented (W2+W4)

- W2: `Infrastructure/Persistence/Migrations/001_matching_baseline.sql`
  (`matching_rounds`, `matching_attempts`), PG/Redis fixtures.
- W4: Redis GEO derived projection (05B §5, 01 §21.2):
  - `Application/Projection/RedisProjectionKeys` —
    `driver:projection:{id}` hash + `drivers:geo` index.
  - Split intents (review correction): `DriverStateProjectionUpdate`
    (status+version only) and `LocationProjectionUpdate` (coords+session
    only) — a state event can never smuggle a stale location.
  - `Infrastructure/RedisProjection/update_projection.lua` — one atomic
    function: version guard (DSI-007), per-session seq/capturedAt guard
    (DSI-006), GEO recompute from current values. Frozen policy: a foreign
    session is NEVER auto-accepted (`stale-session`); Docs 00–04 define no
    session-transition authority, and Redis cannot order opaque sessionIds.
    Known limitation: legitimate session rotation needs a future
    Driver-owned current-session signal.
  - `RedisProjectionUpdater` (StackExchange.Redis, `FCALL`; connection/
    server failure → `Degraded`, never throws; Driver path never blocks).
  - Program loads the function best-effort at startup.
  - Tests: DSI-007/DSI-006 projection-level, cross-session reject,
    state-only-no-clobber, FUN-03 GEOSEARCH discoverable, polar-lat
    applied-but-not-indexed, RESERVED eviction, REL-14 Degraded faultpoint,
    NOSCRIPT reload recovery, corrupt-hash heal, geo missing-coords ZREM.
  - Fixture: explicit REDIS_TEST_CONNECTION fails loud; otherwise disposable
    Testcontainers Redis (8.10.2, one shared container per run, AutoRemove).
    Convention (same as PG fixtures): tests FAIL LOUD when Redis is
    unavailable — no vacuous pass, so a run without real Redis is red, not green.

## Driver HTTP seam (B-W02-02) + freeze note (B-W02-08, PROPOSED)

- `Infrastructure/DriverClient/`: `IDriverClient`, `HttpDriverClient` (typed
  client, no retry handlers by design), request/response/outcome contracts,
  `README.md` freeze note with open questions for A/C.
- Mapping proven by stub-transport unit tests (no server needed): request
  shape, 200/409 mapping, 5xx/timeout/bare-404 propagate (caller keeps
  UNKNOWN per DSI-031; NOT_FOUND arrives only as 200-body outcome),
  all 7 reconcile outcomes, unknown-outcome throws.
- Driver-side `/internal/*` endpoints land in W6/W7; timeout budgets frozen there.

## Deterministic candidate pipeline (B-W05)

- `POST /internal/matching/rounds` (internal test/demo seam; W8 invokes the
  same application entry from `RideRequestedV1`): body carries trip/round
  identity + pickup only (all policy is config-owned). Idempotent replay.
  Errors: 400 `INVALID_REQUEST` (bad input), 409 `DOMAIN_CONFLICT`
  (terminal/cancelled/blocked round, exhausted ledger retry), 503
  `DEPENDENCY_UNAVAILABLE` (backing store down, static message).
- Pipeline: load-or-create round (PROCESSING, S0) → GEOSEARCH nearest-first
  bounded → eligibility (AVAILABLE, fresh location, capability hook,
  not-rejected; DSI-020 by type) → deterministic rank (distance ASC,
  optional freshness weight, driverId tie-break; DSI-019) → persisted
  snapshot + READY attempt ledger with stable reservationIds (DSI-018).
  Concurrent duplicate triggers converge (fresh-ordinal retry, bounded);
  unresolved attempts block new candidates (DSI-017 at the ledger).
- Authority: Redis is a hint, Driver reserve (W6) decides. No terminal round
  states here (`NO_DRIVER` needs W9 Outbox atomicity); empty candidates stay
  PROCESSING. Redis failure degrades (empty + flag), never fabricates.
- Policy: `Matching` config section (radius, bounds, freshness window,
  capability, ranking version). Capability feed pending (projection carries
  no vehicle data; non-null requirement safely excludes).

## NOT implemented (later weeks)

- W6: Reserve OCC + UNKNOWN reconciliation start. W7: UNKNOWN/reconcile completion.
  W8+: RabbitMQ wiring (pipeline is invoked directly; broker consumer comes later).
