# W10 verification manifest

## Phase 2 final verification — 2026-10-25 Asia/Saigon

Worktree `D:/rh-w10b`, existing branch `feature/B-W10-driver-lifecycle`.
Starting W10 head `2d9a3032978296246b172b53001123c22015d7fd`; unchanged base
`66d7d1c1f05a87fe3156b43b94616ae3201006bb`. Implementation commit:
`87b639f5c471861129f05811968e92a3090a5ce7`. Final evidence is a separate docs-only child; compiled
inputs are unchanged by that child. Published head and exact-head CI are
recorded on [PR #39](https://github.com/minhqng/ride-hailing-microservices-system/pull/39).

### Selected unique suites

| Suite | PASS | FAIL | SKIP | Local raw/phase2 artifact |
|---|---:|---:|---:|---|
| Driver unit | 123 | 0 | 0 | driver-unit-final.trx |
| Driver integration (includes all 16 compensation cases) | 107 | 0 | 0 | driver-integration-final.trx |
| Matching unit | 104 | 0 | 0 | matching-unit.trx |
| Matching integration / real Redis projection | 138 | 0 | 0 | matching-integration.trx |
| Trip unit | 267 | 0 | 0 | trip-unit.trx |
| Trip integration / intake | 131 | 0 | 0 | trip-integration.trx |
| Shared platform / Outbox / dedup | 60 | 0 | 0 | platform-integration.trx |
| **Total unique** | **930** | **0** | **0** | Focused/reruns are subsets |

Release solution build PASS; one existing xUnit2029 warning remains at
DriverReserveConcurrencyTests.cs:283. No errors/warnings were suppressed.
Focused compensation filter: 16 PASS. The seven-suite run followed the review
repair. Final naming-only cleanup removed a phase label from an upgrade test
name/comment, then Release build and full affected Driver suites reran; unchanged
Matching/Trip/platform results are retained. `git diff --check` and
`docker compose config --quiet` PASS. No A/C production or shared messaging edits.
The only added direct test package is the repository-pinned existing EF Relational
version, aligning the new test-only Trip reference; no production package change.

### Exact commands

The versioned [runner](../../../scripts/test/member-b-w10-compensation.sh) contains
all commands and fails on the first nonzero exit. It runs Release solution build,
the compensation filter, and the seven selected projects sequentially.

```powershell
docker run --rm --name rh-w10b-p2-final `
  -v D:/rh-w10b:/work -v rh-w9b-nuget:/root/.nuget/packages `
  -v /var/run/docker.sock:/var/run/docker.sock `
  -e TESTCONTAINERS_HOST_OVERRIDE=host.docker.internal `
  -e TESTCONTAINERS_RYUK_DISABLED=true `
  -e W10_SOURCE_HEAD=2d9a3032978296246b172b53001123c22015d7fd+phase2-final-worktree `
  -w /work mcr.microsoft.com/dotnet/sdk:10.0.402 `
  bash /work/scripts/test/member-b-w10-compensation.sh
```

Final affected runner uses the same mounts/environment/SDK with container
`rh-w10b-p2-final-affected` and
`bash /work/evidence/member-b/w10-lifecycle/raw/run-phase2-final-affected.sh`:

```bash
dotnet build RideHailing.Distributed.slnx -c Release --nologo
dotnet test tests/Unit/RideHailing.DriverService.UnitTests -c Release --no-build --no-restore --logger 'trx;LogFileName=driver-unit-final.trx' --results-directory /work/evidence/member-b/w10-lifecycle/raw/phase2
dotnet test tests/Integration/RideHailing.DriverService.IntegrationTests -c Release --no-build --no-restore --logger 'trx;LogFileName=driver-integration-final.trx' --results-directory /work/evidence/member-b/w10-lifecycle/raw/phase2
```

Host recorded `git diff HEAD -- src tests scripts` as tested-source.diff before
the SDK run. `source-head.txt` records the caller-supplied precommit worktree label;
`tested-inputs-final.sha256` identifies the final source inputs. The worktree's
host-only `.git` pointer cannot resolve inside Docker. These records plus the
implementation commit identify tested inputs; hashes are not a build attestation.

### Environment and resources

Observed this run: Docker Desktop engine 29.8.2; SDK 10.0.402, runtime 10.0.13,
Ubuntu 24.04 SDK container. Real fixture images: PostgreSQL 18.6, RabbitMQ
4.3.6-management, Redis 8.10.2. All four image IDs match the retained Phase 1
table below (inspected this run). Version claims are image-tag versions; no
separate broker/Redis executable probe was run.

Tracked runners (all `--rm`): p2-repro, p2-focused, p2-focused-2,
p2-focused-3, p2-validation, p2-upgrade-repro, p2-final, p2-sdk-info and
p2-final-affected, each with `rh-w10b-` prefix. Harness sessions: 76546,
27998, 99835, 32328, 71047, 30555, 33247. All exited; fixtures dispose their
own servers, including containers intentionally retained across stop/start.
No dev server was started. No user/mobile/pg-probe container was stopped or
changed; existing NuGet cache volume remains intentionally retained.

### Failures, reruns and independent review

1. Before fix, `ReleaseRequiresPositiveIntegerRound` failed (0 PASS / 1 FAIL),
   proving missing round validation. Retained `raw/phase2-before.trx`.
2. First focused integration: 11 PASS / 2 FAIL because a test tried to attach
   round2 to a Trip with only round1 opened. Corrected setup: real A round1
   producer versus Driver round2; content-conflict case tampers the payload
   intentionally. `raw/phase2-focused.trx` retained. Initial new Trip reference
   also surfaced EF Relational version conflict; explicit central-version test
   reference resolved it, without suppressing the warning.
3. Restart harness run: 8 PASS / 7 FAIL. Docker remapped ports and old pools
   retained terminated sessions. Fixtures now refresh endpoints and recreate
   the owned pool after restart. `raw/phase2-focused-2.trx` retained. Next
   focused run passed 15/15 (`raw/phase2-focused-3.trx`).
4. Initial broad runner stopped before build on a host-only worktree Git pointer.
   Runner now accepts caller source label and hashes inputs; no tests were
   counted from that failed invocation.
5. First full run passed 929 unique tests before review repair; retained under
   `raw/phase2-before-review-fix/`. Fresh-context reviewer found a P1: validation
   before receipt lookup broke existing three-field command replay. The upgrade
   test failed before repair (`raw/phase2-upgrade-before.trx`). Receipt-first
   replay now preserves old results, requires round only for new execution,
   and rejects altered content before mutation. Final full run: 930 PASS.
6. Independent reviewer re-read the repaired source and upgrade proof: **no
   remaining P0/P1**. It did not independently run suites. Scoped simplifier
   simplified test scenario setup only; no assertions/proofs removed.

### External and not-run gates

Full REL-09 (real C terminal Payment effect/outcome through persisted A Saga,
B release and Saga confirmation), combined A #40 integration, A/C human review,
W11 OS process-kill campaign, local Payment/Identity/System/performance suites:
**NOT RUN / EXTERNAL DEPENDENCY**, not counted as PASS. Broker/PG stop/start and
commit-before-ACK are real component proofs, not a full W11 campaign.
Preflight checks: #35/#36/#37/#39 had no submitted reviews; all exact-head CI
runs succeeded. A #40 exact head also had successful CI. Final publication CI
is checked separately on the new #39 head and recorded on that PR.

## Retained Phase 1 verification manifest

Date: 2026-10-25 Asia/Saigon. Worktree `D:/rh-w10b`.
Code commit: `b1435f79d1fd5b29696de68eeeaedcbd989e7a96`.
Parent/base: `66d7d1c1f05a87fe3156b43b94616ae3201006bb`.
Evidence follows in a separate docs-only commit. Artifact hashes establish
artifact identity; source linkage is the recorded tested inputs and Git diff,
not a cryptographic build attestation.

## Final unique selected suites

| Suite | PASS | FAIL | SKIP | Retained TRX |
|---|---:|---:|---:|---|
| Driver unit | 122 | 0 | 0 | driver-unit-final.trx |
| Driver integration | 91 | 0 | 0 | driver-integration-final.trx |
| Matching unit | 104 | 0 | 0 | matching-unit.trx |
| Matching integration | 138 | 0 | 0 | matching-integration.trx |
| Trip unit | 267 | 0 | 0 | trip-unit.trx |
| Trip integration | 131 | 0 | 0 | trip-integration.trx |
| Shared platform / W9 Outbox / dedup | 60 | 0 | 0 | platform-integration.trx |
| **Total** | **913** | **0** | **0** | Focused reruns are subsets |

Focused lifecycle run: 24 PASS after fixing its two initial test setup errors.
Final review-fix filter: 3 PASS (host startup/reconnect plus payload/type conflicts).
Trip and Matching producer compatibility filters: 1 PASS each, included above.
Release solution build PASS, 0 errors, one pre-existing xUnit2029 warning in
DriverReserveConcurrencyTests.cs:283. No warning was hidden or disabled.
`git diff --check` and `docker compose config --quiet` PASS.

The full seven-suite run preceded the final host-review fix. Final Release
rebuild, focused regression and full Driver unit/integration were rerun after
the fix; unchanged Matching/Trip/platform behavior reused that full-run evidence.
No A/C production source or shared messaging implementation changed.

NOT RUN locally: Payment/Identity/System/performance suites, actual A W10 producer
integration, full Payment-failure REL-09 and W11 process-kill campaign. They are
not counted as PASS. GitHub CI runs full solution tests and smoke checks separately
against the published head; its URL and outcome are recorded on the PR.

## Environment

Docker Desktop engine 29.8.2; disposable Testcontainers PostgreSQL 18.6,
RabbitMQ 4.3.6-management, Redis 8.10.2. SDK 10.0.402, runtime 10.0.13.
Image IDs:

| Image | SHA256 |
|---|---|
| mcr.microsoft.com/dotnet/sdk:10.0.402 | 747cbade5bfd918a175ac32f8f1d931edec2c7d0d5397a3de72eaef1ac1f66d2 |
| postgres:18.6 | 74935e72241653ca55e0414067e6d8763aceb8a810eb51b452253ec3dcfc4336 |
| rabbitmq:4.3.6-management | 54cb013d76cd1fd6c0a0708f34062ad61c452bb6f016b434c6c33f1a765d2b58 |
| redis:8.10.2 | 2c2dff791878316e3b083188be0d578423b24813cdda5de0e80ea4f7b3e6bfd6 |

Versions above are pinned fixture/image versions; image IDs were inspected this
run. No separate Redis/Rabbit executable-version probe was run. No live mobile
database/broker or user process was reused, modified or stopped.

## Commands and resources

Docker SDK invocation from the W10 worktree:

```powershell
docker run --rm --name rh-w10b-validation `
  -v D:/rh-w10b:/work -v rh-w9b-nuget:/root/.nuget/packages `
  -v /var/run/docker.sock:/var/run/docker.sock `
  -e TESTCONTAINERS_HOST_OVERRIDE=host.docker.internal `
  -e TESTCONTAINERS_RYUK_DISABLED=true -w /work `
  mcr.microsoft.com/dotnet/sdk:10.0.402 bash /work/evidence/member-b/w10-lifecycle/raw/run-validation.sh
```

Raw scripts retain exact sequential commands: restore/Release solution build;
`dotnet test <project> -c Release --no-build --no-restore --logger trx` for the
seven projects above; focused compatibility filters; then `run-final-driver.sh`
for final build and affected Driver reruns. Dependency audit:
`dotnet list src/Services/Driver/RideHailing.DriverService package --vulnerable --include-transitive`:
no vulnerable packages. No dependencies were added.

Tracked harness sessions and unique container names: rh-w10b-build (52224),
rh-w10b-focused (15517 then 6388), rh-w10b-validation (75835), rh-w10b-final
(18479 then 24426). Each invocation exited, --rm removed the runner and fixtures
disposed their containers. Testcontainers bound ephemeral ports owned by the
disposable fixtures. No detached dev server was started. NuGet cache volume
rh-w9b-nuget is intentionally retained. Existing mobile stack and pg-probe are
user-owned and remain untouched.

## Retained failures and review repairs

1. Initial focused run: 22 PASS, 2 FAIL. Migration test incorrectly ran old
   public-schema migrations under another search_path in a migrated DB. Fixed
   to create a separate real database. Production migration remained unchanged.
2. Initial relay mark-loss test did not expect the shared deterministic crash
   seam and did not expire the claim lease. It now asserts the crash and uses
   a zero-lease restarted relay; identical IDs and bodies are required.
3. Reviewer found default-disabled runtime ingress: enabled the default,
   documented rollback flag and added real hosted-consumer integration.
4. Reviewer caught repeated migration DDL on reconnect: successful Driver and
   Outbox bootstraps now have independent readiness flags. Real queue cancellation
   while PG blocks DDL proves reconnect and completion remain live.

Initial failures are retained in lifecycle-initial.trx. Earlier successful
Driver results are preserved before the final reconnect regression test.
Raw files remain ignored/local; hashes below identify retained artifacts.

## Artifact SHA256

| Local raw artifact | SHA256 |
|---|---|
| build.log | 9bc87bca22ec28d9a0dd46f92485a8c049d0757b3a6fd013bd2e735009b1b063 |
| build-before-reconnect-fix.log | 14de63bfec9f0af52fa7ad6759c922c921d3aeb46e2b4525b0ca10ed8d462800 |
| build-final.log | c8e440d2e5e46bbddca1aaafbf4656583286d1c7c5407979468a7d49d1eb8f2c |
| driver-before-reconnect-fix.trx | 6c11067a2a1bd705a1a4c25fb195417230a9ecb6ece4d0a1fd7a307d7f464608 |
| driver-dependency-audit.log | e8184e8141be56261c108f1bd82a3bbe64dded94d78182fe1fb70b40cc2bd576 |
| driver-integration.log | 4cf08cda1440ceb93116a42dc7fcd1aed3abd54105491c4084aa53cd57111f57 |
| driver-integration.trx | aed3c44ad27975ae6b3bcc3fa1cd47e80caed68d1a4a1fde137af405c10e8d61 |
| driver-integration-final.log | f151273b231bb00380543856c03038628cbef5bc27a3153c8edbf1646fe375d5 |
| driver-integration-final.trx | a629a31624e5d52cd0831ed5bb7286888903cb549b6f4effc14fa772c47fb92c |
| driver-review-fixes.log | a63026c860009042cbc81726f5d304be14a2fef74243f7983e56bc9ee645c7f9 |
| driver-review-fixes.trx | 85bb116183356f0782d0fcf3a3c2f89cf7842007d009d2a49a3b515d3c91890b |
| driver-unit.log | 73b5ea825e91b1e292e41b466f76fd0085b17b83c103db3fea9875dbb805dae7 |
| driver-unit.trx | a56dc726d302085cab974b02dffb3488950782f4160ade334fde743360632008 |
| driver-unit-final.log | 68ce212f57a7a9f58443722b2a9f2472bd445ba910d681819d96acde3cf63d08 |
| driver-unit-final.trx | 3ba6d67e20685dfb94c8731c65c0c6ea6d574ef83565ffed36a7fdd56d5e9bf5 |
| lifecycle.trx | 3c374b8c16b8c25e5bb642f7609d0f85f998e5baf9018f351c652c0105d2ae9f |
| lifecycle-initial.trx | da80a2dd44f0ab118f5fbaee3d395a11e3a9594d3f3d5c237603a10681b53c47 |
| matching-integration.log | c2d822f8302f8f32b5c1ab81a485bbe2d217d97768ccbcc564b8e6baca9e4370 |
| matching-integration.trx | 57f9f62377cb2bce1a5622c79ffaa70bde01eecc96c48f365594ba6a27f6bf3e |
| matching-lifecycle.log | c68a23f3b3576995242b1170cc9ce0d1cd6b8a7688bffb1e9ee03321ae9a4a88 |
| matching-lifecycle.trx | 3e2bd61cae7c1506e1d219b7d8ce67ce61640a4b5d8d619fd35d19d4da99207c |
| matching-unit.log | 9a992273c940f15a4478555f7c2ddf96960eb8bd5773b7647b66ca1fcba0cda7 |
| matching-unit.trx | 73d39ab68296b4c9d054138e82147b076d83b7f7669fea30b4c1cf1c4a7490c8 |
| platform-integration.log | 0f852f12a4299fc477ec61053dee4bbe7ce6883b9c5d83df653b7cfe86b6f46c |
| platform-integration.trx | 0da17bba0e83613ebc715e94253fe63ae78595fb7180d3fbdd999bb50e230f35 |
| run-final-driver.sh | 30d69473b7b4a6e6fefa9f0d9926aae007b244a6339cfd85a31fb1a8d5919284 |
| run-validation.sh | 010705dd69123de2358a03963638c80e48416226a87904a6b2d1c89087036d7f |
| trip-integration.log | f6dc5d376d8df274d84cc0a70d0663ea6593b37e8604502397af24a1e4704403 |
| trip-integration.trx | 2a3ccc322ab9813893780c182b09c402ee2cd80641952b9942c28841a548f069 |
| trip-lifecycle.log | 9026d358f2871bd5cc423d53269cb0b7cbb9f6ee62c8c86f7b1a7335caa9a8d1 |
| trip-lifecycle.trx | e45cc973181e2a0fcfaa3fb8b9955f8db1d03bad92ee233e47a464aca7ea7d32 |
| trip-unit.log | 90fe3b3b8644e29710db43feda8f6f5c0911ea204bbd4e58fe2ae54131646a4d |
| trip-unit.trx | f67e085294e5f540b1be1de7c8afcb37c1e3487e016ca605b05624ae0482189f |

## Phase 2 artifact SHA256

Local ignored artifacts are retained for audit; these hashes identify files.

| Artifact below evidence/member-b/w10-lifecycle | SHA256 |
|---|---|
| raw/phase2/build-final.log | 655409902d15db270e8de59ffcea11297d4c1e6f37370f9ce89e3e0a58f8fda9 |
| raw/phase2/compensation.trx | e50906faa49008406a4fba2afe1b6d7f479e7919aab2ad7ba8e36d4e9182df78 |
| raw/phase2/driver-unit-final.trx | 1f086febffacdc6fe0655294ff787f1123aa411aaaa7913bcda3e6a7bcf4f24c |
| raw/phase2/driver-integration-final.trx | 7ce8351d2de8836a7cb89186e70418c0e3e1a5460d3fa226535af4f5b36840bb |
| raw/phase2/matching-unit.trx | dc0015cc84296119c570358b3a7ac464b41328c2562aa9234e11ba7309b1be8f |
| raw/phase2/matching-integration.trx | 1ece198964ee6c7fd20956e8191a65653d396c2aae091e9e634f33b1b0d28ca8 |
| raw/phase2/trip-unit.trx | c117bc648ded914647fa80b389fc766449f34c46aecabc1fdc44abe0f547b114 |
| raw/phase2/trip-integration.trx | 1816405ec08e481a6de93e00b6ce91300ba971aac78d92c6dbccf2917663405e |
| raw/phase2/platform-integration.trx | fe3711e6759fd02a1fada6bc2fdf9ad071f214d854e9773bcd71c4722b6e4874 |
| raw/phase2/tested-inputs-final.sha256 | 63ca3e8edf125dee51b11df48ad4c63f3234c307c4f1c55712a4e31b5a354c50 |
| raw/phase2-before.trx | 7c8eeb73cf3f5163503889f35028f265cbcc5a0a584e33ebf474a1d8882d1d9f |
| raw/phase2-focused.trx | f84a4cd9b4c2a79150c7c9209dc86e752f03c752f3168e1682a82cc12db98ef3 |
| raw/phase2-focused-2.trx | 43b3f01eb421802b98613264d5af19516ec99da9512a74c30f49dbef6292f9ab |
| raw/phase2-focused-3.trx | 63902dc562f88e554cb3d0ddb47a0d2cec28b0c8af2cbaf82590704ea9015761 |
| raw/phase2-upgrade-before.trx | 70c1000e3272f116397901a5cb88b4f32de5f854d820f8847476811eb4ce2850 |
| raw/run-phase2-final-affected.sh | 304b7c783d063ae278c4b69a5597a59c3f9294cdea374ea290c7c83426f55118 |
