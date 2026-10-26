# B-W11 Phase 1 evidence manifest

## 1. Source binding

| Item | Value |
|---|---|
| Repository | `minhqng/ride-hailing-microservices-system` |
| Branch | `feature/B-W11-deterministic-faults` |
| Integration base | `origin/main` `86062af028d01b86f07b1c902f040a41c41fe57f` |
| Verified source | `608426e5dc85e02d82e24a66f01c5efdb0cce5fa` |
| Verified tree | `88f48fb4b70cabafe758258770f4057d209362e7` |
| Working tree at final run | clean (`sourceStatus=""` in `final-state/run-summary.json`) |
| Image builds | fresh `docker build` from the verified source during the final run |

Final campaign directories:

```text
run 20261026t163456z  PASS  (authoritative; fresh images from 608426e)
run 20261026t162510z  PASS  (cached-image rerun, same service source)
run 20261026t161013z  PASS  (interim, expected kill-time curl log)
run 20261026t160832z  FAIL  (harness Outbox filter bug; see §8)
```

## 2. Environment

| Component | Version / identity |
|---|---|
| Host | Windows 10.0.26200 x64, Git Bash |
| Docker Engine | `29.8.2` |
| Host .NET SDK | `10.0.403` |
| Host ASP.NET Core runtime | `10.0.14` |
| Driver image | `rhw11b-driver:local` |
| Driver image ID | `sha256:fd34a7572ccae8569d04d61aca0664ceca71233e85c31619bdb358461e49b998` |
| Matching image | `rhw11b-matching:local` |
| Matching image ID | `sha256:4d7c07fd26c2d4af52acd38ab8f9a5b127654756d59d2b6b28f890631cc464c9` |
| PostgreSQL | `postgres:18.6` |
| RabbitMQ | `rabbitmq:4.3.6-management` |
| Redis | `redis:8.10.2` |

The service images were rebuilt in the authoritative run, not reused from an
earlier worktree state. Build logs are in `raw/20261026t163456z/logs/build-*.log`
(gitignored).

## 3. Exact commands

### 3.1 Authoritative container campaign

```bash
cd D:/rh-w11b
bash scripts/fault/b-w11-rel01-rel02.sh
```

The script creates an isolated network and containers, builds both service
images from the worktree, seeds drivers over HTTP, runs REL-01 and REL-02, and
fails on any assertion. It removes the containers on exit; raw output is under
`raw/<timestamp>/`.

Fast rerun after images exist:

```bash
SKIP_BUILD=1 bash scripts/fault/b-w11-rel01-rel02.sh
```

### 3.2 Final suites

```bash
export PATH="$HOME/.dotnet:$PATH"
export TESTCONTAINERS_RYUK_DISABLED=true

dotnet test tests/Unit/RideHailing.DriverService.UnitTests/RideHailing.DriverService.UnitTests.csproj -c Debug
dotnet test tests/Unit/RideHailing.MatchingService.UnitTests/RideHailing.MatchingService.UnitTests.csproj -c Debug

# Isolated Testcontainers; explicit shared-DB env vars unset
unset MATCHING_TEST_POSTGRES DRIVER_TEST_POSTGRES RABBITMQ_TEST_CONNECTION REDIS_TEST_CONNECTION
dotnet test tests/Integration/RideHailing.MatchingService.IntegrationTests/RideHailing.MatchingService.IntegrationTests.csproj -c Debug

unset DRIVER_TEST_POSTGRES RABBITMQ_TEST_CONNECTION
dotnet test tests/Integration/RideHailing.DriverService.IntegrationTests/RideHailing.DriverService.IntegrationTests.csproj -c Debug

dotnet test tests/Integration/RideHailing.Platform.IntegrationTests/RideHailing.Platform.IntegrationTests.csproj -c Debug

unset TRIP_TEST_POSTGRES
dotnet test tests/Integration/RideHailing.TripService.IntegrationTests/RideHailing.TripService.IntegrationTests.csproj -c Debug \
  --filter "FullyQualifiedName~TripDriverAgreementTests|FullyQualifiedName~TripW06AssignmentProofsTests"
```

