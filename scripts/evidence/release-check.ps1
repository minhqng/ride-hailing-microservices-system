param([switch]$WithDocker)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path

function Assert-LastExitCode([string]$Message) {
    if ($LASTEXITCODE -ne 0) { throw $Message }
}

Push-Location $root
try {
    python scripts/security/scan-secrets.py
    Assert-LastExitCode 'Secret/release scan failed.'

    dotnet build RideHailing.Distributed.slnx -c Release --nologo
    Assert-LastExitCode 'Release build failed.'

    dotnet test tests/Unit/RideHailing.PaymentService.UnitTests -c Release --no-build --nologo
    Assert-LastExitCode 'Payment unit regression failed.'

    dotnet test tests/Unit/RideHailing.NotificationWorker.UnitTests -c Release --no-build --nologo
    Assert-LastExitCode 'Notification unit regression failed.'

    $filter = 'FullyQualifiedName~FailurePolicyTests|FullyQualifiedName~OutboxRelayUnitTests|FullyQualifiedName~MessagingMetricsTests|FullyQualifiedName~OutboxMessageTests|FullyQualifiedName~RabbitConnectionFactoryTests'
    dotnet test tests/Integration/RideHailing.Platform.IntegrationTests -c Release --no-build --nologo --filter $filter
    Assert-LastExitCode 'Deterministic Member C integration regression failed.'

    if ($WithDocker) {
        if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
            throw 'Docker CLI is required for -WithDocker.'
        }
        docker compose -f compose.yaml -f deploy/compose/compose.release.yaml config --quiet
        Assert-LastExitCode 'Release Compose validation failed.'
        docker compose -f compose.yaml -f deploy/compose/compose.ha.yaml --profile ha config --quiet
        Assert-LastExitCode 'HA Compose validation failed.'
    }

    Write-Output 'PASS: Member C deterministic release gate.'
}
finally {
    Pop-Location
}
