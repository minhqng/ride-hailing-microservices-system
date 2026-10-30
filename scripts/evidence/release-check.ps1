param([switch]$WithDocker)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
Push-Location $root
try {
    python scripts/security/scan-secrets.py
    if ($LASTEXITCODE -ne 0) { throw 'Secret/release scan failed.' }
    dotnet build RideHailing.Distributed.slnx -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }
    dotnet test tests/Unit/RideHailing.PaymentService.UnitTests -c Release --no-build --nologo
    dotnet test tests/Unit/RideHailing.NotificationWorker.UnitTests -c Release --no-build --nologo
    dotnet test tests/Integration/RideHailing.Platform.IntegrationTests -c Release --no-build --nologo --filter 'FullyQualifiedName~FailurePolicyTests|FullyQualifiedName~OutboxRelayUnitTests|FullyQualifiedName~MessagingMetricsTests|FullyQualifiedName~OutboxMessageTests|FullyQualifiedName~RabbitConnectionFactoryTests'
    if ($LASTEXITCODE -ne 0) { throw 'Deterministic Member C regression failed.' }
    if ($WithDocker) {
        if (-not (Get-Command docker -ErrorAction SilentlyContinue)) { throw 'Docker CLI is required for -WithDocker.' }
        docker compose -f compose.yaml -f deploy/compose/compose.release.yaml config --quiet
        docker compose -f compose.yaml -f deploy/compose/compose.ha.yaml --profile ha config --quiet
        if ($LASTEXITCODE -ne 0) { throw 'Compose validation failed.' }
    }
    Write-Output 'PASS: Member C deterministic release gate.'
}
finally { Pop-Location }
