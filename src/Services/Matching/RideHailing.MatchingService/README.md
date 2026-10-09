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
- W9: terminal publication and atomic Outbox.

## W8 broker runtime

RideRequested intake and Driver projection consumers use the shared C runtime:
manual ACK after commit, confirmed retry/error transfer, bounded retries, and
supervised reconnection on channel closure or broker cancellation. The Trip
payload contract uses the envelope messageId as the round input identity. See
`Infrastructure/DriverClient/README.md` for the integration boundary.

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
  shape, 200/409/404 mapping, 5xx/timeout propagate (caller classifies
  UNKNOWN in W6/W7), all 7 reconcile outcomes over 200-body plus HTTP 404
  mapping to NotFound, unknown-outcome throws.
- Driver-side `/internal/*` endpoints land in W6/W7; timeout budgets frozen there.

## NOT implemented (later weeks)

- W5 bulk pipeline (GEOSEARCH ranking/eligibility over Redis) remains absent
  (reverted from main; W7 consumes explicit candidate lists, not the pipeline).
- W9: terminal publication and atomic Outbox.

## W8 broker runtime

RideRequested intake and Driver projection consumers use the shared C runtime:
manual ACK after commit, confirmed retry/error transfer, bounded retries, and
supervised reconnection on channel closure or broker cancellation. The Trip
payload contract uses the envelope messageId as the round input identity. See
`Infrastructure/DriverClient/README.md` for the integration boundary.
