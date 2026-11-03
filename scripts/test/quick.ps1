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
docker run --rm -e "TRX_DIR=$TrxDir" -v "${RepoRoot}:/work:z" @SocketMount -w /work mcr.microsoft.com/dotnet/sdk:10.0 bash -c 'dotnet restore RideHailing.Distributed.slnx && dotnet build RideHailing.Distributed.slnx -c Release --no-restore && dotnet test RideHailing.Distributed.slnx -c Release --no-build --logger trx --results-directory "$TRX_DIR"'

if ($LASTEXITCODE -ne 0) { throw 'dotnet test failed in disposable SDK container.' }
python scripts/test/assert-trx.py --results-dir $TrxDir --min-passed 1200 --min-files 12 --max-skipped 1
if ($LASTEXITCODE -ne 0) { throw 'TRX verification failed; expected real executed tests.' }
