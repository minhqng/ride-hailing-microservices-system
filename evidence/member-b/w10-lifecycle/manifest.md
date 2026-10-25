# W10 Phase 1 verification manifest

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
