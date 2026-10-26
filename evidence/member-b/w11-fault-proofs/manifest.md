# B-W11 Phase 1 + Phase 2 evidence manifest

## 1. Source binding

| Item | Value |
|---|---|
| Repository | `minhqng/ride-hailing-microservices-system` |
| Branch | `feature/B-W11-deterministic-faults` |
| PR | #44 |
| Integration base | `origin/main` `86062af028d01b86f07b1c902f040a41c41fe57f` |
| Phase 1 source | `608426e5dc85e02d82e24a66f01c5efdb0cce5fa` |
| Final tested source | `c3907a93404d9016a2afefd5b25434f0bacfb49f` |
| Final source tree | `7aad55bb54b574f0edfaf4efaecf3dcaf2caa153` |
| Final source status at run | clean (`""` in `five-pass-summary.json`) |
| Authoritative five-pass run | `five-pass-20261026t174928z` |

Phase 2 commits: `cdd0474` (campaign), `bcbd2c5` (broker/acceptance
hardening), `427c043` (ordered stale phases), `c3907a9` (summary quoting).
The final source differs from `427c043` only in the campaign runner; the test
code is identical.

## 2. Environment

| Component | Version / identity |
|---|---|
| Host | Windows 10.0.26200 x64, Git Bash |
| Docker Engine | `29.8.2` |
| Host .NET SDK / runtime | `10.0.403` / `10.0.14` |
| Linux SDK container | `mcr.microsoft.com/dotnet/sdk:10.0` (Matching unit suite) |
| Driver image | `sha256:684777d4ce74b4fdd978ddd29e450b4416e8608fa4f8dff9f32437f50c13eaf2` |
| Matching image | `sha256:0a704ee0ba13c684ea07c9fa52247a7c73927882e985551d93cee252572eb874` |
| PostgreSQL | `postgres:18.6` |
| RabbitMQ | `rabbitmq:4.3.6-management` |
| Redis | `redis:8.10.2` |

## 3. Exact commands

### 3.1 Acceptance

```bash
cd D:/rh-w11b
./scripts/fault/b-w11-full-campaign.sh
```

The runner builds the solution Release, builds the service images on pass 1,
clears explicit shared-test env overrides, and runs five passes. Each pass
uses fresh GUID identities and a fresh container namespace. A failed or
zero-test scenario stops the pass and preserves its logs. Multi-pass
acceptance rejects a dirty source; `ALLOW_DIRTY` is diagnostic-only for a
single-pass run and the summary is marked `DIAGNOSTIC`.

### 3.2 Full regression

```bash
export PATH="$HOME/.dotnet:$PATH"
export TESTCONTAINERS_RYUK_DISABLED=true
unset MATCHING_TEST_POSTGRES DRIVER_TEST_POSTGRES RABBITMQ_TEST_CONNECTION \
  REDIS_TEST_CONNECTION Redis__ConnectionString TRIP_TEST_POSTGRES
dotnet build RideHailing.Distributed.slnx -c Release

dotnet test tests/Unit/RideHailing.DriverService.UnitTests/... -c Release --no-build
dotnet test tests/Unit/RideHailing.MatchingService.UnitTests/... -c Release   # Linux SDK container in this run
dotnet test tests/Integration/RideHailing.DriverService.IntegrationTests/... -c Release
dotnet test tests/Integration/RideHailing.MatchingService.IntegrationTests/... -c Release
dotnet test tests/Integration/RideHailing.Platform.IntegrationTests/... -c Release
dotnet test tests/Integration/RideHailing.TripService.IntegrationTests/... -c Release \
  --filter "FullyQualifiedName~TripDriverAgreementTests|FullyQualifiedName~TripW06AssignmentProofsTests"
```

The host's Application Control policy blocked loading the Release Matching
unit DLL (`0x800711C7`); that suite was run in the cached Linux SDK container
and passed 104/104. This is an environment load block, not a test failure.

### 3.3 Focused Phase 2

