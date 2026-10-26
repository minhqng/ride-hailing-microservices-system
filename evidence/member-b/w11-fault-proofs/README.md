# Member B W11 — deterministic fault campaign

Status: **B-W11 COMPLETE LOCALLY — CI/PEER GATES REMAIN**

| Item | Value |
|---|---|
| Branch | `feature/B-W11-deterministic-faults` |
| PR | [#44](https://github.com/minhqng/ride-hailing-microservices-system/pull/44) |
| Integration base | `origin/main` `86062af028d01b86f07b1c902f040a41c41fe57f` |
| Phase 1 source | `608426e5dc85e02d82e24a66f01c5efdb0cce5fa` |
| Final tested source | `c3907a93404d9016a2afefd5b25434f0bacfb49f` |
| Final source tree | `7aad55bb54b574f0edfaf4efaecf3dcaf2caa153` |
| Final five-pass run | `five-pass-20261026t174928z` (clean source status) |

Commits: Phase 1 `608426e`; Phase 1 evidence `b4eae70`; Phase 2 campaign
`cdd0474`; review hardening `bcbd2c5`, `427c043`, `c3907a9`.

## B-W11 status

| Task | Status |
|---|---|
| B-W11-01 Driver faultpoint | **EXTERNAL INTEGRATION PENDING** — B-owned seam works; C PR #41 is still open against `MemC-W9-Distributed`, not on `main` |
| B-W11-02 REL-01 | COMPLETE |
| B-W11-03 REL-02 | COMPLETE (harness-triggered recovery) |
| B-W11-04 CON-02 | COMPLETE — both schedules, 5/5 |
| B-W11-05 CON-04 | COMPLETE — direct + real broker, 5/5 |
| B-W11-06 REL-12 | COMPLETE — handler + real broker, 5/5 |
| B-W11-07 REL-13 | COMPLETE — handler + real broker, 5/5 |
| B-W11-08 REL-14 | COMPLETE — network partition, survival + event reconstruction, 5/5 |
| B-W11-09 sleeps | COMPLETE — see `sleep-audit.md` |
| B-W11-10 five passes | COMPLETE — 5/5 consecutive for all seven scenarios |
| B-W11-11 evidence/peer | EVIDENCE COMPLETE — **PEER PENDING** |

## REL-01 / REL-02 (Phase 1, retained)

Phase 1 proved the Driver post-commit response-loss and real Matching
`SIGKILL`/restart paths. The five-pass campaign now repeats both every pass.
Pass 5 representative identities:

| Field | REL-01 | REL-02 |
|---|---|---|
| trip | `15a06d15-00c6-4313-924e-299337d0dfa2` | `b5bbd225-4a5c-4037-bf8f-f19bd670ab67` |
| driver | `b3937ad8-2d36-4d93-b01b-c7fa1f8df4b6` | `02ff1fcb-915f-4c19-854c-eb26813cb4a1` |
| reservation | `dfe628cd-c752-4eee-8ca4-4b1e552ed4fb` | `c5db6b84-a209-400c-bfca-2d8ad3046744` |
| process evidence | timeout while post-commit response held | PID `61644` → `62837`, exit `137` |

Every pass retains the fault-reached snapshot (`pendingCount=1`), pre-crash
`IN_FLIGHT`/Driver `RESERVED`, process exit 137, and final `RESERVED` rows with
one Outbox intent. See `five-pass/pass-N/`.

## CON-02 — GoOffline vs Reserve

Both winning schedules are forced after a common barrier; the loser is
released only after the winner commits from the same starting version.

| Schedule | Final | Version | Outbox |
|---|---|---|---|
| offline-wins | OFFLINE, no reservation | start + 1 | one `DriverOfflineV1`; zero `DriverReservedV1` |
| reserve-wins | RESERVED with R | start + 1 | one `DriverReservedV1`; zero `DriverOfflineV1` |

The losing operation always returns `STALE_VERSION` and does not mutate the
row. Both schedules pass 5/5; representative pass-5 IDs are in
`five-pass/scenarios.tsv`.

## CON-04 — Late release vs newer reservation

Direct proof: `Reserve R1 → Release R1 → Reserve R2 → late Release R1`.
Late R1 is `Conflict/STALE_RESERVATION`; R2 and trip2 remain current at
version 5; `DriverReservationReleasedV1` exists once for R1 and zero times for
R2.

Real-broker proof: `ReleaseDriverReservationV1` for R1 is published after R2
commits. A new envelope identity is executed by the real
`ManualAckConsumer` + `DriverCommandHandler` and produces a durable
`Rejected/STALE_RESERVATION` receipt **before any direct replay**. The exact
committed R1 envelope replays its original `Applied` receipt. R2/trip2,
version and Outbox counts remain unchanged.

## REL-12 / REL-13 — stale projection

Through the real `DriverProjectionHandler` and Redis:

- v10 `RESERVED` then v9 `AVAILABLE`: v10 and `RESERVED` remain; GEO stays
  absent; stale outcome is `Stale`.
- location seq 20 then seq 19: sequence 20, timestamp, coordinates and GEO
  position remain; foreign session is rejected; same-sequence newer timestamp
  applies and older is stale; duplicate delivery converges and its receipt
  fingerprint is retained.

Through real RabbitMQ projection-queue deliveries, authoritative and stale
envelopes are published in separate phases. Each phase completes only when
every envelope has the expected projection receipt; final state remains
authoritative (`receipts=4/4 ordered=true`).

## REL-14 — Redis availability

The test starts a real disposable Redis container and places it behind a
test-controlled TCP gate. Closing the gate drops active client connections:

- production `MatchingRoundService` returns `Degraded=true` with zero
  candidates and zero attempts;
- Driver authority (status, version, reservation) is unchanged;
- no reservation or fabricated availability is created.

After reopening the gate, the shared multiplexer reconnects. The test proves
**data survival** for one driver (`survived=True`) and, separately, deletes a
second driver's projection and reconstructs it by replaying authoritative
Driver state/location events through the real handler
(`eventReplayReconstructed=True`). There is **no automatic full projection
rebuild** in the implementation and none is claimed. The Redis process is not
stopped and no user-owned Redis is touched.

## Five-pass acceptance

`scripts/fault/b-w11-full-campaign.sh` on clean source `c3907a9`:

| Scenario | Pass 1 | Pass 2 | Pass 3 | Pass 4 | Pass 5 |
|---|---|---|---|---|---|
| REL-01 | PASS | PASS | PASS | PASS | PASS |
| REL-02 | PASS | PASS | PASS | PASS | PASS |
| CON-02 | PASS | PASS | PASS | PASS | PASS |
| CON-04 | PASS | PASS | PASS | PASS | PASS |
| REL-12 | PASS | PASS | PASS | PASS | PASS |
| REL-13 | PASS | PASS | PASS | PASS | PASS |
| REL-14 | PASS | PASS | PASS | PASS | PASS |

Fresh GUID identities and a fresh container namespace are used in every
pass. The summary records source SHA/tree, clean source status, image IDs,
per-scenario elapsed time and identity lines. See
`five-pass/five-pass-summary.json`, `five-pass/scenarios.tsv`, and
`five-pass/pass-N/` (including process death evidence).

## Full regression on final source

| Suite | Result |
|---|---|
| Driver unit | 129 / 129 PASS |
| Matching unit | 104 / 104 PASS (Linux SDK container; host Application Control blocked that DLL load) |
| Driver integration | 110 / 110 PASS |
| Matching integration | 144 / 144 PASS |
| Platform integration | 60 / 60 PASS |
| Trip identity agreement | 10 / 10 PASS |
| **Total** | **557 / 557 PASS, 0 failed** |

The same test code was also green on parent `427c043`; the final source change
is the campaign runner's summary-quoting fix. Logs: `test-output/`.

## Independent review

Two fresh-context read-only reviews examined the Phase 2 diff and its Phase 1
interaction. No P0 was found. The P1 finding — dirty-source evidence could
attest a clean SHA — was fixed by rejecting multi-pass dirty runs, recording
source status and marking diagnostics. P2 findings fixed: broker tests now
require a receipt for every delivered message and order authoritative/stale
phases; consumers cancel only after that barrier and are cleaned up in
`finally`; the test logger is concurrent; shared-test env overrides are
cleared; CON-04 asserts the broker-written receipt before replay; REL-14 gate
teardown is bounded/race-hardened. No P0/P1 remains open. This AI review is
not the required human peer rerun.

## Limitations / non-claims

1. **B-W11-01 is not fully integrated with C's shared platform.** C PR #41 is
   open, not on `main`; its Driver hook predates W9's transactional Outbox.
   The B seam keeps the frozen name/`HitAsync` shape and is inert in
   Production. Do not count B-W11-01 complete until the shared contract lands.
2. **REL-02 recovery is harness-triggered.** Real process kill, DB reload and
   new-process reconcile are proven; unattended startup/redelivery recovery
   is not.
3. **REL-14 is a network partition of a live Redis**, not a process stop. It
   proves degrade/no-fabrication/reconnect plus data survival and event-driven
   reconstruction. No automatic full rebuild exists.
4. **No Trip leg in the container oracle.** Matching's production intake does
   not execute reserve in this branch. Trip identity agreement is separately
   green (10/10).
5. **No W12/W13 work; no merge.** Peer rerun is `PEER PENDING`.
6. **No green CI claim.** GitHub Actions still fails before any job step
   (billing). Branch CI is reported separately.

## Reproduce

```bash
# One clean-checkout command: five consecutive passes of all seven scenarios
./scripts/fault/b-w11-full-campaign.sh

# Focused Docker campaign for REL-01/REL-02 only
./scripts/fault/b-w11-rel01-rel02.sh
```

Requirements: bash, Docker, curl, Node.js, dotnet in `PATH`. Full suite
commands are in `manifest.md`.

## Evidence map

- `campaign.txt`, `final-state/` — Phase 1 authoritative REL-01/02 run.
- `five-pass/` — Phase 2 five-pass matrix, per-pass process/DB evidence and logs.
- `test-output/` — final-source full-suite logs.
- `sleep-audit.md` — B-W11-09 findings.
- `manifest.md` — source binding, exact commands, identities, retry history.
- `handoff.md` — peer rerun and external gates.
- `hashes.txt` — SHA-256 of committed evidence and source files.
