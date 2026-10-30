param(
    [ValidatePattern('^[a-z0-9][a-z0-9_-]+$')][string]$ProjectName = 'ride-memc-ha',
    [string]$ManagementUrl = 'http://localhost:15672',
    [string]$RabbitUser = $env:RABBITMQ_USER,
    [string]$RabbitPassword = $env:RABBITMQ_PASSWORD,
    [int]$DrainTimeoutSeconds = 180,
    [switch]$Execute
)
$ErrorActionPreference = 'Stop'
if (-not $Execute) { throw 'This restarts the primary broker. Re-run with -Execute against the disposable HA project.' }
if (-not (Get-Command docker -ErrorAction SilentlyContinue)) { throw 'Docker CLI is required.' }
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$compose = @('compose','--project-name',$ProjectName,'-f','compose.yaml','-f','deploy/compose/compose.ha.yaml','--profile','ha')
if ([string]::IsNullOrWhiteSpace($RabbitUser)) { $RabbitUser = 'ride_app' }
if ([string]::IsNullOrWhiteSpace($RabbitPassword)) { $RabbitPassword = 'ride_dev_pw' }
$authHeaders = @{
    Authorization = 'Basic ' + [Convert]::ToBase64String(
        [Text.Encoding]::UTF8.GetBytes("${RabbitUser}:${RabbitPassword}"))
}

function Invoke-ManagementApi {
    param([string]$Path)
    try {
        return Invoke-RestMethod -Uri "$($ManagementUrl.TrimEnd('/'))$Path" -Headers $authHeaders -TimeoutSec 5
    }
    catch {
        throw "RabbitMQ management API request failed: $Path`n$($_.Exception.Message)"
    }
}

function Get-PaymentConnections {
    return @(Invoke-ManagementApi '/api/connections' | Where-Object {
        $_.client_properties.connection_name -eq 'payment-service'
    })
}

function Get-PaymentQueueState {
    $queue = Invoke-ManagementApi '/api/queues/%2F/payment.commands.q'
    return [pscustomobject]@{
        Consumers = [int]$queue.consumers
        Ready = [int]$queue.messages_ready
        Unacked = [int]$queue.messages_unacknowledged
    }
}

function Wait-ManagementApi {
    param([int]$TimeoutSeconds = 60)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        try { Invoke-ManagementApi '/api/overview' | Out-Null; return }
        catch { if ((Get-Date) -le $deadline) { Start-Sleep -Seconds 2 } }
    } until ((Get-Date) -gt $deadline)
    throw "RabbitMQ management API did not become ready at $ManagementUrl."
}

Push-Location $root
try {
    Wait-ManagementApi
    $connectionDeadline = (Get-Date).AddSeconds(60)
    do {
        try { $connections = @(Get-PaymentConnections) } catch { $connections = @() }
        try { $baseline = Get-PaymentQueueState } catch { $baseline = $null }
        if ($connections.Count -gt 0 -and $null -ne $baseline -and $baseline.Consumers -ge 1) { break }
        if ((Get-Date) -le $connectionDeadline) { Start-Sleep -Seconds 2 }
    } until ((Get-Date) -gt $connectionDeadline)
    if ($connections.Count -eq 0 -or $null -eq $baseline -or $baseline.Consumers -lt 1) {
        throw 'Payment is not connected to payment.commands.q before the fault; refusing a false recovery claim.'
    }
    Write-Output ("BASELINE: payment-service connections={0}; payment.commands.q consumers={1} ready={2} unacked={3}" -f `
        $connections.Count, $baseline.Consumers, $baseline.Ready, $baseline.Unacked)

    # Only a connection created after this instant proves that Payment actually
    # re-established its session instead of having stayed on another node.
    $interventionStart = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
    & docker @compose restart rabbitmq
    if ($LASTEXITCODE -ne 0) { throw 'Primary broker restart failed.' }

    Wait-ManagementApi
    $recoveryDeadline = (Get-Date).AddSeconds($DrainTimeoutSeconds)
    do {
        try { $connections = @(Get-PaymentConnections) } catch { $connections = @() }
        $state = $null
        try { $state = Get-PaymentQueueState } catch { }
        $newConnections = @($connections | Where-Object {
            [long]$_.connected_at -ge $interventionStart
        })
        $staleConnections = @($connections | Where-Object {
            [long]$_.connected_at -lt $interventionStart
        })
        # Every payment-service connection must be new: if any consumer or
        # publisher session survived on another node, the primary restart did
        # not exercise Payment's reconnect path and must not be reported PASS.
        $recovered = $connections.Count -gt 0 -and $staleConnections.Count -eq 0 -and
            $null -ne $state -and
            $state.Consumers -ge $baseline.Consumers -and
            $state.Ready -le $baseline.Ready -and
            $state.Unacked -le $baseline.Unacked
        if (-not $recovered -and (Get-Date) -le $recoveryDeadline) { Start-Sleep -Seconds 2 }
    } until ($recovered -or (Get-Date) -gt $recoveryDeadline)

    if (-not $recovered) {
        $stateText = if ($null -eq $state) { '<unreadable>' } else {
            "consumers=$($state.Consumers) ready=$($state.Ready) unacked=$($state.Unacked)"
        }
        throw (("Payment did not establish a new connection for every session and drain to baseline within {0}s " +
            "(connections={1}; new={2}; stale={3}; state={4}). If a session stayed on another node, " +
            "the primary restart did not exercise Payment's reconnect path.") -f `
            $DrainTimeoutSeconds, $connections.Count, $newConnections.Count, $staleConnections.Count, $stateText)
    }

    & docker @compose exec -T rabbitmq rabbitmqctl cluster_status
    if ($LASTEXITCODE -ne 0) { throw 'cluster_status failed after recovery.' }
    Write-Output (("PASS: payment-service established fresh connections for every session and " +
        "payment.commands.q drained to baseline (consumers={0} ready={1} unacked={2} newConnections={3}).") -f `
        $state.Consumers, $state.Ready, $state.Unacked, $newConnections.Count)
}
finally { Pop-Location }