```bash
dotnet test tests/Integration/RideHailing.DriverService.IntegrationTests/... -c Release \
  --filter "FullyQualifiedName~DriverCon02DeterministicTests|FullyQualifiedName~Con04_LateReleaseR1_ViaBroker|FullyQualifiedName~Stale_Release_After_ReReserve_Cannot_Free_Newer_Owner"
dotnet test tests/Integration/RideHailing.MatchingService.IntegrationTests/... -c Release \
  --filter "FullyQualifiedName~DriverProjectionStalenessHandlerTests|FullyQualifiedName~W11_Rel12_|FullyQualifiedName~W11_Rel13_|FullyQualifiedName~RedisOutageRecoveryTests"
```

## 4. Controlled boundaries

| Scenario | Barrier / fault | Oracle |
|---|---|---|
| REL-01 | `Driver.AfterReserveCommitBeforeResponse` after Driver row + Outbox commit | Matching attempt UNKNOWN/`RESERVE_TRANSPORT`, same-R reconcile, Driver DB/Outbox |
| REL-02 | same barrier then real container `SIGKILL` | exit 137, new container/PID, persisted `IN_FLIGHT`, same-R reconcile |
| CON-02 | `Barrier(2)` + controlled release gate; same starting version | final Driver row, version +1, per-driver Outbox counts |
| CON-04 direct | exact-token late R1 after R2 | final R2/trip2/version, R1/R2 release intent counts |
| CON-04 broker | real RabbitMQ command queue + `ManualAckConsumer` ACK/receipts | broker-written Rejected receipt before replay; R2 unchanged |
| REL-12/13 handler | real `DriverProjectionHandler` + Redis FCALL | projection hash/GEO and stale outcomes |
| REL-12/13 broker | authoritative and stale publish phases separated by receipt fingerprints | every message receipt exists; final authoritative state |
| REL-14 | test TCP gate closes every Redis connection | production candidate Degraded + no attempts; reconnect, survival/replay |

## 5. Five-pass identities and process evidence

| Pass | REL-02 killed PID | restarted PID | exit | pre-crash | final |
|---|---|---|---|---|---|
| 1 | 29238 | 30425 | 137 | IN_FLIGHT | RESERVED |
| 2 | 37451 | 38514 | 137 | IN_FLIGHT | RESERVED |
| 3 | 45464 | 46583 | 137 | IN_FLIGHT | RESERVED |
| 4 | 53720 | 54755 | 137 | IN_FLIGHT | RESERVED |
| 5 | 61644 | 62837 | 137 | IN_FLIGHT | RESERVED |

Every pass has `rel02-before-crash-fault.json` with `pendingCount=1`,
`rel02-process-after-kill.txt` with `exit_code=137`, and a final `RESERVED`
attempt with one Outbox intent. Pass 5 representative IDs:

- REL-01: trip `15a06d15-00c6-4313-924e-299337d0dfa2`, reservation
  `dfe628cd-c752-4eee-8ca4-4b1e552ed4fb`.
- REL-02: trip `b5bbd225-4a5c-4037-bf8f-f19bd670ab67`, reservation
  `c5db6b84-a209-400c-bfca-2d8ad3046744`.
- CON-02 offline-wins: final version 3, one Offline intent, zero Reserved.
- CON-02 reserve-wins: final version 3, one Reserved intent, zero Offline.
- CON-04 direct: late `Conflict/STALE_RESERVATION`, final version 5, R1
  release intent 1, R2 release intent 0.
- CON-04 broker: late `Rejected/STALE_RESERVATION` receipt, original replay
  `Applied`, R2/trip2 unchanged.
- REL-12: v10 RESERVED retained, GEO absent, stale outcome `Stale`.
- REL-13: seq 20 retained, foreign session rejected, same-seq newer applied,
  older stale, duplicate converged, GEO lat 10.85.
- REL-14: degraded true, candidates 0, attempts 0, Driver unchanged,
  `survived=True`, `eventReplayReconstructed=True`.

## 6. Test matrix (final source)

