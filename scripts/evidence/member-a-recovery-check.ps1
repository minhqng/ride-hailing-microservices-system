param(
    [string]$Prometheus = "http://localhost:9090",
    [switch]$MetricsOnly
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$queries = [ordered]@{
    SagaActive = 'sum by (step) ({__name__=~"saga_active(_sagas)?"})'
    OutboxPending = 'sum({__name__=~"outbox_pending(_messages)?"})'
    OutboxOldestSeconds = 'max({__name__=~"outbox_oldest_pending_age(_seconds)?"})'
    OutboxFailedVisible = 'sum({__name__=~"outbox_failed_visible(_messages)?"})'
}

foreach ($entry in $queries.GetEnumerator()) {
    $encoded = [Uri]::EscapeDataString($entry.Value)
    $response = Invoke-RestMethod "$($Prometheus.TrimEnd('/'))/api/v1/query?query=$encoded" -TimeoutSec 10
    if ($response.status -ne "success") { throw "Prometheus query failed: $($entry.Key)" }
    [pscustomobject]@{ Metric = $entry.Key; Result = ($response.data.result | ConvertTo-Json -Compress -Depth 8) }
}

if (-not $MetricsOnly) {
    Push-Location $root
    try {
        dotnet test tests/Integration/RideHailing.TripService.IntegrationTests `
            -c Release --nologo --filter "FullyQualifiedName~TripW12InvariantAuditorTests"
        if ($LASTEXITCODE -ne 0) { throw "Trip invariant auditor regression failed." }
    }
    finally { Pop-Location }
}

Write-Output "PASS: Trip-side recovery metrics captured; claim remains scoped to the tested scenario."
