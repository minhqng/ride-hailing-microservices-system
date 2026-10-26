# Member B W11 Phase 1 — deterministic REL-01 / REL-02 fault proofs

Status: **CODE VERIFIED — EXTERNAL GATES REMAIN**

Branch: `feature/B-W11-deterministic-faults`
Base: `origin/main` `86062af028d01b86f07b1c902f040a41c41fe57f`
Verified source: `608426e5dc85e02d82e24a66f01c5efdb0cce5fa`
Source tree: `88f48fb4b70cabafe758258770f4057d209362e7`

This bundle contains a reproducible Driver response-loss proof (REL-01) and a
real Matching process-kill/restart proof (REL-02). Both use real PostgreSQL,
RabbitMQ and HTTP transport in the final campaign.

## What landed

| Item | Result |
|---|---|
| B-W11-01 B-owned seam | `Driver.AfterReserveCommitBeforeResponse`, Testing-only, control-token gated, bounded hold, post-commit and post-Outbox |
| B-W11-02 REL-01 | PASS across a real HTTP boundary and reloaded databases |
| B-W11-03 REL-02 | PASS with `SIGKILL`, confirmed exit 137, new Matching PID, same DB/broker, same reservation identity |
| C shared fault platform | **Not wired.** PR #41 is not on `main`; its Driver hook predates the W9 transactional Outbox. B-W11-01 is therefore not claimed as fully integrated. |
| W11-04…W11-11 | Not started in this phase |

## REL-01 — lost Reserve response after Driver commit

Controlled sequence actually observed:

```text
Matching attempt IN_FLIGHT + dispatch fence (Matching DB)
→ real POST /drivers/{id}/reserve
→ Driver RESERVED + DriverReservedV1 Outbox committed
→ Driver.AfterReserveCommitBeforeResponse holds the response
→ Matching 3s HTTP timeout: UNKNOWN / RESERVE_TRANSPORT, no candidate advance
→ release fault
→ reconcile the SAME reservationId
→ Matching attempt + round RESERVED
```

Final-run identities:

| Field | Value |
|---|---|
| tripId | `8c4dbedc-0b3a-4b2a-8e1c-efc3b5464e36` |
| driverId | `3d40a9e7-e744-4dbf-9b56-10036395b051` |
| next driverId | `b69f4f7c-ec84-499a-a744-4579e7547333` (never attempted) |
| reservationId | `e182771d-b2d7-4440-862a-d13999807dd6` (unchanged end to end) |
| Driver version | 3 at commit and after reconcile replay |
| Driver Outbox | exactly one `DriverReservedV1`, published once |
| Matching round | `PROCESSING` while UNKNOWN → `RESERVED`, no result identity |
| `MatchingFailedV1` | none |

Key evidence:

- `final-state/rel01-before-release-attempts.json` — `IN_FLIGHT`,
  `max_dispatched_version=2`.
- `final-state/rel01-before-release-fault.json` — barrier reached,
  `pendingCount=1`.
- `final-state/rel01-unknown-attempts.json` — `UNKNOWN` /
  `RESERVE_TRANSPORT`, same reservation.
- `final-state/rel01-final-attempts.json` and
  `rel01-final-attempts-round.json` — `RESERVED` with the same identity.
- `final-state/rel01-final-attempts-driver-outbox.json` — one published
  `DriverReservedV1` with the same reservation and round 1.
- `final-state/rel01-final-drivers.json` — Driver `RESERVED`, next Driver
  `AVAILABLE` at its pre-test version.

The automated test `LostReserveResponseHttpTests` reproduces the same sequence
inside the test suite using a real Kestrel Driver host and the production
`HttpDriverClient`/orchestrator/repository. It passed in the final
`MatchingService.IntegrationTests` run.

## REL-02 — real Matching process crash after Driver commit

Controlled sequence actually observed:

```text
Matching attempt IN_FLIGHT + fence; Driver RESERVED + one Outbox intent
→ barrier reached with pendingCount=1
→ SIGKILL Matching container
→ original PID 55560 exited 137, container not running
→ reserve HTTP client failed (000 / curl 52)
→ Driver and Matching DB still hold the same committed R / IN_FLIGHT
→ release fault
→ NEW Matching container + PID 56637 on the same PostgreSQL and RabbitMQ
→ reconcile persisted IN_FLIGHT identity
→ attempt + round RESERVED with the same R
```

