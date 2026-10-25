# A-W12 — Trip/Saga Observability, Authorization Hardening & Core Release Gate

Member A Week 12 deliverables (Doc 05A §20), stacked on the verified A-W11
head (`46ac68a`, PR #42). Code revision verified by this bundle:
`976c7f8662e05c0abe17a0dedcf8a45d94b03592`.

## Task matrix (A-W12-01..09)

| Task | Status | Where |
| --- | --- | --- |
| 01 Trip domain spans/logs | DONE | `TripDiagnostics` ActivitySource `RideHailing.Trip`: CreateRide / CancelRide endpoint spans + `ApplyAuthority` consumer span; safe fields only (trip.id, trip.state, saga.id, saga.step, messaging.message.id/correlation.id/type, ride.outcome/rejection); NeedsReview logs at Warning | `src/Services/Trip/RideHailing.TripService/Integration/Observability/TripDiagnostics.cs`, `Api/CreateRideEndpoint.cs`, `Api/CancelRideEndpoint.cs`, `Integration/Messaging/TripMessageHandler.cs` |
| 02 Trip/Saga/Outbox metrics | DONE | Meter `RideHailing.Trip`: `trip.created`, `trip.cancelled`, `trip.transition.rejected{reason}`, `saga.compensation`, `saga.requires_review` counters (post-commit only) + `saga.active{step}` ObservableGauge from durable rows via `TripMetricsObserver`; C's `outbox.pending`/`outbox.oldest_pending.age` reused unchanged | `Integration/Observability/*`, `metrics/aw12-02-instruments.md` |
| 03 Async trace propagation | DONE, PROVEN on real broker | Creation context → durable `headers_json` → `outbox.publish` → wire `traceparent` → real `ManualAckConsumer` consume span; one traceId, correct parent chain across EF persistence + publication delay + broker redelivery | `TripW12TracePropagationTests` |
| 04 Authz/invalid-transition negatives | DONE | Terminal-state cancels rejected with durable state+version unchanged and zero outbox rows; malformed UUIDs never route; body actor fields inert (SEC-04); faultpoints not activatable via public traffic | `TripW12AuthzNegativeTests` (+ existing W4/W5 suites) |
| 05 Full A regression | DONE | Full solution Release: 989/989 (Trip unit 299, Trip integration 192 incl. 19 new W12; all other projects green) | `test-output.txt` |
| 06 Invariant auditor | EXECUTED (A-scope) | Read-only `TripInvariantAuditor` over disposable real databases: AUD-03/05/06/07/08 executable (PASS on compensated scenario; seeded violations FAIL), AUD-01/02/04 NOT RUN (B-owned Driver data dependency, honestly recorded) | `final-state/aw12-06-auditor.md`, `TripInvariantAuditor.cs` |
| 07 G4 core release gate | REVIEWED — **G4 NOT YET GREEN** | Evidence-driven matrix with exact blockers (B/C integrations unmerged, peer reruns pending, cross-service auditor legs NOT RUN) | `final-state/g4-matrix.md` |
| 08 Cross-explain to B/C | DONE (handoff published; peer verification PENDING) | Trip README §"W12 observability…": lifecycle, fencing, saga lineage, outbox atomicity, recovery, auditor usage, reproduce commands, 8-point review checklist | `src/Services/Trip/RideHailing.TripService/README.md` |
| 09 Evidence gap repair | DONE | This bundle; two-commit provenance anchored by `scripts/test/verify-source-anchor.sh` | `manifest.json`, `reproduce.sh` |

## Limitations & unresolved dependencies (honest scope)

- **G4 is not green as a TEAM gate.** A-scope work is verified; the matrix in
  `final-state/g4-matrix.md` lists every DEPENDENCY_PENDING entry (B-W10
  driver lifecycle unmerged, C-W10/W11 payment messaging + shared fault
  platform unmerged, B/C independent reruns pending).
- AUD-01/02/04 (and the Payment/Matching halves of AUD-05/06) are NOT RUN:
  they require B/C-owned databases; no cross-service read access is
  configured in A's environment. The Trip-side proxies are labelled as
  proxies and additionally structurally enforced by DB CHECK constraints.
- End-to-end JWT authentication is SYSTEM_AUTH_PENDING: Identity service is
  a C-owned skeleton today. Trip's claims-boundary negative proofs (401/403/
  404-ownership/fail-closed malformed claims) are verified; the authenticated
  integration fixture (`TestPrincipalStartupFilter`) is test-host-only and
  bypasses no production path.
- SEC-07 oversized payloads rely on platform defaults (Kestrel 30 MB) plus
  the existing bounded validations (Idempotency-Key ≤ 128, coordinate
  domain checks) — documented, no fabricated test.
- A-W12-08 peer verification requires actual B/C reruns; A's self-authored
  handoff is NOT claimed as peer-verified.

## How to reproduce

```bash
bash evidence/member-a/w12-core-gate/reproduce.sh
```

Verifies source equivalence to the code commit first (any drift aborts),
then runs: Release build, full solution tests, W12 telemetry/propagation/
authz/auditor lanes, and W08/W09/W10/W11 regressions. Raw output lands in
gitignored `outputs/repro-<stamp>/`; curated results live in
`test-output.txt`.
