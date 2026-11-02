# Reproduce the accepted W14 scope

Use the preserved integration branch. Its code is the measured724d6c6 tree;
subsequent commits only close evidence. The branch retains certificate ancestry
that GitHub's rebase merge changes on main. Historical W13 measurements retain
their own source and definitions; do not relabel them as W14 observations.

Prerequisites: Docker Desktop/Linux containers, Compose supporting the committed
!override/!reset tags (observed5.6.0), Python3, PowerShell and .NET10. Host
measurements used SDK10.0.403. Do not reuse a contributor worktree or a production
Compose project. All reset commands below name only the disposable test project.

```powershell
git clone --branch integration/team-w14-final https://github.com/minhqng/ride-hailing-microservices-system.git rh-w14-rehearsal
Set-Location rh-w14-rehearsal
git status --short
python scripts/security/init-local-auth.py
dotnet build scripts/test/w10-g3-harness/RideHailing.W10Harness.csproj -c Release
$env:COMPOSE_PROJECT_NAME='team-w12-w14-ha'
$env:COMPOSE_FILE='compose.yaml;deploy/compose/compose.ha.yaml'
$env:TRIP_MESSAGING_ENABLED='true'
docker compose --profile ha --profile obs build
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/fault/ha-up.ps1 -Applications -ProjectName team-w12-w14-ha
docker compose --profile ha --profile obs up -d --no-build --wait
python scripts/test/platform-smoke.py
python scripts/fault/team-ha-conservation.py --output plans/reports/conservation.json
python scripts/fault/team-ha-runtime.py --scenario broker --output plans/reports/broker-runtime.json
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/evidence/member-a-recovery-check.ps1 -MetricsOnly
```

The process-scoped PowerShell invocation leaves machine execution-policy settings
unchanged. The initialization script keeps existing complete auth keys, generates
a disposable HA cookie when needed and never prints credentials. Do not copy .env
or bearer tokens into evidence. Existing broker data without the initialization
marker is deliberately refused; archive and reset only a disposable old profile.

Full runtime rehearsals require a fresh dataset so an earlier available Driver
cannot win the next actor's reservation. After archiving prior evidence:

```powershell
docker compose --profile ha --profile obs down -v
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/fault/ha-up.ps1 -Applications -ProjectName team-w12-w14-ha
docker compose --profile ha --profile obs up -d --no-build --wait
python scripts/test/platform-smoke.py
python scripts/fault/team-ha-runtime.py --scenario quorum --node rabbitmq --output plans/reports/primary-runtime.json
docker compose --profile ha --profile obs stop
Remove-Item Env:COMPOSE_FILE
Remove-Item Env:COMPOSE_PROJECT_NAME
```

The root node must lead a non-dead-letter critical queue; the wrapper records the
actual selected queue, all19 quorum memberships and real node-running flags. It
requires2/3 throughout all journeys and3/3 after return. Payment's cancellation
restart uses --no-deps so it cannot silently restore the lost primary. Provider
version evidence is read from the surviving broker. The independent auditor
reads every owning PostgreSQL authority. No business row or outcome is fabricated.

Stop other campaign stacks before E04. Build all app images once, then retain
them across all three fresh-volume repetitions:

```powershell
$env:W13_G4_EVIDENCE='evidence/member-b/w13-performance-campaign/g4/team-w14-readiness.json'
python scripts/perf/w14.py g4
docker compose --project-name ride-hailing-w14 -f compose.yaml -f scripts/perf/compose.w13.yaml -f scripts/perf/compose.w14.yaml --profile obs build identity driver matching trip gateway payment notification
$env:W13_USE_PREBUILT_IMAGES='1'
python scripts/perf/w14.py e04 --mode report --rate 0.5 --outage 30 --baseline 30 --timeout 120 --repeats 3 --seed 1400
```

The runner records a new source/host/image fingerprint, obtains real Identity
ownership, keeps publishing through recovery, performs read-only final audits
and removes its own project/volumes in finally. All confirmations, sampling
points and final state remain under the ignored member-b outputs directory.
Smoke is never accepted as report-grade. Missing/changed artifacts, incomplete
repetitions, dirty code, G4 drift and observed resource/image changes fail closed.

Analyze the retained accepted measurements from the preserved integration branch:

```powershell
python scripts/perf/w14.py analyze evidence/team/w14-integration/e04/W14-E04-20261102T170141Z-e6c185b4 evidence/team/w14-integration/e04/W14-E04-20261102T170344Z-e8057611 evidence/team/w14-integration/e04/W14-E04-20261102T170539Z-b6a2a2ff
python -m unittest discover -s scripts/perf/tests -v
dotnet test RideHailing.Distributed.slnx -c Release --logger trx --results-directory TestResults
```

A fresh main campaign needs a certificate bound to its independently verified main
source; the historical branch certificate is not asserted to survive rebase ancestry.
The release CI also runs a fresh Linux checkout, full image build, actual security
checks and all three demo journeys. Exact artifact digests and checkout trees are
verified independently, with merge/post-main refs recorded in TEAM PR61.

Claims are limited to one physical Docker host and the tested application-level
failure windows. E04 is Matching NO_DRIVER recovery, not end-to-end ride capacity.
Broker management polling and variable interval service rates limit timing/model
precision. Explicit Redis replay is distinct from automatic projection survival.
No PostgreSQL/Redis HA, arbitrary partition tolerance, whole-host resilience or
zero-downtime guarantee follows from these experiments.
