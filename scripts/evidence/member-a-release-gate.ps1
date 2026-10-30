param([switch]$WithDockerIntegration)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$tripUnit = "tests/Unit/RideHailing.TripService.UnitTests/RideHailing.TripService.UnitTests.csproj"
$tripIntegration = "tests/Integration/RideHailing.TripService.IntegrationTests/RideHailing.TripService.IntegrationTests.csproj"

function Assert-LastExitCode([string]$Message) {
    if ($LASTEXITCODE -ne 0) { throw $Message }
}

Push-Location $root
try {
    dotnet restore RideHailing.Distributed.slnx --nologo
    Assert-LastExitCode "Restore failed."
    dotnet build RideHailing.Distributed.slnx -c Release --no-restore --nologo
    Assert-LastExitCode "Release build failed."
    dotnet test $tripUnit -c Release --no-build --nologo
    Assert-LastExitCode "Trip unit regression failed."

    if ($WithDockerIntegration) {
        if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
            throw "Docker CLI is required for -WithDockerIntegration."
        }
        docker info *> $null
        Assert-LastExitCode "Docker daemon is unavailable."
        dotnet test $tripIntegration -c Release --no-build --nologo `
            --filter "FullyQualifiedName~TripW09|FullyQualifiedName~TripW10|FullyQualifiedName~TripW11|FullyQualifiedName~TripW12"
        Assert-LastExitCode "Member A real-infrastructure regression failed."
    }

    Write-Output "PASS: Member A deterministic release gate."
    if (-not $WithDockerIntegration) {
        Write-Output "NOTE: real-infrastructure lanes were not requested; use -WithDockerIntegration where Docker is exposed to this shell."
    }
}
finally {
    Pop-Location
}
