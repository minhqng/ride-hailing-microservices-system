# Matching Service — Owner B

- Owner: B (reviewer A, backup C)
- Authority: MatchingRound/Attempt, ranking, Redis projection use. Never writes Driver DB. Must never access `driver_db`.
- DB: `matching_db` (role `matching_app`)
- Deps: `ConnectionStrings__Postgres`, `Redis__ConnectionString`, `RabbitMq__Host`, `Services__DriverBaseUrl`
- Read: Docs 00 §8, 01 §8-9, 02 §12-13, 05B
- Bootstrap: skeleton only. NOT implemented: rounds, ranking, UNKNOWN reconciliation, Redis GEO logic.

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
    applied-but-not-indexed, RESERVED eviction, REL-14 Degraded faultpoint.
  - Convention: tests return early (vacuous pass) when Redis is unreachable
    but always log INFRA/SKIP via test output, so a vacuous run is visible.

## NOT implemented (later weeks)

- W5: GEOSEARCH ranking/eligibility, MatchingRound/Attempt ledger.
- W6: Reserve OCC. W7: UNKNOWN reconciliation. W8+: RabbitMQ wiring
  (W4 updater is invoked directly; broker consumer comes later).
