$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
Set-Location $RepoRoot
$TrxDir = 'TestResults/quick-' + [Guid]::NewGuid().ToString('N')
# Testcontainers-based integration tests need the Docker socket inside the SDK
# container. Mounted only when present so socket-less hosts still build/test.
$SocketMount = @()
if (Test-Path \\.\pipe\docker_engine) {
  $SocketMount = @('-v', '/var/run/docker.sock:/var/run/docker.sock')
}
docker run --rm -e "TRX_DIR=$TrxDir" -v "${RepoRoot}:/work:z" @SocketMount -w /work mcr.microsoft.com/dotnet/sdk:10.0@sha256:de4fe90a98845d75910b9769662465c84de1a934665c431279d1382b2bddc02b bash -c 'dotnet restore RideHailing.Distributed.slnx && dotnet build RideHailing.Distributed.slnx -c Release --no-restore && dotnet test RideHailing.Distributed.slnx -c Release --no-build --logger trx --results-directory "$TRX_DIR"'

if ($LASTEXITCODE -ne 0) { throw 'dotnet test failed in disposable SDK container.' }
python scripts/test/assert-trx.py --results-dir $TrxDir --min-passed 1200 --min-files 12 --max-skipped 1 --allowed-skip "RideHailing.IdentityService.IntegrationTests.AuthNegativeTests.RejectedRequestsDoNotMutateAuthoritativeState" --required-suites-file scripts/test/required-suites.txt
if ($LASTEXITCODE -ne 0) { throw 'TRX verification failed; expected real executed tests.' }
