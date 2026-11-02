param(
    [string]$Prometheus = "http://localhost:9090",
    [switch]$MetricsOnly,
    [double]$MaxPending = 0,
    [double]$MaxOldestSeconds = 60,
    [double]$MaxFailedVisible = 0
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$queries = [ordered]@{
    SagaActive = 'sum by (step) ({__name__=~"saga_active(_sagas)?"})'
    OutboxPending = 'sum({__name__=~"outbox_pending(_messages)?"})'
    OutboxOldestSeconds = 'max({__name__=~"outbox_oldest_pending_age(_seconds)?"})'
    OutboxFailedVisible = 'sum({__name__=~"outbox_failed_visible(_messages)?"})'
}

function Get-PrometheusVector {
    param([string]$Name, [string]$Query)
    $encoded = [Uri]::EscapeDataString($Query)
    $response = Invoke-RestMethod "$($Prometheus.TrimEnd('/'))/api/v1/query?query=$encoded" -TimeoutSec 10
    if ($response.status -ne "success") { throw "Prometheus query failed: $Name" }
    if ($null -eq $response.data -or $null -eq $response.data.result) {
        throw "Prometheus response has no result payload: $Name"
    }
    return @($response.data.result)
}

function Convert-SeriesValue {
    param([string]$Name, $Series)
    if ($null -eq $Series.value -or $Series.value.Count -lt 2) {
        throw "Prometheus series has no instant value: $Name"
    }
    $value = [double]::Parse([string]$Series.value[1], [Globalization.CultureInfo]::InvariantCulture)
    if ([double]::IsNaN($value) -or [double]::IsInfinity($value)) {
        throw "Prometheus series has a non-finite value: $Name"
    }
    return $value
}

function Get-RequiredScalar {
    param([string]$Name, [string]$Query)
    $result = @(Get-PrometheusVector -Name $Name -Query $Query)
    if ($result.Count -eq 0) {
        throw "Prometheus query returned no series: $Name"
    }
    if ($result.Count -ne 1) {
        throw "Prometheus query returned $($result.Count) series where one was required: $Name"
    }
    return Convert-SeriesValue -Name $Name -Series $result[0]
}

$sagaResult = @(Get-PrometheusVector -Name "SagaActive" -Query $queries.SagaActive)
foreach ($series in $sagaResult) {
    $null = Convert-SeriesValue -Name "SagaActive" -Series $series
}
$pending = Get-RequiredScalar -Name "OutboxPending" -Query $queries.OutboxPending
$oldest = Get-RequiredScalar -Name "OutboxOldestSeconds" -Query $queries.OutboxOldestSeconds
$failedVisible = Get-RequiredScalar -Name "OutboxFailedVisible" -Query $queries.OutboxFailedVisible

if ($pending -gt $MaxPending) {
    throw "Outbox pending=$pending exceeds MaxPending=$MaxPending; recovery has not drained."
}
if ($oldest -gt $MaxOldestSeconds) {
    throw "Oldest pending Outbox age=$oldest s exceeds MaxOldestSeconds=$MaxOldestSeconds; recovery has not drained."
}
if ($failedVisible -gt $MaxFailedVisible) {
    throw "Outbox failed-visible=$failedVisible exceeds MaxFailedVisible=$MaxFailedVisible."
}

Write-Output ("OBSERVED: outbox_pending={0}; outbox_oldest_seconds={1}; outbox_failed_visible={2}; saga_active_series={3}" -f $pending, $oldest, $failedVisible, $sagaResult.Count)
if ($sagaResult.Count -eq 0) {
    Write-Warning "saga_active returned no series; this is valid only when no non-terminal Saga work remains."
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

Write-Output "PASS: Trip-side recovery metrics drained to the configured thresholds; claim remains scoped to the tested scenario."
