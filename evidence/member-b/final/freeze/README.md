# Driver/Matching feature freeze evidence

The [technical report](../../../../docs/driver-matching-report.md) explains
the frozen implementation. The [manifest](manifest.json) is the task/evidence
index. It distinguishes historical accepted experiments from fresh regression,
cross-member authority and release-process dependencies.

No service, technology, product feature or performance optimization is added.
The only cleanup removes the obsolete Matching README claim that integrated
performance/HA experiments had not started. Existing compatibility seams,
canonical experiments, rejected measurements and historical source-bound
proofs remain preserved.

| Candidate | Classification | Evidence / reason |
| --- | --- | --- |
| Matching README unstarted-experiments paragraph | MIGRATE_THEN_DELETE | Replaced by links to accepted TEAM W13/W14 measurements and their limits. |
| MatchingRoundService and internal round endpoint | KEEP | G3 harness, auditor and regression consumers remain. |
| W11 fault scripts and fault-control endpoints | KEEP | Deterministic response-loss/process proofs; Testing/token gates remain. |
| Original member evidence and old measured source | KEEP | Required to reproduce exact historical source; not dead code. |
| W13 rejected image-drift campaign | KEEP | Invalid setup is retained and excluded from accepted measurements. |
| W14 pre-fix and failed HA observations | KEEP | Establishes the confirmed-work-loss counterexample and corrected boundary. |
| Duplicate Redis startup function ensure | KEEP | No release benefit justifies changing startup behavior during freeze. |

Reproduce from a fresh full clone, never the author's build directory:

```powershell
git clone https://github.com/minhqng/ride-hailing-microservices-system.git ride-b-check
cd ride-b-check
git checkout --detach <sourceCommit-from-manifest>
python scripts/security/init-local-auth.py
$env:COMPOSE_PROJECT_NAME='team-w12-b-freeze'
$env:TRIP_MESSAGING_ENABLED='true'
docker compose config --quiet
docker compose up -d --wait postgres redis rabbitmq
for ($attempt = 0; $attempt -lt 60; $attempt++) {
  docker compose exec -T postgres pg_isready -h 127.0.0.1
  if ($LASTEXITCODE -eq 0) { break }
  Start-Sleep -Seconds 1
}
if ($LASTEXITCODE -ne 0) { throw 'PostgreSQL TCP startup did not complete' }
docker compose --profile obs up -d --build --wait
python scripts/test/platform-smoke.py
# SDK container supports Windows hosts without a native dotnet installation.
docker run --rm -v "${PWD}:/work" -v /var/run/docker.sock:/var/run/docker.sock -e TESTCONTAINERS_HOST_OVERRIDE=host.docker.internal -w /work mcr.microsoft.com/dotnet/sdk:10.0 bash -lc 'dotnet restore RideHailing.Distributed.slnx && dotnet build RideHailing.Distributed.slnx -c Release --no-restore && dotnet test RideHailing.Distributed.slnx -c Release --no-build --logger trx --results-directory TestResults/regression'
docker run --rm -v "${PWD}:/work" -w /work mcr.microsoft.com/dotnet/sdk:10.0 dotnet build scripts/test/w10-g3-harness/RideHailing.W10Harness.csproj -c Release
python scripts/test/core-runtime-gate.py
docker compose --profile obs down -v
```

Use only the named disposable project; ports must be free before startup.
Run regression and stack startup sequentially to avoid resource contention.
Do not call a failed or zero-discovery suite PASS. The ordinary Identity
theory is opt-in; execute its seven negative cases using the dedicated security
lane before claiming security acceptance. Container invocation is a tested
platform adaptation only to the extent recorded in test-output.txt.

Historical measurement reproduction belongs to
[W13](../../../team/w13-integration/README.md) and
[W14](../../../team/w14-integration/reproduce.md), at their recorded sources.
Their current-head source guards must not be bypassed to relabel old results.