### 3.3 Focused fault tests

```bash
dotnet test tests/Unit/RideHailing.DriverService.UnitTests/RideHailing.DriverService.UnitTests.csproj -c Debug \
  --filter "FullyQualifiedName~DriverFaultPointTests"

dotnet test tests/Integration/RideHailing.MatchingService.IntegrationTests/RideHailing.MatchingService.IntegrationTests.csproj -c Debug \
  --filter "FullyQualifiedName~LostReserveResponseHttpTests"
```

## 4. Injected fault boundaries

| Boundary | Location | Property |
|---|---|---|
| `Driver.AfterReserveCommitBeforeResponse` | `DriverEndpoints.cs:241`, after `TryReserveAsync` returns and before `DriverReserveResults.ToHttpResult` | Row and `DriverReservedV1` Outbox are committed; response is held |
| REL-02 kill | Matching container while blocked in the real HTTP reserve call | Matching DB still `IN_FLIGHT`; Driver DB already `RESERVED`; no `RESERVED` attempt persisted locally |

## 5. Process / container identities and state

### REL-01 (container, timeout path)

| Item | Value |
|---|---|
| trip | `8c4dbedc-0b3a-4b2a-8e1c-efc3b5464e36` |
| Driver | `3d40a9e7-e744-4dbf-9b56-10036395b051` |
| reservation | `e182771d-b2d7-4440-862a-d13999807dd6` |
| next Driver | `b69f4f7c-ec84-499a-a744-4579e7547333`, `AVAILABLE`, version 2 throughout |
| before timeout | attempt `IN_FLIGHT`, fence 2; fault `pendingCount=1`; Driver `RESERVED` version 3; one Outbox row |
| timeout outcome | attempt `UNKNOWN`, `RESERVE_TRANSPORT`; round `PROCESSING`; no result identity |
| final | attempt/round `RESERVED`, same R; Driver version 3; exactly one published `DriverReservedV1` |
| HTTP trigger | 200, JSON `outcome=BlockedOnUnknown` |

### REL-02 (actual SIGKILL and restart)

| Item | Value |
|---|---|
| trip | `48bdeb40-c3a8-48aa-9bad-7877ac1631b0` |
| Driver | `c8312f61-823c-4527-9ce4-5de74343b454` |
| reservation | `58d5cac0-7a0a-4660-bfa8-165dda1876cc` |
| next Driver | `222f8ed1-1821-48a3-b37d-6805116b32dc`, `AVAILABLE`, version 2 throughout |
| before crash | attempt `IN_FLIGHT`, fence 2; round `PROCESSING`; fault `reachedCount=2`, `pendingCount=1`; Driver `RESERVED` version 3; one Outbox row |
| killed Matching | container `ca896139a1313c0f8c454e78a0da321dcb41d71caa212749b5d3a2e88de6d2dd`, PID `55560`, exit `137`, `running=false`, `oom=false` |
| client outcome | reserve HTTP `000`, curl exit `52`; killed port health `000` |
| after kill | Matching attempt still `IN_FLIGHT` / fence 2; round `PROCESSING`; Driver untouched |
| restarted Matching | container `879021393d4fd52dbcd3756d83fb16ffb18bb9df6da27f14a65bd3f04f0214c0`, PID `56637`, same DB and broker |
| final | attempt/round `RESERVED`, same R and ordinal 0; Driver version 3; one published `DriverReservedV1`; next Driver untouched |

The restarted process reads the attempt row from PostgreSQL, reuses
`reservationId=58d5cac0…`, and does not mint a new identity. `max_dispatched_version`
moves from 2 to 3 because recovery re-reads the Driver's current version before
its same-R replay; this is a fence refresh, not a new logical attempt.

## 6. Test matrix (source `608426e`)

