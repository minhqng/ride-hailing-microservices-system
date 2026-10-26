# B-W11 Phase 1 handoff

## Current state

Branch `feature/B-W11-deterministic-faults` contains a verified Phase 1 delta
on top of `origin/main` `86062af`:

- B-owned `Driver.AfterReserveCommitBeforeResponse` seam, Testing-only and
  control-token gated (C's shared platform is not on `main`).
- REL-01 automated proof across a real HTTP boundary and reloaded databases.
- REL-02 container-level real `SIGKILL` + new-process reconcile proof.
- Full local regression matrix: **549/549 PASS**.

Verified source: `608426e5dc85e02d82e24a66f01c5efdb0cce5fa`.

## What a peer should rerun

```bash
# From a clean checkout of the branch, with Docker running:
./scripts/fault/b-w11-rel01-rel02.sh
```

Expected hard signals:

- REL-01: one trip/reservation, attempt `IN_FLIGHT` → `UNKNOWN`
  (`RESERVE_TRANSPORT`) → `RESERVED`; next driver never attempted; exactly one
  `DriverReservedV1`.
- REL-02: original Matching container stops with exit 137; a NEW container/PID
  starts on the same PostgreSQL/RabbitMQ; final attempt/round `RESERVED` with
  the same reservation; no duplicate Outbox row.

Expected campaigns end with:

```text
PASS: REL-01 and REL-02
campaign exiting rc=0
```

For the full suite, use the commands in `manifest.md` §3.2. The final run
counts are 129 + 104 + 139 + 107 + 60 + 10 = 549.

## Non-claims / external gates

| Gate | Owner | Status / action |
|---|---|---|
| C shared fault platform for B-W11-01 | C (PR #41) | **Open, not on `main`.** C's Driver hook predates W9's transactional Outbox and its fault class lacks B's Testing/token gate. B seam keeps the frozen name and `HitAsync` shape; wire the adapter when the shared contract lands. Do not mark B-W11-01 fully integrated before that. |
| Unattended Matching recovery | B / architecture | Not present. The REL-02 restart uses the test-only reconcile trigger; the proof is real process kill + DB reload + explicit recovery, not automatic recovery. |
| Trip leg of the REL oracle | B / A | Not in the container proof because Matching's production intake does not execute reserve in this branch. Trip identity agreement is separately green (10/10). |
| GitHub Actions | leader | Latest `main` runs are billing-blocked before any job step (run `40343480340`, four jobs, 0 steps). Branch CI result is reported in the PR/final handoff; no green claim. |
| W11-04…W11-08 | B | CON-02, CON-04, REL-12, REL-13, REL-14 Redis/fault campaigns not started. |
| W11-09 | B | Arbitrary-sleep removal is only partially addressed in this delta (the new proof uses barriers/bounded polls). Existing suites still need a sweep. |
| W11-10 | B | Five consecutive key-case passes not run. This bundle has one authoritative run plus interim runs. |
| W11-11 | B + peer | Evidence bundle created; peer rerun not yet performed. |

## Review focus

1. Verify the fault seam is inert in Production and cannot be activated over
   HTTP; `DriverFaultPoint.FromEnvironment` requires Testing **and** a control
   token.
2. Verify the hit is after `TryReserveAsync` (row + Outbox commit) and before
   the response result; see `DriverEndpoints.cs:241`.
3. Verify the REL-02 evidence is a real container exit, not a simulated
   exception. `final-state/rel02-process-*.txt` and `campaign.txt` show the
   container/PID transition.
4. Confirm no test was weakened to obtain green: the final campaign rebuilt
   both images from the verified source and `run-summary.json` records
   `sourceStatus=""`.
5. Treat the Trip leg and automatic recovery as explicit gaps, not implied
   passes.

## Reproduction environment

Docker 29.8.2; host .NET SDK 10.0.403 (installed under `~/.dotnet` for this
run); PostgreSQL 18.6, RabbitMQ 4.3.6-management, Redis 8.10.2. The container
images use the repo's Dockerfiles and the cached `mcr.microsoft.com/dotnet`
10.0 images. Raw logs remain under `raw/` and are gitignored.
