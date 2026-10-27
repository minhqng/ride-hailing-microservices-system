# B-W12-10 — Cross-team execution handoff (Driver / Matching)

**Status markers: `HANDOFF DELIVERED` · `HUMAN REVIEW PENDING` · `PEER RERUN PENDING`**

Audience: lead, Member A (Trip/Saga), Member C (Payment/Platform/Gateway).
Tested B source: see `manifest.md` (Phase 2). All runtime claims below are
executable from the B worktree unless marked as A/C-owned.

## 1. Driver correctness

- **OCC and the reservation linearization point.** Reserve/online/offline/
  release open a short PostgreSQL transaction, `SELECT ... FOR UPDATE` the
  Driver row, then decide on the fresh row (`DriverRepository` /
  `DriverLifecycleRepository`). The row lock is the linearization point; there
  is no check-then-act window across processes. A stale `expectedVersion`
  loses to `STALE_VERSION`; a same-`(tripId, reservationId)` replay returns the
  canonical success before the version fence. Cross-driver reservation identity
  is also unique (`ux_drivers_reservation_id`).
- **Exact R fencing.** Release carries the exact trip + reservation token.
  On a live RESERVED row a mismatch throws `STALE_RESERVATION` without
  mutation; duplicate/no-op cleanup is idempotent only when the end state
  already holds. Release has no version parameter; token equality on the locked
  row is the complete guard. Broker Release adds the same exact identity plus
  matching-round provenance and writes a durable receipt.
- **Outbox atomicity.** Every authoritative effect stages its canonical event
  through `DriverOutboxWriter` on the same connection/transaction as the state
  row. The relay publishes after commit and reuses the stored message identity
  after crash/restart. A required-intent insert failure rolls the state change
  back (`DriverEventPublishTests`).
- **Start/Complete provenance.** `driver_lifecycle_effects` is append-only,
  keyed by exact `(driver, trip, reservation, phase)`, and retains the original
  version/message identity after the live reservation fields clear. Completed
  identities cannot be newly reserved; legacy ON_TRIP rows without provenance
  fail safely rather than being guessed. Historical lifecycle rows are never
  treated as current reservations by the auditor (AUD-02).
- **Late compensation safety.** `Reserve R1 -> Release R1 -> Reserve R2 ->
  late Release R1` keeps R2 current and rejects R1 with `STALE_RESERVATION`.
  The real-broker variant (CON-04) publishes the late Release after R2 commits
  and records a durable rejected receipt; exact redelivery replays the original
  receipt (`DriverCompensationTests`, W11 campaign).

## 2. Matching correctness

- **Durable IN_FLIGHT/UNKNOWN attempts.** Before the remote Reserve, Matching
  persists the attempt identity and dispatch fence (`max_dispatched_version`).
  Transport uncertainty becomes UNKNOWN; it is never a business rejection and
  never advances to the next candidate. The same reservation identity is
  reconciled with version fencing (AVAILABLE is conclusive only when the
  Driver version exceeds every dispatched fence).
- **Same-R reconciliation.** `ReconcileAsync` reuses the persisted attempt and
  reservation id; it never mints a fresh identity. Bounded reserve/reconcile
  budgets make exhaustion a visible terminal state, not a silent retry loop.
- **NO_DRIVER terminalizer.** `MatchingTerminalizer` locks the round row, then
  requires: PROCESSING status, no cancellation, every attempt conclusively
  REJECTED, and full snapshot coverage. It commits `NO_DRIVER` + a deterministic
  `result_message_id` + `MatchingFailedV1` in one transaction; replay reuses the
  persisted identity and repairs a missing intent only when the same safety
  boundary holds.
- **Redis is derived-projection only.** The projection applies monotonic
  version/sequence guards in the production Lua FCALL and is never reservation
  authority. A Redis outage returns `Degraded`/`Retry` with zero fabricated
  candidates or reservations; after recovery the tests prove surviving data and
  event-driven reconstruction separately (no automatic full rebuild claim).

## 3. Fault proof boundaries

- **REL-01** uses the Testing-only `Driver.AfterReserveCommitBeforeResponse`
  barrier after the real Driver commit; Matching's real HTTP call times out into
  UNKNOWN, makes zero candidate advance, then reconciles the same R.
- **REL-02** SIGKILLs the real Matching container while it is blocked on the
  committed reserve (exit 137), starts a new container on the same PostgreSQL/
  RabbitMQ, and reconciles the persisted attempt/identity.
- **Five-pass campaign** re-runs REL-01/02 plus CON-02/04 and REL-12/13/14 with
  fresh containers and identities on the B tested source. The Phase 2 summary
  is in `test-output/w11-critical-regression.txt`; raw campaign details are
  retained under the W11 evidence bundle. The campaign ran at the audit commit
  `60f5cd4018b259c7f483984baf288e500732c493`; the final code commit adds only
  auditor tests/fix, and the production `src/` subtree is identical
  (`0a2dab00e04a8ef2b43076472ec5c03641feab5c`). The final full-solution
  regression re-executed the W11 component tests.
