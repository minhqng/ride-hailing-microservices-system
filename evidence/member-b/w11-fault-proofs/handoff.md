# B-W11 Phase 1 + Phase 2 handoff

## Current state

Branch `feature/B-W11-deterministic-faults`, PR #44, on top of `origin/main`
`86062af`.

- Phase 1 source: `608426e` (fault seam, REL-01/REL-02).
- Phase 2 source: `c3907a9` (CON-02, CON-04, REL-12/13/14, five-pass runner).
- Final tested source: `c3907a9`, tree `7aad55b`; five-pass source status
  clean.
- Local acceptance: **5/5 consecutive passes for REL-01, REL-02, CON-02,
  CON-04, REL-12, REL-13, REL-14**.
- Full regression: **557/557 PASS** (Matching unit ran in the Linux SDK
  container because Windows Application Control blocked the Release DLL).
- Peer rerun: **PEER PENDING**.
- GitHub CI: **CI INFRASTRUCTURE BLOCKED** (billing, 0-step jobs); no green
  CI claim.

## B-W11 status

| Task | Status |
|---|---|
| B-W11-01 Driver faultpoint | EXTERNAL INTEGRATION PENDING (B seam works; C PR #41 not on main) |
| B-W11-02 REL-01 | COMPLETE |
| B-W11-03 REL-02 | COMPLETE (harness-triggered recovery) |
| B-W11-04 CON-02 | COMPLETE |
| B-W11-05 CON-04 | COMPLETE |
| B-W11-06 REL-12 | COMPLETE |
| B-W11-07 REL-13 | COMPLETE |
| B-W11-08 REL-14 | COMPLETE (network partition; survival + event replay) |
| B-W11-09 sleeps | COMPLETE |
| B-W11-10 five passes | COMPLETE (5/5 all scenarios) |
| B-W11-11 evidence/peer | EVIDENCE COMPLETE — PEER PENDING |

## Peer rerun instructions

From a clean checkout of the PR head with Docker, `curl`, Node.js and
`dotnet` available:

```bash
./scripts/fault/b-w11-full-campaign.sh
```

Expected:

- `five-pass-summary.json` has `"acceptance": "5/5 consecutive passes"`,
  `"sourceStatus": ""`, and every matrix entry has five `PASS` rows.
- Every pass has REL-02 process evidence with exit 137, a new PID, and final
  `RESERVED` with the same reservation.
- Every pass has `receipts=4/4 ordered=true` for REL-12/13 broker deliveries
  and `degraded=true ... survived=True eventReplayReconstructed=True` for
  REL-14.
- Full suite commands are in `manifest.md`; expected total 557.

Peer status must be reported as `PEER VERIFIED` only after an A/C human
executes and acknowledges it; otherwise `PEER PENDING`.

## External gates / non-claims

| Gate | Owner | Status |
|---|---|---|
| C shared fault platform for B-W11-01 | C (PR #41) | Open against `MemC-W9-Distributed`; not on `main`. Keep B seam; do not count B-W11-01 complete. |
| Unattended Matching recovery | B / architecture | Not present. REL-02 recovery is explicitly harness-triggered. |
| Automatic projection rebuild | B / architecture | Not present. REL-14 proves degradation/no fabrication, reconnect, data survival, and event-driven reconstruction; no automatic full rebuild. |
| Trip leg in container oracle | B / A | Not in this path; Trip identity agreement is 10/10 separately. |
| Peer rerun | A/C + B | PEER PENDING. AI review is not a substitute. |
| GitHub CI | leader | Billing-blocked before job steps. Local gates stand alone; no green claim. |
| W12/W13 | B | Not started. |

## Review focus

1. Confirm the Driver fault seam is Production-inert and the hit is after the
   Driver row + Outbox commit.
2. Confirm CON-04's broker-written `Rejected/STALE_RESERVATION` receipt exists
   before direct replay and R2/trip2 never change.
3. Confirm REL-12/13 broker phases are ordered by receipt fingerprints and
   every published envelope is accounted for.
4. Confirm REL-14 is a real network partition of a disposable Redis, with
   authority unchanged and survival/replay claims separated.
5. Confirm the five-pass summary is source-bound, clean-status, and cannot
   pass a dirty multi-pass run.
6. Treat the C platform, peer rerun and CI gates as open.

## Environment

Docker 29.8.2; PostgreSQL 18.6; RabbitMQ 4.3.6-management; Redis 8.10.2;
host .NET SDK 10.0.403/runtime 10.0.14; Linux SDK container for the Matching
unit suite. Raw run directories are under `raw/` and gitignored; curated
evidence, per-pass logs and hashes are committed.
