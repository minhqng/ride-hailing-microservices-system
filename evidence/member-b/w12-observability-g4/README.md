# B-W12 Phase 1 — Observability, tracing and security

**Verdict: `B-W12 PHASE 1: CODE VERIFIED — SECURITY/EXTERNAL GATES REMAIN`**

Branch `feature/B-W12-observability-g4`, based on verified W11 HEAD
`3ba3724e7e7df378e8c234af685c027ad9d26863` (PR #44, open against `main`
`86062af028d01b86f07b1c902f040a41c41fe57f`). The W12 PR targets the W11
branch, not `main`.

| Item | Value |
|---|---|
| Implementation commit | `4f523d10de592a5bd05585abb19f68b540e8b867` |
| Evidence commit | `<EVIDENCE_SHA>` |
| PR | `<PR_URL>` |
| Tested source | `4f523d10de592a5bd05585abb19f68b540e8b867` (evidence adds docs only) |
| W12 focused proofs | 19/19 passed |
| Full solution regression | 1083 passed, 1 skipped, 0 failed |

## What landed

### B-W12-01 Driver metrics
- `driver_reserve_success_total` — committed NEW reservation only; idempotent
  replay and DB/transport failure excluded.
- `driver_reserve_conflict_total{reason_class}` — definitive domain
  rejections; bounded labels; no IDs or exception text.
- `driver_stale_release_blocked_total{reason_class}` — only exact-token and
  reservation-round fencing, from HTTP and broker paths; idempotent receipts
  and provenance refusals excluded.
- `reserve_unknown` is deliberately **not** a Driver metric: Driver cannot
  infer a caller timeout from a committed transaction. The logical signal is
  `matching_unknown_total` at the caller.

### B-W12-02 Matching metrics
- `matching.round.duration` (histogram, monotonic), `matching.round.candidate_count`,
  `matching.round.total`, `matching.attempt.created_total`,
  `matching.unknown_total` (committed UNKNOWN transitions only),
  `matching.reconcile_total` (every authoritative poll), `matching.no_driver_total`
  (one NEW committed terminal; replay/relay excluded).
- Replay of a terminal round does not mint a new terminal metric;
  repeated execution on an already-UNKNOWN attempt does not re-count UNKNOWN.

### B-W12-03 Redis projection metrics
- `matching.projection.stale_state_reject`,
  `matching.projection.stale_location_reject` measured only at the real FCALL
  reply; `matching.projection.latency` covers the FCALL boundary.
- Redis unavailable returns `Degraded` and the real handler returns `Retry`;
  stale counters stay zero. Exact replays are `stale` because the frozen W4
  contract applies only strict advances (documented and tested).

### B-W12-04 Reserve/reconcile tracing
- B-owned `DriverTelemetry`/`MatchingTelemetry` add sources, meters and HTTP
  client instrumentation to the existing OTel pipeline; C-owned
  `PlatformBootstrap` is untouched.
- Verified chain: workflow → Matching round → attempt → HTTP reserve → Driver
  server → Driver transaction. REL-01 response loss yields
  `BlockedOnUnknown`/`RESERVE_TRANSPORT`, an Error client span (no fabricated
  200), a real committed Driver transaction, then same-R reconciliation.
- Privacy is asserted on a real location request as well as reserve spans.
- Limitation: trace context is not persisted in the Matching ledger; a
  reconciliation caller must supply a workflow/consumer span.

### B-W12-05 Authorization / actor spoofing
**OPEN — `SYSTEM_AUTH_PENDING`.** Identity and Gateway are skeletons; there is
no token/principal contract and no auth middleware. Real HTTP probes reproduce
unauthenticated mutation and Outbox creation. The exposure tests are tripwires,
not an authorization PASS. See `security/inventory.md`.

### B-W12-06 Invalid location data
Real Kestrel + binding + validator + monotonic acceptor + real Rabbit/Redis:
18 invalid requests (bounds, non-finite, malformed identity, session/seq/time,
stale, malformed JSON, 70 KiB body) leave the accepted sample, publication and
projection unchanged. Latitude 86 remains domain-valid, is accepted, stored in
the hash, and removed from GEO as non-indexable.

## Validation

- Release solution build: 0 errors.
- Full solution tests: 1083 passed, 1 skipped, 0 failed (1 Identity auth test skipped because
  `RIDE_AUTH_FIXTURE` is absent — consistent with `SYSTEM_AUTH_PENDING`).
- W12 focused: 19/19 (Driver unit 2, Driver integration 3, Matching
  integration 14).
- Existing W9/W10 Outbox/receipt, W11 REL/CON/Redis, Driver/Matching/Platform/
  Trip/Payment suites all pass.

## Non-claims

- W12-07…10 are not claimed complete (see `handoff.md`).
- No G4 acceptance, no W13 performance work, no invariant-auditor run.
- No authorization PASS; SEC-AUTH-01..04 remain open cross-team findings.
- No durable cross-restart trace linkage.
- Remote CI status is reported separately after publication; a billing-blocked
  run is not green.

## Evidence map

- `manifest.md` — source binding, environment, commands, hashes.
- `handoff.md` — status, remaining work, owners.
- `metrics/semantics.md`, `metrics/measured-values.md`.
- `traces/proof-matrix.md`, `traces/reserve-trace.txt`.
- `security/inventory.md`, `security/http-probes.txt`, `security/secret-scan.txt`.
- `test-output/` — full solution and focused runs.
