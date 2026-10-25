# A-W11-10 — Invariant auditor review (cross-service properties)

- **Run ID**: A-W11-10 / code 6047a7a
- **Status**: A-owned review complete. The invariant auditor itself is
  spec-only in Doc 03 §11 (AUD-01..08) with no team-owned implementation in
  the tree yet (`scripts/evidence/` is a placeholder; nothing names `AUD-*`).
  Per the mission rule, no second generic auditing framework was created;
  this record is the review + cross-owner handoff.

## Review: canonical property vs where it is actually proven today

| Property | Locally auditable in `trip_db`? | Current proof | Gap / owner |
| --- | --- | --- | --- |
| Trip terminal monotonicity | YES | Domain guards (DSI-010) + W11 process proofs: CANCELLED never reactivated, COMPLETED never regressed (stale/duplicate outcomes), Version/StepVersion frozen at terminal | none (A) |
| Trip single-current assignment | YES | Aggregate guard + W06 uniqueness proofs; W11 keeps assignment identity exact through restart | none (A) |
| Driver–Trip reservation agreement | NO — needs Driver authority | Trip-side proxies only: exact release command identities + release-confirmation receipts | **B** (driver_db read or B-owned audit output) — AUD-04 |
| Exact compensation release | YES (Trip side) | Cleanup intents + release outbox rows carry the exact reservationId; W11-05 asserts exact identity to COMPENSATED | B-side release effect (does Driver agree the SAME reservation is gone?) — **B** |
| One operationKey per Saga Payment step | YES | Frozen lineage (`{tripId}:authorize|capture|void:v1`), DB CHECK on `ride_sagas`, deterministic outbox messageId as a second fence; W11 asserts lineage through crash/restart | none (A) |
| Saga step ↔ Trip state consistency | YES | Every step pairs with the Trip transition in ONE EF unit of work (W10/W11 tests assert the pair through kill/restart) | none (A) |
| No orphaned required Outbox intent | YES | Intent staged in the same transaction as its transition (W09); relay publishes or marks FAILED_VISIBLE (visible, not silent); W11 proves pending intent survives SIGKILL and is published after restart | AUD-08 "stuck beyond policy" age alerting is operational tooling — **C** dashboards (W12 scope) |
| No duplicated authoritative business effect | PARTIAL | Trip-side: durable receipts + domain replay (same-messageId replay and logical-duplicate BusinessReplay proven at process level in W11) | Payment/Driver-side effect counts are **C/B** authority — a trip_db-only audit must NOT claim them |
| No lost unresolved compensation | YES (Trip side) | Cleanup ledger + saga compensation status; W11-05 proves obligations survive a crash and COMPENSATED requires both legs | B/C leg confirmations still harness-published (pending legs below) |

## Soundness rule (kept)

The auditor must not pretend a single Trip database proves a Payment or
Driver effect. Today's A-owned proofs deliberately stop at the correct
boundaries: transport delivery of the exact command identities (verified on
the real broker) plus Trip-authoritative state. Nothing in this bundle claims
a Payment or Driver authoritative effect.

## Cross-owner handoff (compact)

To **C** (fault platform + auditor data):

1. Adopt the frozen faultpoint contract (`TripFaultPointGate` doc comment /
   `scripts/fault/README.md`): name, relay pre-publish location, saga-dispatch
   scope, double-gated activation, marker/release file signaling, bounded
   wait. A migrates behind `FaultPointProvider` with unchanged semantics.
2. When the auditor tool is built per Doc 03 §11, `trip_db` supports these
   local queries: pending/failed-visible outbox by age (AUD-08), saga step
   vs terminal Trip consistency, duplicate operation-key scan (expected none
   by CHECK), unresolved cleanup intents on terminal trips.
3. Payment-side join (AUD-05 "one logical effect") requires
   `payment_db.operation` rows keyed by the same operationKey lineage; C owns
   that database read.

To **B** (Driver agreement, AUD-01/02/04/07):

1. Driver–Trip agreement needs `driver_db` current-reservation state; the
   W11 proofs publish Driver facts with correct identity metadata, and the
   exact release identities are asserted Trip-side.
2. B-W10's Driver command consumer (release/mark-started/mark-completed) is
   the missing leg that would turn "transport delivered" into "authoritative
   downstream effect" in future E2E proofs.

## Appendix — A-W11-06 timing audit record

`Task.Delay`/`Thread.Sleep` occurrences in A-owned tests were enumerated and
classified: (a) bounded poll intervals inside deadline-bounded predicate
loops (W08 `Eventually`, W09 `RunUntilAsync`, W11 `TripW11Wait.UntilAsync`) —
the canonical allowed pattern; (b) one intentional unbounded hold parking the
W08 REL-06 consumer in the exact after-commit-before-ACK window,
synchronized by a `TaskCompletionSource` (an awaitable barrier, not timing);
(c) one fixed negative-observation window (W09 broker-unreachable) whose END
is the oracle ("nothing burned in 5 s"). No sleep substitutes for
establishing a failure window; no unbounded retry loops; no clock-dependent
ordering. Nothing required repair; no stable test was rewritten for style.