- **CON/Redis reproduction** is component-level with real PostgreSQL/RabbitMQ/
  Redis. Faults are deterministic barriers or network gates; no sleep-based
  race oracle is used.

## 4. Observability

- **Metric names/counting semantics.** Driver: `driver_reserve_success_total`
  (committed NEW reservation only), `driver_reserve_conflict_total{reason_class}`
  (definitive domain rejections, bounded labels),
  `driver_stale_release_blocked_total{reason_class}` (token/round fencing only).
  Matching: `matching.round.duration`, `matching.round.candidate_count`,
  `matching.round.total`, `matching.attempt.created_total`,
  `matching.unknown_total` (committed UNKNOWN only), `matching.reconcile_total`,
  `matching.no_driver_total` (one NEW committed terminal). Projection:
  `matching.projection.stale_state_reject`,
  `matching.projection.stale_location_reject`, `matching.projection.latency`.
  Replays and duplicate relays do not re-mint business counters.
- **Reserve/reconcile trace chain.** In-process: workflow span -> Matching round
  -> attempt -> HTTP reserve client -> Driver server -> Driver transaction.
  REL-01 yields an Error client span, no fabricated success, a real committed
  Driver transaction, then same-R reconciliation. The caller must supply a
  workflow/consumer span for reconciliation; traceparent is not persisted in the
  Matching ledger (recorded limitation).
- **Cardinality/privacy.** Labels are bounded reason classes only; no IDs,
  exception text or coordinates. The privacy assertion covers a real location
  request as well as reserve spans. Do not add high-cardinality IDs to metric
  labels.

## 5. G4 and security

- **Auditor results/limits.** `auditor/auditor-results.json`: AUD-01/02/04/06/
  07/08 PASS on real B scenarios; AUD-03 NOT RUN (A-owned Trip auditor; source
  provenance differs); AUD-05 NOT RUN (C-owned Payment authority). AUD-08 checks
  Driver/Matching Outbox/attempt ages only; Trip Saga is A-owned. Seeded
  negative controls prove detection; constraint-enforced guards are demonstrated
  instead of bypassed. AUD-07 reads the Driver row, so a sent/confirmed cleanup
  is never proof of release.
- **Confirmed authentication vulnerability.** SEC-AUTH-01 Critical: real HTTP
  probes mutate Driver authority and create Outbox intents without any
  credential. SEC-AUTH-02 High (actor spoofing), SEC-AUTH-03 High (internal
  routes exposed), SEC-AUTH-04 Medium (no object-level authz). These are
  reproduced exposures, not passing negative tests. G4 remains FAIL/BLOCKED
  until remediation and retest.
- **Identity / Gateway / B responsibilities.** A: authenticated principal/token
  contract and claims. C: Gateway validation, internal route network isolation.
  B: Driver/Matching service-side enforcement after the shared contract, plus
  the reject-without-mutation matrix. Detailed agreement:
  `security/auth-remediation-handoff.md`. No unsigned-header trust, dev JWT key
  or fake Identity implementation was introduced.
- **CI trigger/billing.** PR #45 has zero check runs because `.github/workflows/
  ci.yml` `pull_request.branches` excludes the W11 base (and the shared workflow
  is leader-owned). Zero checks is not green; a billing-blocked run is not green.
- **Peer rerun required.** AI review is not a substitute for A/C execution.
  A/C must rerun the B procedures from this pack and confirm the Driver/Matching
  explanations before G4 sign-off.

## 6. B-W12 completion table

| Task | Disposition |
|---|---|
| B-W12-01 Driver metrics | **COMPLETE** |
| B-W12-02 Matching metrics | **COMPLETE** |
| B-W12-03 Redis projection metrics | **COMPLETE** |
| B-W12-04 Reserve/reconcile tracing | **COMPLETE** (in-process chain; durable cross-restart traceparent is a recorded limitation) |
| B-W12-05 Driver authz/spoofing | **OPEN — SECURITY DEPENDENCY** (`SYSTEM_AUTH_PENDING`, SEC-AUTH-01..04) |
| B-W12-06 Invalid location | **COMPLETE** |
| B-W12-07 Formal full regression | **COMPLETE** (see `g4/regression-summary.json`; no B-owned correctness regression) |
| B-W12-08 Authoritative auditor | **COMPLETE — A/C OWNER DEPENDENCIES RECORDED** (AUD-03/05 NOT RUN, never PASS) |
| B-W12-09 G4 assessment | **COMPLETE — G4 RED/BLOCKED** (security + DSI-021 evidence gap) |
| B-W12-10 Cross-team handoff | **COMPLETE — HANDOFF DELIVERED / HUMAN REVIEW PENDING / PEER RERUN PENDING** |