| Field | Value |
|---|---|
| tripId | `48bdeb40-c3a8-48aa-9bad-7877ac1631b0` |
| driverId | `c8312f61-823c-4527-9ce4-5de74343b454` |
| next driverId | `222f8ed1-1821-48a3-b37d-6805116b32dc` (never attempted) |
| reservationId | `58d5cac0-7a0a-4660-bfa8-165dda1876cc` |
| killed container | `ca896139a1313c0f8c454e78a0da321dcb41d71caa212749b5d3a2e88de6d2dd` |
| killed PID / exit | `55560` / `137` |
| restarted container | `879021393d4fd52dbcd3756d83fb16ffb18bb9df6da27f14a65bd3f04f0214c0` |
| restarted PID | `56637` |
| Driver Outbox | exactly one `DriverReservedV1`, published once |

`final-state/rel02-before-crash-fault.json` proves a new fault hit
(`reachedCount=2`, `pendingCount=1`) rather than reusing REL-01's event.
`final-state/rel02-process-*.txt` records the real container/PID transition.
The final attempt row is `RESERVED` with the same reservation and ordinal 0.
`max_dispatched_version` moves 2 → 3 because the restarted process re-reads the
current Driver version before its same-R replay; the Driver row/version and
Outbox do not change.

## Fault-seam safety

- Enabled only under `ASPNETCORE_ENVIRONMENT=Testing`; every other environment
  resolves a disabled no-op even if `RIDE_FAULTPOINTS` is set.
- Requires `RIDE_FAULT_CONTROL_TOKEN`; without it the seam disables itself
  (fail closed). Control endpoints exist only in Testing and compare the token
  with SHA-256 + fixed-time equality.
- No HTTP endpoint can activate a fault. Activation is process environment at
  startup.
- The hit is after `TryReserveAsync` returns (row + Outbox committed) and
  before the HTTP result is built. No exception is injected before commit.
- Every hold is bounded (default 30s, clamp 1–120s). The harness sets 90s and
  releases explicitly.
- Request cancellation is deliberately not linked to the hold: a timed-out or
  killed caller must not erase the committed effect before the proof observes
  it. The bounded timeout is the fail-safe.

## Test matrix (final source `608426e`)

| Suite | Result |
|---|---|
| Driver unit (`RideHailing.DriverService.UnitTests`) | 129 / 129 PASS |
| Matching unit (`RideHailing.MatchingService.UnitTests`) | 104 / 104 PASS |
| Matching integration (real PostgreSQL/RabbitMQ/Redis via Testcontainers) | 139 / 139 PASS |
| Driver integration (PostgreSQL + RabbitMQ) | 107 / 107 PASS |
| Platform integration (shared Outbox/dedup/relay) | 60 / 60 PASS |
| Trip identity agreement (`TripDriverAgreementTests`, `TripW06AssignmentProofsTests`) | 10 / 10 PASS |
| **Total** | **549 / 549 PASS, 0 failed** |

The final campaign itself (`campaign.txt`) passed REL-01 and REL-02. No
assertion was weakened to obtain green runs.

## Limitations / non-claims

1. **B-W11-01 is not fully integrated with C's shared platform.** The B seam is
   an adapter-shaped placeholder with the frozen fault name and `HitAsync`
   contract. C PR #41 is open, not on `main`, and its Driver hook is stale
   relative to W9's transactional Outbox. Do not count B-W11-01 as complete
   until the shared contract is wired.
2. **REL-02 recovery is harness-triggered.** The service has no unattended
   startup/redelivery reconcile. The proof is a real process kill, real DB
   reload and explicit `ReconcileAsync` through the real Matching process; it
   is not an automatic-recovery claim.
3. **No Trip leg in the container oracle.** Matching's production intake does
   not execute reserve in this branch, so the container proof asserts Driver +
   Matching only. Trip identity agreement is covered separately by the 10
   passing Trip tests above.
4. **No W11-04…W11-08 Redis/CON campaign, no W11-10 five-pass stability, no
   W11-11 peer rerun.** Those remain B W11 work.
5. **No GitHub Actions green claim.** The latest `main` runs failed before any
   job step started (run `40343480340`, four jobs, 0 steps, billing-blocked).
   Local evidence above stands on its own; branch CI is reported separately.
6. The final-state JSON `rel02-final-attempts-driver-outbox.json` includes the
   `DriverAvailableV1` rows from the two seeded drivers; the assertion filters
   `DriverReservedV1` and requires exactly one.

## Reproduce

```bash
# Build and run the full container campaign (fresh images from HEAD)
./scripts/fault/b-w11-rel01-rel02.sh

# Reuse rhw11b-driver:local / rhw11b-matching:local for a fast rerun
SKIP_BUILD=1 ./scripts/fault/b-w11-rel01-rel02.sh
```

Requirements: bash, Docker, curl, Node.js. Exact suite commands are in
`manifest.md`. Raw container logs are under `raw/` and are gitignored; curated
snapshots and test logs are committed here.
