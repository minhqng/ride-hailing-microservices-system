# Matching Service — Owner B

- Owner: B (reviewer A, backup C)
- Authority: MatchingRound/Attempt, ranking, Redis projection use. Never writes Driver DB. Must never access `driver_db`.
- DB: `matching_db` (role `matching_app`)
- Deps: `ConnectionStrings__Postgres`, `Redis__ConnectionString`, `RabbitMq__Host`, `Services__DriverBaseUrl`
- Read: Docs 00 §8, 01 §8-9, 02 §12-13, 05B
- W9: terminal `NO_DRIVER` and `MatchingFailedV1` intent commit together through
  `MatchingTerminalizer` and the shared Outbox store. The relay is enabled by
  `RabbitMq:PublishingEnabled`; pending intents remain durable when disabled.
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
- W8 broker consumers and W9 terminal publication/Outbox are implemented.

## W9 duplicate safety and recovery

Migration 006 preserves legacy `NO_DRIVER` rows without a result identity. On
replay, the terminalizer repairs identity and intent in one transaction only if
attempts are conclusively rejected, the snapshot is covered, and cancellation is
absent. Unsafe legacy rows return `LEGACY_TERMINAL_REQUIRES_REVIEW`.
Existing intents must match payload, type, schema, route, aggregate identity and
version, Saga identity and operation key; conflicts fail before terminal commit.

Driver projection deliveries store a semantic fingerprint at
`driver:projection:receipt:<SHA256(messageId)>` in the same Redis function call as
the version/session guarded projection and GEO update. Conflicting content raises
`MESSAGE_ID_CONTENT_CONFLICT` and follows the existing poison/DLQ policy. Receipts
have no TTL and share the derived projection's rebuild lifecycle; clear them
with a deliberate complete projection rebuild, never independently during normal
consumption. Redis is derived state: loss of its data also loses these conflict
receipts. FCALL prevents interleaving; it does not roll back arbitrary server
errors. Dependency errors retry and version/session guards converge redelivery.

`CrashAfterReserveRecoveryTests` is the REL-02 W9 executable base: real PostgreSQL
and Driver repository, exception injected after remote commit before local
`RESERVED`, followed by fresh repository/orchestrator recovery from persisted
`IN_FLIGHT`. Recovery replays the same reservation through the existing reserve
reconciliation path. It proves one attempt, no next candidate, no false
`NO_DRIVER`, and unchanged Driver version. It is not a process-kill or HTTP
transport proof. W11 owns external kill/restart orchestration; it was not started.
See [W9 evidence](../../../../evidence/member-b/w09-outbox/README.md).

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

## B-REC-01 — autonomous production execution (forward repair on W12)

`RideRequestedV1` is a durable intake, not an execution shortcut:

1. `RideRequestedHandler` parses the canonical payload (`tripId`,
   `matchingRound`, `pickup`) and commits the round, pickup center,
   `auto_execute = TRUE`, input envelope identity and any pending
   `CancelMatchingRoundV1` tombstone in ONE transaction before ACK.
2. `MatchingWorkDispatcher` (hosted service) scans `PROCESSING` auto-execute
   rounds from PostgreSQL, takes a per-round `pg_try_advisory_lock` lease, and
   drives the durable state machine. A crashed process drops its session lease;
   recovery is the next scan, never an in-memory queue or test endpoint.
3. Unresolved `IN_FLIGHT`/`UNKNOWN` attempts are reconciled first with the SAME
   `reservationId`; no new Driver is attempted while unresolved. READY rows are
   transitioned to write-ahead `IN_FLIGHT` before the remote call.
4. `CancelMatchingRoundV1` is consumed from `matching.commands.q`. A durable
   tombstone survives a cancel overtaking its ride request; the worker starts no
   new attempt and reconciles an unresolved effect query-only. A committed
   reservation remains authoritative (Trip owns exact cleanup).

`MatchingWorkRepository` owns the durable coordination: bounded due-round
discovery with `work_checked_at` backoff, atomic stage-snapshot + READY attempt
creation, and conditional stage advancement. Worker failures record
`work_error_code` and leave the round PROCESSING; a Redis/Driver outage is never
converted into `NO_DRIVER`.

## DSI-021 — progressive stages

`Matching:Stages` is deployment configuration (`S0..Sn`, strictly increasing
radii). Production ships S0/S1/S2 at 5/8/12 km. The round row persists
`current_stage` and `stage_evaluated`; each attempt row carries its logical
stage. A stage advances only when every frozen entry and attempt in that stage
is conclusively `REJECTED`, no unresolved/reserved effect exists, and the round
is still PROCESSING. `NO_DRIVER` is legal only at the final configured stage
whose discovery completed. The frozen snapshot is appended per stage; existing
entries and reservation identities are never rewritten.

Proofs: `ProductionMatchingWorkflowTests`, `ProductionMatchingStageTests` and
`MatchingStageGateTests` in the Matching integration project run the real
Matching process against real PostgreSQL/Redis/RabbitMQ and real Driver Kestrel
endpoints. See
[B-REC-01/DSI-021 evidence](../../../../evidence/member-b/b-rec01-dsi021/README.md).

## NOT implemented (later weeks)

- W13 performance experiments (E01/E02/E03) and W14 HA validation are not started.
- The internal `POST /internal/matching/rounds` seam remains a W5 demo trigger;
  production rounds are worker-owned and it does not initiate reserve work.
