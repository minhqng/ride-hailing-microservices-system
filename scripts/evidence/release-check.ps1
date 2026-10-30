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
        $releaseSecrets = @(
            'POSTGRES_PASSWORD', 'IDENTITY_DB_PASSWORD', 'DRIVER_DB_PASSWORD',
            'TRIP_DB_PASSWORD', 'MATCHING_DB_PASSWORD', 'PAYMENT_DB_PASSWORD',
            'NOTIFICATION_DB_PASSWORD', 'RABBITMQ_PASSWORD')
        $missing = @($releaseSecrets | Where-Object {
            [string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($_, 'Process'))
        })
        if ($missing.Count -gt 0) {
            throw "Release Compose requires explicit secrets: $($missing -join ', ')"
        }

        docker compose -f compose.yaml -f deploy/compose/compose.release.yaml config --quiet
        if ($LASTEXITCODE -ne 0) { throw 'Release Compose validation failed with supplied secrets.' }

        # Prove the release overlay is fail-closed: with every required secret
        # removed, rendering must fail and name the first missing variable.
        $saved = @{}
        foreach ($name in $releaseSecrets) {
            $saved[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
            Remove-Item "Env:$name" -ErrorAction SilentlyContinue
        }
        $emptyEnvFile = New-TemporaryFile
        try {
            $negativeOutput = docker compose --env-file $emptyEnvFile.FullName -f compose.yaml -f deploy/compose/compose.release.yaml config --quiet 2>&1 | Out-String
            $negativeExit = $LASTEXITCODE
        }
        finally {
            Remove-Item $emptyEnvFile.FullName -ErrorAction SilentlyContinue
            foreach ($name in $releaseSecrets) {
                if ($null -eq $saved[$name]) { Remove-Item "Env:$name" -ErrorAction SilentlyContinue }
                else { Set-Item "Env:$name" $saved[$name] }
            }
        }
        if ($negativeExit -eq 0) { throw 'Release Compose rendered without required secrets.' }
        if ($negativeOutput -notmatch 'POSTGRES_PASSWORD') {
            throw "Release Compose failed without secrets for an unexpected reason:`n$negativeOutput"
        }

        docker compose -f compose.yaml -f deploy/compose/compose.ha.yaml --profile ha config --quiet
        if ($LASTEXITCODE -ne 0) { throw 'HA Compose validation failed.' }
    }
    Write-Output 'PASS: Member C deterministic release gate.'
}
finally { Pop-Location }
