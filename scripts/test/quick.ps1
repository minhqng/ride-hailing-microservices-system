$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
Set-Location $RepoRoot
docker run --rm -v "${RepoRoot}:/work:z" -w /work mcr.microsoft.com/dotnet/sdk:10.0 bash -c 'dotnet restore RideHailing.Distributed.slnx && dotnet build RideHailing.Distributed.slnx -c Release --no-restore && dotnet test RideHailing.Distributed.slnx -c Release --no-build'
