# Verification manifest

Date: 2026-10-25 (Asia/Saigon). Repository: minhqng/ride-hailing-microservices-system.
Worktree: `D:\rh-w9b`, branch `feature/B-W09-driver-outbox`.

## Revisions and preflight — VERIFIED

- C dependency: `7a0575a51a89ba32bd3017afd779355ed2da9576`.
- Phase 1: `0b1611cfee256f1acb1dc698a59675a18603d4c9`.
- Phase 2 / initial clean HEAD: `03c04380ec8ddd7409e0dce934c155f853922779`.
- Tested implementation: `d825920d51aaae39ecff31a72540bf45cd800b4a`.
- The evidence commit is separate; resolve it with `git log -1 --format=%H -- evidence/member-b/w09-outbox`.
- `git status --porcelain=v1` was empty before edits. `git merge-base --is-ancestor`
  returned 0 for Phase 1 and the C dependency. Phase 2 remains the direct parent of
  the implementation commit. No original commit was rewritten.
- PR [#35](https://github.com/minhqng/ride-hailing-microservices-system/pull/35)
  was OPEN, not merged, head exactly the C dependency above, base main at
  `ba5bb40615564ab022ddb0ff5e933bde93ee15a1`. The local gh binary was found under
  the user's temporary CLI installation but lacked authentication. Read-only
  connected GitHub `get_pr_info` supplied the verified metadata. No remote writes.

## Infrastructure — VERIFIED

All tests used disposable Testcontainers resources. Existing mobile application
containers and `pg-probe` were left alone. No development database was reused.

| Component | Version / image | Image ID |
|---|---|---|
| Docker client/server | 29.8.2 / 29.8.2 | local Docker Desktop |
| SDK | mcr.microsoft.com/dotnet/sdk:10.0.402; runtime 10.0.13; Ubuntu 24.04 x64 | sha256:747cbade5bfd918a175ac32f8f1d931edec2c7d0d5397a3de72eaef1ac1f66d2 |
| PostgreSQL | postgres:18.6; executable reports 18.6 (Debian 18.6-1.pgdg13+2) | sha256:74935e72241653ca55e0414067e6d8763aceb8a810eb51b452253ec3dcfc4336 |
| RabbitMQ | rabbitmq:4.3.6-management; CLI reports 4.3.6 | sha256:54cb013d76cd1fd6c0a0708f34062ad61c452bb6f016b434c6c33f1a765d2b58 |
| Redis | redis:8.10.2 (fixture/image pin; no separate server version probe) | sha256:2c2dff791878316e3b083188be0d578423b24813cdda5de0e80ea4f7b3e6bfd6 |

## Executed commands

From `D:\rh-w9b`, SDK commands ran inside this Docker configuration (unique
names used per invocation: rh-w9b-build, rh-w9b-targeted, rh-w9b-validation,
rh-w9b-final, rh-w9b-trip-verified):

```powershell
docker run --rm --name rh-w9b-validation `
  -v D:/rh-w9b:/work -v rh-w9b-nuget:/root/.nuget/packages `
  -v /var/run/docker.sock:/var/run/docker.sock `
  -e TESTCONTAINERS_HOST_OVERRIDE=host.docker.internal -w /work `
  mcr.microsoft.com/dotnet/sdk:10.0.402 bash /work/evidence/member-b/w09-outbox/raw/run-validation.sh
```

The initial restore invocation used no NuGet volume; later runs used the named
cache above. Ignored raw runner scripts retain exact executed command lines.
Their substantive SDK commands were:

```text
dotnet restore RideHailing.Distributed.slnx --nologo
dotnet build RideHailing.Distributed.slnx -c Release --no-restore --nologo
dotnet test tests/Unit/RideHailing.MatchingService.UnitTests -c Release --no-build --no-restore
dotnet test tests/Unit/RideHailing.DriverService.UnitTests -c Release --no-build --no-restore
dotnet test tests/Unit/RideHailing.TripService.UnitTests -c Release --no-build --no-restore
dotnet test tests/Integration/RideHailing.MatchingService.IntegrationTests -c Release --no-build --no-restore
dotnet test tests/Integration/RideHailing.DriverService.IntegrationTests -c Release --no-build --no-restore
dotnet test tests/Integration/RideHailing.TripService.IntegrationTests -c Release --no-build --no-restore
dotnet test tests/Integration/RideHailing.Platform.IntegrationTests -c Release --no-build --no-restore
```

Each test command also used `--logger 'trx;LogFileName=<run>.trx'
--results-directory /work/evidence/member-b/w09-outbox/raw`, with stdout/stderr
retained in the corresponding `.log`. Focused Matching invocations allowed build
and used these exact filters:

```text
FullyQualifiedName~CrashAfterReserveRecoveryTests|FullyQualifiedName~ProjectionDuplicateSafetyTests
FullyQualifiedName~CrashAfterReserveRecoveryTests|FullyQualifiedName~ProjectionDuplicateSafetyTests|FullyQualifiedName~MatchingTerminalIdentityMigrationTests|FullyQualifiedName~MatchingIntent_ConflictingSemanticMetadata
```

Focused Trip invocation used `--filter 'FullyQualifiedName~W9PublicationReplay'`.
After the final new assertion correction, the verified Trip targeted and full
runs used `--no-build --no-restore` against the corrected final Release build.
The only subsequent production-file change was a comment encoding repair.

## Final matrix — VERIFIED

| Scope | Result | Passed / failed / skipped | Local raw reference |
|---|---|---|---|
| Matching unit | PASS | 104 / 0 / 0 | RideHailing.MatchingService.UnitTests.trx |
| Driver unit | PASS | 117 / 0 / 0 | RideHailing.DriverService.UnitTests.trx |
| Trip unit | PASS | 267 / 0 / 0 | RideHailing.TripService.UnitTests.trx |
| Matching integration, final production source | PASS | 137 / 0 / 0 | final-matching.trx |
| Driver integration | PASS | 65 / 0 / 0 | RideHailing.DriverService.IntegrationTests.trx |
| Trip integration, corrected final tests | PASS | 130 / 0 / 0 | verified-trip.trx |
| Shared platform / Outbox / dedup | PASS | 60 / 0 / 0 | RideHailing.Platform.IntegrationTests.trx |
| REL-02 + projection targeted, subset of Matching | PASS | 3 / 0 / 0 | final-targeted.trx |
| Three event-type receipt targets, subset of Trip | PASS | 3 / 0 / 0 | verified-trip-targeted.trx |
| Terminal/migration/metadata focused earlier run, subset of Matching | PASS | 8 / 0 / 0 | w9-targeted.trx |
| Release solution build | PASS | final incremental build: 0 errors, 0 warnings | final-build.log |
| Real OS process-kill REL-02 campaign | NOT RUN | W11 orchestration | no artifact |
| Payment, Identity, System and performance suites | NOT RUN | outside requested validation scope | no artifact |

Unique selected suite total: **880 passed, 0 failed, 0 skipped**. Focused and
repeated runs are subsets and are not added again. Unit/Driver/platform results
were reused after changes that did not affect their tested behavior; Matching
and Trip were rerun after their final changes. The first full Release build had
one existing xUnit2029 warning in DriverReserveConcurrencyTests.cs:283.

C's `ConcurrentRelaysClaimDisjointBatches` passed. Its reported flake was not
observed; no C-owned code or assertions were modified, suppressed or retried.

## Retained failures and diagnosis

- Initial new REL test compilation: FAIL CS1061, accessing nonexistent
  `ReservationQuery.DriverVersion`. Corrected to its real `Version` property;
  focused tests and solution builds then passed. Diagnostic retained in session
  tool output, not represented as a completed test result.
- Initial validation wrapper: all seven SDK suites and build returned success,
  but its final Bash `exit` failed with `0\r: numeric argument required` because
  the PowerShell-written shell file had CRLF. Final runners were written with LF.
  The wrapper failure is not counted as a test success or product regression.
- `final-trip-targeted.trx`: **FAIL, 2 passed / 1 failed**. The new Released test
  incorrectly asserted a single total cleanup intent. Cancellation legitimately
  creates both CancelMatching and ReleaseReservation. Corrected the assertion to
  select the release intent and additionally preserve the total cleanup count.
  `verified-trip-targeted.trx` then passed all three cases.
- `final-trip.trx`: **FAIL, 108 passed / 22 failed**. The Rabbit fixture exited
  before those tests ran, reporting `Error when reading
  /var/lib/rabbitmq/.erlang.cookie: eacces` at TripRabbitFixture.InitializeAsync.
  The controller had concurrently run `docker exec <starting-test-broker>
  rabbitmqctl version` as root. Image inspection verifies default root exec and
  HOME=/var/lib/rabbitmq; the probe likely created/raced ownership of the cookie.
  That causal attribution is **REPORTED inference**, because ownership of the
  removed failed container was not captured. No A/C application defect is proven.
  Fresh isolated rerun without the probe passed all 130 tests. Failure artifacts
  remain intact; the suite was neither filtered nor weakened to obtain that pass.

## Crash windows and durable DB oracles

- REL-02: durable Matching IN_FLIGHT plus max-dispatched fence; Driver Reserve(R)
  and its Outbox intent committed; injected exception before local RESERVED.
  Before recovery: one attempt, same R held by Driver, terminalizer refuses and
  no MatchingFailed intent. After fresh-instance recovery: same ordinal/R,
  RESERVED attempt/round, same selected driver, winner version unchanged, second
  real driver still AVAILABLE at original version, exactly one DriverReservedV1
  intent (`SELECT count(*) FROM outbox_messages WHERE aggregate_id=@driver AND
  message_type='DriverReservedV1'` = 1), no failure intent.
- Outbox relay windows are separate from REL-02: tests inject exceptions after
  claim/before publish and after confirmed publish/before mark. Pending intent
  survives and the stored messageId is reused. These are not OS process crashes.
- Trip commit-before-ACK: real PostgreSQL receipt and effect commit, broker
  connection closes with zero ACK, redelivery has one receipt and no second
  version/effect. Failure-before-commit leaves neither receipt nor effect.
- Matching legacy upgrade: 006 keeps old NULL identity rows readable, enforces
  identity for new terminals, rejects identity reuse; safe replay fills one
  deterministic identity and one matching intent atomically.

Raw artifacts live only at `D:\rh-w9b\evidence\member-b\w09-outbox\raw\` and
are ignored by Git. They have not been uploaded. TRX files contain per-test
outcomes; source assertions are the DB oracle, not fabricated database dumps.

## Artifact SHA-256 inventory

Generated below from actual local files; failures are included as well as passes.

| Local raw artifact | SHA-256 |
|---|---|
| RideHailing.MatchingService.UnitTests.trx | 4538c4ed31900043052fdd90e6922a0950361397ab1dd0bccf39824bfe2a7699 |
| RideHailing.DriverService.UnitTests.trx | ea4cb0be2fd7e0c47c1c0b1115ffe7fbd41a28e8a375e62a21c33d9325bc256f |
| RideHailing.TripService.UnitTests.trx | ebb1797958037de768c180ae13bbd4a9886514f20eb5399d8aeb0066070c1f23 |
| final-matching.trx | ed1cf7477103923620564119d0a6dacf7caa6106602c647c0b633549ad783121 |
| RideHailing.DriverService.IntegrationTests.trx | 677a12a734ed8caadf04d82418e14b4859732dbf10909e75c5871e33bde2aec1 |
| verified-trip.trx | 0e2595cb5866cca3506b70e3019da49727ae8de41ca8a485c346f2837305719c |
| RideHailing.Platform.IntegrationTests.trx | 444b79c62598dfdbb7044e1b9635598a14dd89f4ee2df44bfee1bb53938a0395 |
| final-targeted.trx | 0994111a1f8bc336cd3b834ca0e5c5d54924314e15710add1abc495081fea12e |
| verified-trip-targeted.trx | 31e4699a37d235f7ad64c726fee25021a582cc0891e15676f9cd41a79564934e |
| w9-targeted.trx | a90c06e21f91fc41950ddc6547a604b4c60e2d7f4d0905b9167c8367653f968d |
| final-trip-targeted.trx | 3a543d2e2c50eed2497cf333cd344b38c366789d29435f45fdc75785796fdd23 |
| final-trip.trx | c18a6d281d6a8bf20e4bc19b79172c21b8267e0fe65d3b0b4d7cd9c7a11160a6 |
| final-trip-targeted.log | 9ea2147f749324be558c5aa42ad1fe70f6cf500e2c1bddb869cc5e22437e0617 |
| final-trip.log | 5bfe853b34364d6140b58d3824cf89b008b727a5071e40dd18b22d51abd857fc |
| build.log | 67839f1ff7acd0490e0d891f02333c1cafcef22483f43a407ebb3e7e69eddea1 |
| final-build.log | a07d5f08dfff1110fc493e6a87fe7e642fda86d499285b8c73aab96519b9ee9c |
