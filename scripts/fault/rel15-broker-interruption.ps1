param(
    [ValidatePattern('^[a-z0-9][a-z0-9_-]+$')][string]$ProjectName = 'ride-memc-ha',
    [switch]$Execute
)
$ErrorActionPreference = 'Stop'
if (-not $Execute) { throw 'This restarts the primary broker. Re-run with -Execute against the disposable HA project.' }
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$compose = @('compose','--project-name',$ProjectName,'-f','compose.yaml','-f','deploy/compose/compose.ha.yaml','--profile','ha')
Push-Location $root
try {
    & docker @compose restart rabbitmq
    if ($LASTEXITCODE -ne 0) { throw 'Primary broker restart failed.' }
    $deadline = (Get-Date).AddMinutes(2)
    do {
        try { $response = Invoke-WebRequest 'http://localhost:8105/health/ready' -TimeoutSec 3; $ready = $response.StatusCode -eq 200 }
        catch { $ready = $false }
        if (-not $ready) { Start-Sleep -Seconds 2 }
    } until ($ready -or (Get-Date) -gt $deadline)
    if (-not $ready) { throw 'Payment did not recover through the configured endpoint list.' }
    & docker @compose exec -T rabbitmq rabbitmqctl cluster_status
    Write-Output 'PASS: Payment readiness recovered after the primary broker restart.'
}
finally { Pop-Location }
