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
        Assert-LastExitCode 'Release Compose validation failed with supplied secrets.'

        # Prove the release overlay is fail-closed. Compose gives process
        # variables precedence over --env-file, so temporarily remove and then
        # restore the required variables around the negative render.
        $saved = @{}
        foreach ($name in $releaseSecrets) {
            $saved[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
            Remove-Item "Env:$name" -ErrorAction SilentlyContinue
        }
        $emptyEnvFile = New-TemporaryFile
        try {
            $negativeOutput = docker compose --env-file $emptyEnvFile.FullName `
                -f compose.yaml -f deploy/compose/compose.release.yaml config --quiet 2>&1 | Out-String
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
        if ($negativeOutput -notmatch 'must be set for the release overlay') {
            throw "Release Compose failed without secrets for an unexpected reason:`n$negativeOutput"
        }

        docker compose -f compose.yaml -f deploy/compose/compose.ha.yaml --profile ha config --quiet
        Assert-LastExitCode 'HA Compose validation failed.'
    }

    Write-Output 'PASS: Member C deterministic release gate.'
}
finally {
    Pop-Location
}
