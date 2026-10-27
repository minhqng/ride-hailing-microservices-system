# B-W12 Phase 2 — Formal regression, authoritative audit, G4 and handoff

**Verdict: `B-W12 DELIVERABLES COMPLETE — G4 SECURITY BLOCKED`**

Branch `feature/B-W12-observability-g4`, PR
[#45](https://github.com/minhqng/ride-hailing-microservices-system/pull/45),
based on verified W11 head `3ba3724e7e7df378e8c234af685c027ad9d26863`
(PR #44, base `main` `86062af028d01b86f07b1c902f040a41c41fe57f`).

| Item | Value |
|---|---|
| Tested B code head | `7bc4510cbf524406d545b7529c938414b85f9c9c` |
| Tested tree | `27ba100e59bec471489a5f7cdfaa5dc7c54361b9` |
| Formal full solution | 1099 passed, 1 skipped, 0 failed (1100 total) |
| W12 focused | 19/19 |
| B auditor | 14 runs / 12 negative controls; positive AUD-01/02/04/06/07/08 PASS; AUD-03/05 NOT RUN (A/C) |
| W11 critical campaign | five consecutive passes at the audit commit; production `src/` tree identical to the final head |
| Overall G4 | **FAIL/BLOCKED — SEC-AUTH-01 Critical open; DSI-021 NOT RUN** |

## What landed in Phase 2

### B-W12-07 — formal full regression
A Release build and full solution test run against the committed Phase 2
source, with real PostgreSQL/RabbitMQ/Redis for infrastructure-bound suites.
Per-project counts and exact commands are in `g4/regression-summary.json` and
`test-output/formal-full-regression.txt`. No test assertion was weakened and no
B-owned correctness regression was found. The SystemTests project discovers
zero tests and is recorded as NOT RUN, never counted as PASS. The Identity auth
test skip is recorded separately, not counted as verification.

### B-W12-08 — authoritative invariant auditor
`scripts/audit/b-w12-g4.sh` runs the B-owned test-scope auditor over disposable
PostgreSQL environments and real scenario final states. It uses read-only
verification transactions, implements AUD-01..08 with the required result
contract (`runId`, `sourceCommit`, `scenarioId`, `observationMode`,
`authoritySources`, `inspectedRecordCount`, `status`, `findings`,
`elapsedTime`), and writes `auditor/auditor-results.{json,md}` plus
`auditor/negative-controls.md`.

- AUD-01/02/04/06/07/08: PASS on real positive scenarios (reserved agreement,
  no-Driver terminal, cancel+release, driver lifecycle, late-release fencing,
  recognized pending cleanup).
- AUD-03: NOT RUN — A-owned Trip auditor; source/scenario provenance differs.
  B does not duplicate it and does not reuse A's result as B's PASS.
- AUD-05: NOT RUN — C-owned Payment authority.
- Negative controls: contradictory Matching terminal facts, missing terminal
  intent, leaked reservation after a falsely confirmed cleanup, stuck cleanup,
  Driver–Trip disagreement, stuck unresolved rows, empty dataset, unreachable
  authority. Constraint-enforced guards are demonstrated as enforcement.
- A PASS requires the owning authority; proxy evidence cannot silently become
  PASS. A's PR #43 TripInvariantAuditor was reviewed for its status discipline;
  B's auditor is independently implemented and does not copy A's branch.

### B-W12-09 — G4 assessment
`g4/coverage-matrix.{md,json}` contains every required FUN/CON/REL/DSI/DSL row
with contract id, requirement, proof, source SHA, authority, result, evidence,
limitation and owner, plus the seven architectural prohibitions. The verdict is
in `g4/g4-verdict.md`. **G4 remains FAIL/BLOCKED** while SEC-AUTH-01 exists;
DSI-021 has no executable proof and is not filled with PASS.

### B-W12-10 — cross-team handoff
`cross-team-handoff.md` explains Driver correctness (OCC linearization, exact R
fencing, Outbox atomicity, lifecycle provenance, late compensation), Matching
correctness (durable attempts, same-R reconcile, NO_DRIVER terminalizer, Redis
projection-only), fault boundaries, observability semantics, and G4/security
responsibilities. Status: `HANDOFF DELIVERED / HUMAN REVIEW PENDING / PEER
RERUN PENDING`.

## Security (release blocker)

Real HTTP probes reproduce unauthenticated Driver mutations and Outbox creation:

| ID | Severity | Status |
|---|---|---|
| SEC-AUTH-01 | CRITICAL | OPEN |
| SEC-AUTH-02 | HIGH | OPEN |
| SEC-AUTH-03 | HIGH | OPEN |
| SEC-AUTH-04 | MEDIUM | OPEN |
| SEC-07-01 | MEDIUM (partial) | body bound present; no rate limit |
| SEC-08 | INFO | DB ownership tests |
| SEC-09 | PASS | secret scan |

These are reproduced exposures, not successful security enforcement. The
remediation agreement is `security/auth-remediation-handoff.md`. No fake auth,
unsigned-header trust or invented Identity provider was introduced.

## Evidence map

- `test-output/formal-full-regression.txt` — build + full solution output.
- `test-output/w11-critical-regression.txt` — five-pass W11 campaign on the W12 source.
- `g4/regression-summary.json` — machine-readable counts/environment/commands.
- `g4/coverage-matrix.md`, `g4/coverage-matrix.json` — G4 checklist.
- `g4/g4-verdict.md` — red verdict and exit criteria.
- `auditor/` — auditor JSON/Markdown/controls.
- `security/auth-remediation-handoff.md` — remediation agreement.
- `cross-team-handoff.md` — B-W12-10 execution handoff.
- `manifest.md` — Phase 2 source binding and commands.
- `hashes.txt` — SHA-256 over every deliverable.

## Non-claims

- G4 is not green; SEC-AUTH-01..04 remain open.
- AUD-03 (Trip) and AUD-05 (Payment) are not run by B.
- DSI-021 has no executable proof.
- FUN-05 is PARTIAL for B; A/C end-to-end integration is pending.
- No W13 performance work.
- CI did not trigger for the stacked base; zero checks is not green.
- No A/C peer rerun or human sign-off yet.

## Phase 1 (historical)

Phase 1 delivered B-W12-01..06 (metrics, tracing, security exposure, invalid
location). Its evidence (1083 passed / 1 skipped, 19/19 focused) remains in
`metrics/`, `traces/`, `security/inventory.md`, `security/http-probes.txt` and
the Phase 1 test outputs. Phase 2 preserves those results and adds the four
remaining deliverables.


---

# Appendix — Phase 1 README (historical full text)

# B-W12 Phase 1 — Observability, tracing and security

**Verdict: `B-W12 PHASE 1: CODE VERIFIED — SECURITY/EXTERNAL GATES REMAIN`**

Branch `feature/B-W12-observability-g4`, based on verified W11 HEAD
`3ba3724e7e7df378e8c234af685c027ad9d26863` (PR #44, open against `main`
`86062af028d01b86f07b1c902f040a41c41fe57f`). The W12 PR targets the W11
branch, not `main`.

| Item | Value |
|---|---|
| Implementation commit | `699a80151984ce91e3990be5621526df8bdcca56` |
| Evidence commit | `3f2ed52` (bundle) + final metadata commit on this PR head |
| PR | https://github.com/minhqng/ride-hailing-microservices-system/pull/45 |
| Tested source | `699a80151984ce91e3990be5621526df8bdcca56` (evidence adds docs only) |
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
- Remote CI did **not trigger** for this stacked PR: `ci.yml`
  `pull_request.branches` excludes the W11 base. Zero check runs is not green;
  a billing-blocked run would also not be green.

## Evidence map

- `manifest.md` — source binding, environment, commands, hashes.
- `handoff.md` — status, remaining work, owners.
- `metrics/semantics.md`, `metrics/measured-values.md`.
- `traces/proof-matrix.md`, `traces/reserve-trace.txt`.
- `security/inventory.md`, `security/http-probes.txt`, `security/secret-scan.txt`.
- `test-output/` — full solution and focused runs.