| Suite | Command result | Log |
|---|---|---|
| Driver unit | **129 passed, 0 failed** | `test-output/driver-unit.txt` |
| Matching unit | **104 passed, 0 failed** | `test-output/matching-unit.txt` |
| Matching integration | **139 passed, 0 failed** | `test-output/matching-integration.txt` |
| Driver integration | **107 passed, 0 failed** | `test-output/driver-integration.txt` |
| Platform integration | **60 passed, 0 failed** | `test-output/platform-integration.txt` |
| Trip identity agreement | **10 passed, 0 failed** | `test-output/trip-agreement.txt` |
| **Total** | **549 passed, 0 failed** | |

Included in these totals: 6 `DriverFaultPointTests`, 1
`LostReserveResponseHttpTests`, 30 reserve/reconcile/atomic-fence/recovery
tests, all W9 Outbox/terminalization suites, all Driver lifecycle/OCC tests,
and all 60 shared-platform Outbox/dedup/relay tests.

## 7. Before / after oracle summary

| Invariant | REL-01 | REL-02 |
|---|---|---|
| One logical reservation R | yes | yes |
| One durable attempt ordinal/identity | yes (ordinal 0) | yes (ordinal 0) |
| No attempt on next Driver while unresolved | yes | yes |
| Driver state/version/Outbox not duplicated | yes (version 3, one intent) | yes (version 3, one intent) |
| No false `NO_DRIVER` / `MatchingFailedV1` | yes | yes |
| No identity-changing retry | yes | yes |
| Transport uncertainty not converted to business rejection | intermediate `UNKNOWN`/`RESERVE_TRANSPORT`, then `RESERVED` | `IN_FLIGHT` persisted, then `RESERVED` |

## 8. Retry and failure history

| Run / attempt | Result | Explanation / fix |
|---|---|---|
| `20261026t160832z` campaign | FAIL at REL-01 Outbox count | Harness filtered `outbox_messages.aggregate_id` by hyphenated GUID; production stores dense `"N"` GUID. Driver/Outbox themselves were correct (`RESERVED`, one row). Fixed the harness filter; no source change. |
| `20261026t161013z` campaign | PASS | Expected curl error on SIGKILL was logged as a campaign ERROR by the inherited `ERR` trap. Fixed the client subshell to capture exit 52 as evidence instead. |
| `20261026t162510z` campaign | PASS | Cached-image rerun; used only for interim validation. |
| `20261026t163456z` campaign | PASS | **Authoritative.** Fresh images from `608426e`, clean source status, image IDs recorded. |
| First full Matching integration run (shared explicit DB) | 55 pass / 84 fail | Several collection fixtures initialized concurrently against one database, racing DDL (`pg_type_typname_nsp_index`). This is a fixture-isolation limitation of the shared-env harness, not a source failure. Reran on the host with isolated Testcontainers: 139/139 pass. |
| First REL-01 HTTP test | FAIL | The in-test Driver Kestrel host had not registered location services, so full endpoint inference failed at routing. Registered the real `LocationAcceptor`/`DriverEventPublisher`/options; reran green. |
| Post-review unit hardening | PASS | Added control-token fail-closed test and non-vacuous bounded-hold assertions; 6/6 `DriverFaultPointTests` pass. |

No failing test was skipped, muted or made vacuous to obtain a green run.

## 9. CI status

- Latest `main` runs (`40343480340` at `86062af`, `40343237565`, `40343160418`,
  `40342610376`): `failure` with **0 job steps**. The four jobs (`build-test`,
  `platform-smoke`, `compose-config`, `hygiene`) have no runner and no steps,
  which is an account-billing block before execution, not a source regression.
- This branch's GitHub Actions result is reported separately in the PR/final
  handoff. No green CI claim is made here.

## 10. Artifact integrity

`hashes.txt` lists SHA-256 digests for the verified source files, campaign log,
final-state snapshots, process identity files and test logs. Raw container logs
are under `raw/` (gitignored) and are bounded by the run timestamp recorded in
`final-state/run-summary.json`.