| Suite | Result | Log |
|---|---|---|
| Driver unit | 129 / 129 PASS | `test-output/driver-unit.txt` |
| Matching unit | 104 / 104 PASS | `test-output/matching-unit.txt` |
| Driver integration | 110 / 110 PASS | `test-output/driver-integration.txt` |
| Matching integration | 144 / 144 PASS | `test-output/matching-integration.txt` |
| Platform integration | 60 / 60 PASS | `test-output/platform-integration.txt` |
| Trip identity agreement | 10 / 10 PASS | `test-output/trip-agreement.txt` |
| **Total** | **557 / 557 PASS** | |

Included: 6 `DriverFaultPointTests`, the REL-01 HTTP test,
30 reserve/reconcile/atomic-fence/recovery tests, both CON-02 schedules,
CON-04 direct + broker, REL-12/13 handler + broker, REL-14, all W9
Outbox/terminalization suites, all Driver lifecycle/OCC tests, and the shared
platform Outbox/dedup/relay suite.

## 7. Sleep audit

No `Thread.Sleep`; no fixed sleep used as a correctness oracle. Remaining
`Task.Delay` uses are bounded condition/health/message polls or deliberately
blocked crash-before-ACK fixtures ended by cancellation. Shell sleeps occur
only inside bounded `wait_for`/container-stop loops. The Phase 2 proofs use
barriers, receipts, ACK counts, process exit codes and durable rows. Details:
`sleep-audit.md`.

## 8. Retry history

| Attempt | Outcome | Explanation / fix |
|---|---|---|
| Phase 1 `20261026t160832z` | FAIL harness | Outbox `aggregate_id` filter used hyphenated GUID instead of dense `"N"`; product state was correct. Fixed filter. |
| Phase 1 `20261026t161013z` | PASS with noisy log | Expected kill-time curl failure logged via inherited `ERR` trap; client subshell now captures exit 52 as evidence. |
| Phase 1 `20261026t162510z` | PASS | Cached-image interim run. |
| Phase 1 `20261026t163456z` | PASS | Authoritative Phase 1 run on `608426e`, fresh images. |
| Shared explicit-DB full Matching suite | 55/139 | Concurrent fixtures raced migrations against one database. Rerun with isolated Testcontainers: 139/139, then 144/144 with Phase 2. |
| First REL-01 HTTP test | FAIL | Test-host endpoint inference lacked location services; registered real dependencies; green. |
| Five-pass smoke `170258z` | PASS 1/1 diagnostic | Dirty source at `b4eae70`; explicitly non-acceptance. |
| Five-pass `171053z`, `172653z` | Interrupted | Stopped early for review fixes; artifacts retained; not acceptance. |
| Five-pass `173837z` | 5/5 scenarios PASS, summary writer failed | A single quote in the Node diagnostic template broke shell quoting. Runner fixed in `c3907a9`; acceptance restarted. |
| Five-pass `174928z` | **5/5 acceptance PASS** | Clean source `c3907a9`, sourceStatus empty, summary generated. |
| Matching unit Release on host | Load block | Application Control `0x800711C7` blocked the DLL; Linux SDK container ran 104/104. Not a test failure. |

No failed repetition was excluded from this history. No assertion was muted,
skipped or weakened to obtain a green run.

## 9. CI status

- `main` run `40343480340` at `86062af`: failure, 4 jobs, **0 steps**, no
  runner — account-billing block before execution.
- Prior PR #44 head `b4eae70` run `40362137435`: failure, 4 jobs, **0 steps**.
- Final-head PR run is reported in the final handoff/PR comment. No green CI
  is claimed. This is `CI INFRASTRUCTURE BLOCKED`, not a B test failure.

## 10. Peer rerun

**PEER PENDING.** No A/C human peer has executed and acknowledged this
campaign. An independent AI review is not a substitute for that gate.

## 11. Artifact integrity

`hashes.txt` lists SHA-256 of committed source files, five-pass summary and
per-pass evidence, Phase 1 evidence, test logs and docs. Raw container logs
remain under `raw/` and are gitignored; the authoritative run directory is
`raw/five-pass-20261026t174928z`.
