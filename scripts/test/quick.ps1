$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
Set-Location $RepoRoot
# Testcontainers-based integration tests need the Docker socket inside the SDK
# container. Mounted only when present so socket-less hosts still build/test.
$SocketMount = @()
if (Test-Path \\.\pipe\docker_engine) {
  $SocketMount = @('-v', '/var/run/docker.sock:/var/run/docker.sock')
}
docker run --rm -v "${RepoRoot}:/work:z" @SocketMount -w /work mcr.microsoft.com/dotnet/sdk:10.0 bash -c 'dotnet restore RideHailing.Distributed.slnx && dotnet build RideHailing.Distributed.slnx -c Release --no-restore && dotnet test RideHailing.Distributed.slnx -c Release --no-build'
