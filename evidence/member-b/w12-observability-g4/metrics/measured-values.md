# B-W12 measured instrument values

Each value below is asserted by a test that reads a real `MeterListener`
measurement. No value is computed from logs or guessed.

## 1. Driver (real PostgreSQL; real Kestrel for the HTTP case)

Test: `DriverMetricsTests.Repository_Counts_Committed_Effects_Domain_Conflicts_And_Stale_Release_Only`.

| Observation | Measured | Oracle |
|---|---|---|
| New reserve win | `driver_reserve_success_total = 1` | Driver row `RESERVED`, version bumped, one `DriverReservedV1` intent |
| Same-reservation replay | success stays `1` | no new authoritative effect |
| Foreign live reservation | `driver_reserve_conflict_total{reason_class=reservation_conflict} = 1` | HTTP/domain conflict, no mutation |
| Stale expected version | `driver_reserve_conflict_total{reason_class=version_fence} = 1` | no mutation |
| Unknown driver | `driver_reserve_conflict_total{reason_class=driver_not_found} = 1` | no row |
| Database unreachable | success/conflict unchanged (`1` / `3` total) | `NpgsqlException`; no domain metric |
| Foreign-token release | `driver_stale_release_blocked_total{reason_class=foreign_reservation} = 1` | Driver still owns the live reservation |
| Exact release, then idempotent replay | stale counter stays `1` | `Applied` then `Unchanged`; no second mutation |
| Broker stale release (foreign reservation) | `driver_stale_release_blocked_total{reason_class=foreign_reservation} = 1` | `Rejected` receipt; state unchanged |
| Broker compensation after exact HTTP release | stale counter stays `1` | `RELEASE_PROVENANCE_REQUIRED` refusal is not a stale-release signal |
| Unit predicates | `IsStaleReleaseBlocked` is true only for `STALE_RESERVATION`/`RESERVATION_ROUND_MISMATCH`; false for provenance/round-required/illegal/null | `DriverMetricSemanticsTests` (2/2) |

Test: `DriverMetricsTests.Http_Reserve_And_Release_Emit_The_Same_Truthful_Counts`.

| HTTP action | Measured |
|---|---|
| `POST /reserve` win (200) | success `1` |
| Same reservation replay (200) | success `1` (no inflation) |
| Foreign reservation conflict (409) | conflict `1` |
| Stale release (409) | stale blocked `1` (`foreign_reservation`) |
| Exact release (200) + idempotent replay (200) | stale blocked stays `1` |

## 2. Matching (real matching_db attempt/round ledger + real terminalizer)

Tests: `MatchingMetricsTests.*`.

| Scenario | Measured |
|---|---|
| Successful round, 2 candidates | `matching.attempt.created_total=1`; `matching.unknown_total=0`; `matching.no_driver_total=0`; `matching.reconcile_total=0`; `matching.round.total{outcome=Reserved}=1`; `matching.round.candidate_count=[2]`; `matching.round.duration` one sample |
| Two conclusive rejections | `attempt.created_total=2`; `unknown_total=0`; `no_driver_total=1` |
| Exact replay of the NO_DRIVER round | attempts stay `2`; `no_driver_total` stays `1`; `round.total{replay=True}=1`; live candidate-count samples stay `1` |
| Reserve response lost (transport) | `attempt.created_total=1`; `unknown_total{reason=reserve_transport}=1`; `no_driver_total=0` |
| Repeat reserve execution on the already-UNKNOWN attempt | `unknown_total` stays `1`; `attempt.created_total` stays `1` |
| Reconcile with three transport failures | `reconcile_total{outcome=transport_failure}=3`; `unknown_total` stays `1`; result `NeedsReview` |
| Same-R reconcile resolves `OWNED_BY_THIS_RESERVATION` | `reconcile_total{outcome=OWNED_BY_THIS_RESERVATION}=1` (total 4); `unknown_total` stays `1`; round `RESERVED` with the same reservation |
| Round argument throws | `round.total{outcome=error}=1`; duration one `error` sample |

## 3. Redis projection (real Redis 8.10.2 + real Lua function + real handler)

Tests: `ProjectionMetricsTests.*`.

| Scenario | Measured |
|---|---|
| v10 `RESERVED` then v9 `AVAILABLE` through `DriverProjectionHandler` | `matching.projection.stale_state_reject = 1`; hash remains version `10`, status `RESERVED`; no double increment from handler+updater |
| seq20 then seq19 through the handler | `matching.projection.stale_location_reject = 1`; hash remains seq `20`, coordinates unchanged |
| Exact v10 state replay | `stale_state_reject` increments once more per call — the frozen contract classifies equal version as `stale` (documented exception) |
| Exact seq20 location replay | `stale_location_reject` increments once per call — frozen contract requires strict advance |
| Foreign session (`stale-session`) | `stale_location_reject` increments once more |
| Content-conflict reply | handler throws `ArgumentException`; latency histogram has a second sample for the FCALL that produced the conflict |
| Redis unavailable (`127.0.0.1:6399`) | `ProjectionResult.Degraded=true`; the real handler returns `HandlerOutcome.Retry`; both stale counters `0`; two latency samples with `degraded` |
| Latency | four FCALL samples for the four handler deliveries; each `>= 0 ms`, operation/outcome tagged |

## 4. HTTP location ingress

Test: `DriverLocationIngressTests.Invalid_Location_Data_Is_Rejected_Without_Mutation_Publication_Or_Projection_Corruption`.

- Baseline accepted sample: seq `10`, lat `10.8`, lon `106.6`; exactly one
  `DriverLocationAcceptedV1` published; projection hash seq `10`; GEO member present.
- 18 invalid requests cover: lat `91/-91`, lon `181/-181`, overflow literal,
  non-numeric `NaN`, malformed route UUID, missing/empty/null `deviceSessionId`,
  negative seq, default/future/old `capturedAt`, two stale samples, malformed
  JSON and 70 KiB body.
- Observed classifications: `400 INVALID_COORDINATE` for bounds/non-finite;
  `400 INVALID_LOCATION` for session/sequence/time; `404` for malformed route
  identity; `409 STALE_LOCATION` for stale samples; `400` for malformed JSON;
  `413` for oversized body.
- After all invalid requests: accepted sample still seq `10` with original
  coordinates; queue has no new message; projection hash still seq `10`; GEO
  position unchanged.
- Preservation case: latitude `86` is accepted (`202`) and published; after
  applying its event the hash stores `86` but the GEO member is removed because
  `86 > 85.05112878`. Domain-valid coordinate is not rejected as invalid.

## 5. Security exposure (real Kestrel; counts are observations, not PASS)

Test: `DriverAuthorizationExposureTests.*`.

| Observed exposure | Result |
|---|---|
| Unauthenticated online | `200`; version `1→2`; one `DriverAvailableV1` intent |
| Unauthenticated create/read | `201`; arbitrary `userId` creates a Driver; `GET` returns `200` |
| Unauthenticated offline | `200`; `OFFLINE`; one `DriverOfflineV1` intent |
| Spoofed `X-Driver-Id`/`X-Role`/unsigned Bearer mutating another Driver | `200`; target version `1→2`; one Outbox intent |
| Rider role performing Driver-only online | `200`; target `AVAILABLE` |
| Unauthenticated internal reserve | `200`; `RESERVED`; one `DriverReservedV1` intent |
| Unauthenticated internal reservation read | `200`; reservation disclosed |
| Unauthenticated internal release | `200`; `AVAILABLE`; one `DriverReservationReleasedV1` intent |

These are open findings (`SYSTEM_AUTH_PENDING`); they are not an authorization pass.
