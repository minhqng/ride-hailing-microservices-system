# B-W12 metric semantics

Meters are registered on the existing OpenTelemetry pipeline by B-owned
`AddDriverTelemetry`/`AddMatchingTelemetry` extensions; C's
`PlatformBootstrap` remains the provider/exporter owner and was not modified.
No second exporter or observability stack was added. All labels are bounded
sets; no DriverId, TripId, reservationId, messageId or exception text is a
metric dimension.

## 1. Logical signal → instrument

| Brief signal | Instrument | Type | Labels | Location |
|---|---|---|---|---|
| `reserve_success` | `driver_reserve_success_total` | Counter<long> | none | Driver |
| `reserve_conflict` | `driver_reserve_conflict_total` | Counter<long> | `reason_class` | Driver |
| `reserve_unknown` | `matching_unknown_total` | Counter<long> | `reason` | **Matching** (caller observes uncertainty) |
| `stale_release_blocked` | `driver_stale_release_blocked_total` | Counter<long> | `reason_class` | Driver |
| `round_duration` | `matching.round.duration` | Histogram<double> ms | `outcome` | Matching |
| `candidate_count` | `matching.round.candidate_count` | Histogram<long> | none | Matching |
| `attempt_count` | `matching.attempt.created_total` | Counter<long> | none | Matching |
| `unknown_count` | `matching_unknown_total` | Counter<long> | `reason` | Matching |
| `reconcile_count` | `matching.reconcile_total` | Counter<long> | `outcome` | Matching |
| `no_driver` | `matching.no_driver_total` | Counter<long> | none | Matching |
| `stale_state_reject` | `matching.projection.stale_state_reject` | Counter<long> | none | Matching |
| `stale_location_reject` | `matching.projection.stale_location_reject` | Counter<long> | none | Matching |
| `projection_latency` | `matching.projection.latency` | Histogram<double> ms | `operation`, `outcome` | Matching |

Doc 02 §29.4 names map 1:1 to the logical signals; the existing W05
`MatchingMetrics` instance uses the repository's dotted convention
(`matching.candidates.*`, `matching.geo.query.duration`,
`matching.rounds.degraded`). W12 keeps that convention rather than creating a
second naming scheme. `matching.round.total` additionally counts every
orchestration request (`outcome`, `replay`), so the doc's round-total intent is
covered without conflating it with the terminal `no_driver` decision.

## 2. Truth boundary per instrument

| Instrument | Counts | Does not count |
|---|---|---|
| `driver_reserve_success_total` | A NEW reservation row + its Outbox intent committed (`Won`). | Same-reservation `Replayed` (no new effect); conflict/not-found; DB/transport failures. Emitted after the repository returns, i.e. after COMMIT. |
| `driver_reserve_conflict_total` | Definitive domain rejection (`Conflict`, `DriverNotFound`) mapped to a bounded reason class. The narrow `ux_drivers_reservation_id` backstop is a domain identity collision by W8 design. | Database/transport failures; invalid HTTP input rejected before the repository. |
| `driver_stale_release_blocked_total` | A release rejected by exact-token fencing (`STALE_RESERVATION`) or reservation-round fencing (`RESERVATION_ROUND_MISMATCH`), from HTTP or the broker path, after its transaction resolves. | Exact-token release success; coherent `AVAILABLE` idempotent no-op; missing Driver; provenance/legality refusals (`RELEASE_PROVENANCE_REQUIRED`, `RESERVATION_ROUND_REQUIRED`, `ILLEGAL_TRANSITION`). |
| `matching.round.duration` | Wall time of one `ExecuteRoundAsync` request, tagged with the returned outcome (`error` if it throws). | No terminal/conflict inference; duration is not a terminal decision count. |
| `matching.round.candidate_count` | Candidate-sequence length offered to a live (`PROCESSING`) round (caller input; normally the frozen snapshot). | Terminal replays (no candidate work). It is explicitly an offered-input quantity, not re-read from the ledger. |
| `matching.round.total` | One orchestration request, tagged `outcome` and `replay`; thrown requests are recorded as `error`. | It is a request counter, not a terminal business decision. |
| `matching.attempt.created_total` | One newly inserted durable `matching_attempts` row (write-ahead attempt identity). | Duplicate workers converging on the existing row; retries that reuse the reservation identity. |
| `matching_unknown_total` | A committed UNKNOWN attempt transition: reserve transport ambiguity or contention exhaustion where this worker won the guarded transition. | Ordinary domain conflicts; pre-effect version-read/fence failures that leave the attempt IN_FLIGHT; replay of an attempt already durably UNKNOWN; a lost race that reports another worker's durable terminal. |
| `matching.reconcile_total` | Every actual authoritative `GetReservationAsync` poll, including each repeated transport failure. | No fabricated success; no poll when the row was already terminal. |
| `matching.no_driver_total` | A committed NEW `NO_DRIVER` row plus its `MatchingFailedV1` Outbox intent. | Terminal replay that reuses the persisted result identity; relay duplicate publication. |
| `matching.projection.stale_state_reject` | FCALL state section returned `stale`. The frozen W4 contract applies state only when `version > projected`, so an exact equal-version semantic replay is also `stale` by contract. | Redis unavailable/`degraded`; key-type errors; skipped state section. |
| `matching.projection.stale_location_reject` | FCALL location section returned `stale` (same-session non-advancing) or `stale-session` (foreign session). | Exact replay is `stale` under the same frozen contract (no `>` advance); `degraded` is not counted. |
| `matching.projection.latency` | One FCALL execution: Redis round-trip plus the atomic function, including reply parsing. Each retry after a missing function produces its own sample; a content-conflict reply is recorded before throwing. | Handler/consumer time outside the FCALL; a reload that fails before any FCALL. |

## 3. Reason / outcome label domains

- `driver_reserve_conflict_total.reason_class`: `reservation_conflict`,
  `version_fence`, `stale_identity`, `identity_mismatch`, `illegal_transition`,
  `invalid_identity`, `driver_not_found`, `domain_conflict`.
- `driver_stale_release_blocked_total.reason_class`: `foreign_reservation`,
  `round_mismatch`.
- `matching_unknown_total.reason`: `reserve_transport`, `reserve_contention`.
- `matching.reconcile_total.outcome`: the seven Doc 02 §12.6 outcomes plus
  `transport_failure`.
- `matching.projection.latency.operation` / `.outcome`: `state|location` /
  `applied|stale|stale_session|skipped|degraded|error|content_conflict`.

No label uses a business identity, message identity, raw exception text or
unbounded value.
