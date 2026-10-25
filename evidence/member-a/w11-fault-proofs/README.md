# A-W11 — Deterministic Crash Faults, Durable Saga Restart & Distributed Invariant Proofs

Member A Week 11 local verification (Doc 05A §19, A-W11-01..10). Scope: the
canonical `Saga.AfterPersistBeforeDispatch` faultpoint integrated at the
durable commit→dispatch boundary with a frozen integration contract for C's
pending shared fault platform; the first A-owned proofs that terminate
(SIGKILL) and restart the REAL compiled Trip service process against
disposable PostgreSQL and RabbitMQ (REL-04 producer crash, REL-07 saga
restart with durably backlogged Payment outcome, REL-17 late terminal
events, W11-05 compensation outage/crash recovery with exact identities);
the timing-dependency audit of A-owned tests; a five-pass stability
campaign; and the cross-service invariant-auditor review with handoffs.

## Provenance model

Identical to W09/W10: every result below was produced from exactly one code
revision; `reproduce.sh` delegates to the shared
`scripts/test/verify-source-anchor.sh`, which proves the checkout's source
(every tracked path outside `evidence/` — added, modified, deleted,
renamed and mode-changed paths are all drift) is identical to that revision
before running anything, and refuses otherwise.

- Branch: `feature/A-W11-trip-fault-proofs`, stacked on the verified
  `feature/A-W10-trip-saga` head `532adb1` (PR #40, OPEN/unmerged — W11
  targets that stacked base; `ci.yml` lists it so CI runs on the PR).
- Verified code commit: `6047a7a` (tree `d7a7079`).
- Final-state proof records live in `final-state/` (REL-04, REL-07, REL-17,
  A-W11-05, A-W11-01, auditor review), cross-referenced to the test names
  proven in `test-output.txt` §3/§4.

## Environment

| Item | Value |
|---|---|
| OS | Linux 7.3.0-1-default |
| .NET SDK | 10.0.401 |
| Configuration | Release |
| Docker | 29.7.2-ce |
| Test database | postgres:18.6 (Testcontainers, disposable probe DB per scenario) |
| Test broker | rabbitmq:4.3.6-management (Testcontainers, per-class fixture) |
| Producer processes | the real compiled Trip service apphost, launched as harness-owned child processes; crash mechanism = SIGKILL (`Process.Kill(entireProcessTree)`), exit verified |

## Results

| Lane | Result |
|---|---|
| solution-build-release | PASS (exit 0, 0 errors, 1 pre-existing warning: xUnit2029 in B-owned `DriverReserveConcurrencyTests.cs`, not touched here) |
| solution-tests-release | PASS **970 passed, 0 failed, 1 skipped** (pre-existing Identity opt-in skip; SystemTests project has no discoverable tests — not counted as a skip). Includes the 16 new W11 tests (11 faultpoint + 5 process-level). |
| w11-faultpoint-tests | PASS 11/11 (settings/gating/marker/release/deadline/wiring; production refusal) |
| w11-process-tests ×5 (A-W11-07) | PASS **5/5 consecutive controlled passes, 25/25 scenario runs, 0 flakes** — each pass on fresh probe DBs/identities/signal directories, each green pass only reachable through its asserted fault window |
| w09-outbox-regression | PASS 17/17 |
| w10-saga-regression | PASS 14/14 |
| w08-messaging-regression | PASS 18/18 |

## Scenario claims (records in `final-state/`)

- **REL-04** `rel-04-producer-crash.md` — SIGKILL at the faultpoint after
  the DRIVER_ACCEPTED+saga+outbox commit, before AuthorizePaymentV1
  publication; pre-crash authoritative state asserted (business committed,
  outbox PENDING with original messageId, queue empty); restart reclaims the
  orphaned claim and publishes the SAME messageId with intact lineage; no
  second logical transition.
- **REL-07** `rel-07-saga-restart.md` — kill after durable dispatch; Payment
  success backlogs on the quorum queue while the service is down (consumer
  count 0 / ready 1 asserted); restarted process continues the SAME saga
  (identity, operationKey, StepVersion+1 exactly) to terminal
  COMPLETED/CAPTURED; stale/duplicate late outcomes cannot regress it.
- **REL-17** `rel-17-late-terminal-events.md` — late DriverReserved on
  CANCELLED → exact cleanup only; logically duplicate delivery → durable
  BusinessReplay; COMPLETED terminal monotonicity under stale/duplicate
  payment outcomes. Case selection documented against existing coverage (no
  redundant combinatorial suite).
- **A-W11-05** `aw11-05-compensation-recovery.md` — authorized-after-cancel
  resolves through the exact void key (`{tripId}:void:v1`) and exact release
  identity to COMPENSATED only when both legs resolved; crash with the
  release obligation outstanding resolves via the backlogged confirmation
  after restart; no void obligation on the DECLINED path.
- **A-W11-01** `aw11-01-faultpoint.md` — boundary correctness (relay
  pre-publish seam is necessarily after-commit), double-gated activation,
  observable marker, bounded wait, frozen C contract.
- **A-W11-10 + A-W11-06** `auditor-review.md` — cross-service property
  review (local vs B/C-owner split, AUD-01..08 mapping), compact handoffs,
  and the timing audit record.

## Peer rerun (A-W11-08) — PEER_RERUN_PENDING

C can independently rerun everything from a plain checkout of the PR head:

```
bash evidence/member-a/w11-fault-proofs/reproduce.sh
```

The script first proves source equivalence to `6047a7a` (tree `d7a7079`)
via the shared verifier, then rebuilds and runs every lane above including
the five-pass process campaign. Requirements: git, Docker, .NET SDK 10
(`dotnet` on PATH or `$HOME/.dotnet-sdk/dotnet`). The process lanes need
Docker only for the disposable PostgreSQL/RabbitMQ containers; the harness
SIGKILLs only Trip apphost processes it launched itself. Docker credentials,
volumes and unrelated containers are never touched; no prune is run.

Peer checklist for C (from the mission): faultpoint integration and
environment gating; producer crash + outbox recovery; consumer receipt/ACK
boundaries; saga restart with Payment outcome backlog; message/operation
identity through restart; authoritative final-state evidence; no test-only
controls on production routes. **No C rerun has been executed or recorded —
`PEER_RERUN_PENDING`.**

## Claim boundaries and limitations

- No exactly-once-delivery claim; transport delivery to the real queue plus
  Trip-authoritative final state is the proof boundary.
- Payment outcomes are harness-published with the saga's exact lineage
  (C-W10 Payment producer pending) — an A-side real-consumer restart proof,
  not a three-service Payment execution claim; no provider effect is
  fabricated. Driver authority facts are likewise harness-published
  (B-W10 PR #39 OPEN); Driver-side state effects are not claimed.
- Scenarios needing DRIVER_ACCEPTED (no transport exists for driver
  acceptance yet) seed the committed artifacts through the real handler
  chain — the identical rows the service commits — and every scenario
  re-asserts that committed state through the real process's own relay and
  consumer before the crash.
- C's shared fault platform is undelivered; A's faultpoint is the narrow
  A-owned integration with the contract frozen for C's provider. A did not
  fork or duplicate a shared framework.
