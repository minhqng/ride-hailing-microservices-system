param(
    [ValidatePattern('^[a-z0-9][a-z0-9_-]+$')][string]$ProjectName = 'ride-memc-ha',
    [string]$PrimaryManagementUrl = 'http://localhost:15672',
    [string]$SurvivorManagementUrl = 'http://localhost:25672',
    [string]$RabbitUser = $env:RABBITMQ_USER,
    [string]$RabbitPassword = $env:RABBITMQ_PASSWORD,
    [ValidateRange(2, 200)][int]$ProbeCount = 12,
    [int]$BacklogTimeoutSeconds = 90,
    [int]$DrainTimeoutSeconds = 240,
    [string]$EvidencePath,
    [switch]$Execute
)
$ErrorActionPreference = 'Stop'
if (-not $Execute) {
    throw 'This stops Payment and the primary broker in a disposable HA project. Re-run with -Execute.'
}
if (-not (Get-Command docker -ErrorAction SilentlyContinue)) { throw 'Docker CLI is required.' }

$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
. (Join-Path $PSScriptRoot 'quorum-common.ps1')
$compose = @('compose','--project-name',$ProjectName,'-f','compose.yaml','-f','deploy/compose/compose.ha.yaml','--profile','ha')
if ([string]::IsNullOrWhiteSpace($RabbitUser)) { $RabbitUser = 'ride_app' }
if ([string]::IsNullOrWhiteSpace($RabbitPassword)) { $RabbitPassword = 'ride_dev_pw' }
$headers = New-RabbitManagementHeaders -Username $RabbitUser -Password $RabbitPassword
$runId = "rel15-$([DateTimeOffset]::UtcNow.ToString('yyyyMMddHHmmss'))-$([Guid]::NewGuid().ToString('N').Substring(0, 8))"
$queue = 'payment.commands.q'
$paymentStopped = $false
$primaryStopped = $false

Push-Location $root
try {
    try {
        Wait-RabbitManagementApi -ManagementUrl $PrimaryManagementUrl -Headers $headers
        Wait-RabbitManagementApi -ManagementUrl $SurvivorManagementUrl -Headers $headers
        $beforeQuorum = Wait-ForQuorumOnline -Compose $compose -Queue $queue -Online 3 -TimeoutSeconds 60
        $baseline = Get-PaymentQueueState -ManagementUrl $PrimaryManagementUrl -Headers $headers
        if ($baseline.Consumers -lt 1) {
            throw 'Payment has no payment.commands.q consumer before the fault.'
        }
        if ($baseline.Ready -ne 0 -or $baseline.Unacked -ne 0) {
            throw "REL-15 requires an idle baseline; observed ready=$($baseline.Ready) unacked=$($baseline.Unacked)."
        }
        Write-Output "BASELINE: $queue quorum=$($beforeQuorum.Online)/$($beforeQuorum.Total) consumers=$($baseline.Consumers) ready=0 unacked=0"

        # Isolate the application/broker boundary before publishing. This makes
        # every routed delivery durable backlog rather than racing a live consumer.
        Invoke-ComposeCommand -Compose $compose -Arguments @('stop', 'payment') | Out-Null
        $paymentStopped = $true
        $consumerDeadline = (Get-Date).AddSeconds(60)
        do {
            $isolated = Get-PaymentQueueState -ManagementUrl $PrimaryManagementUrl -Headers $headers
            if ($isolated.Consumers -eq 0) { break }
            Start-Sleep -Seconds 2
        } until ((Get-Date) -gt $consumerDeadline)
        if ($isolated.Consumers -ne 0) { throw 'Payment consumer did not disconnect before probe publication.' }

        Invoke-ComposeCommand -Compose $compose -Arguments @('stop', 'rabbitmq') | Out-Null
        $primaryStopped = $true
        Wait-RabbitManagementApi -ManagementUrl $SurvivorManagementUrl -Headers $headers
        $degraded = Wait-ForQuorumOnline -Compose $compose -Queue $queue -Online 2 `
            -ControlService 'rabbitmq-2' -TimeoutSeconds 90
        if ($degraded.Total -ne 3 -or $degraded.Online -ne 2) {
            throw "Expected exactly 2/3 online quorum members with the primary stopped; observed $($degraded.Online)/$($degraded.Total)."
        }

        $published = Publish-PaymentProbeBatch -ManagementUrl $SurvivorManagementUrl -Headers $headers `
            -RunId $runId -Count $ProbeCount -Copies 2
        $expectedBacklog = $published.RoutedDeliveries
        $backlogDeadline = (Get-Date).AddSeconds($BacklogTimeoutSeconds)
        do {
            $backlog = Get-PaymentQueueState -ManagementUrl $SurvivorManagementUrl -Headers $headers
            if ($backlog.Ready -ge $expectedBacklog -and $backlog.Unacked -eq 0) { break }
            Start-Sleep -Seconds 2
        } until ((Get-Date) -gt $backlogDeadline)
        if ($backlog.Ready -lt $expectedBacklog -or $backlog.Unacked -ne 0) {
            throw "Backlog did not accumulate while the boundary was isolated: expected >=$expectedBacklog ready, observed ready=$($backlog.Ready) unacked=$($backlog.Unacked)."
        }
        Write-Output "OUTAGE: primary stopped; quorum=2/3; consumers=0; ready=$($backlog.Ready); unique=$ProbeCount; duplicate deliveries=$($published.RoutedDeliveries - $ProbeCount)"

        Invoke-ComposeCommand -Compose $compose -Arguments @('start', 'rabbitmq') | Out-Null
        $primaryStopped = $false
        Wait-RabbitManagementApi -ManagementUrl $PrimaryManagementUrl -Headers $headers
        $restoredQuorum = Wait-ForQuorumOnline -Compose $compose -Queue $queue -Online 3 -TimeoutSeconds 180

        $recoveryStart = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
        Invoke-ComposeCommand -Compose $compose -Arguments @('start', 'payment') | Out-Null
        $paymentStopped = $false

        $final = Wait-PaymentProbeConvergence -Compose $compose -RunId $runId `
            -Expected $ProbeCount -TimeoutSeconds $DrainTimeoutSeconds
        $drainDeadline = (Get-Date).AddSeconds($DrainTimeoutSeconds)
        do {
            $drained = Get-PaymentQueueState -ManagementUrl $PrimaryManagementUrl -Headers $headers
            $observedConnections = Invoke-RabbitManagementApi -ManagementUrl $PrimaryManagementUrl -Headers $headers `
                -Path '/api/connections'
            $connections = @($observedConnections | Where-Object {
                    $_.client_properties.connection_name -eq 'payment-service' -and
                    [long]$_.connected_at -ge $recoveryStart
                })
            $allAcknowledged = $null -ne $drained.Ack -and
                ($drained.Ack - [long]$baseline.Ack) -ge $published.RoutedDeliveries
            if ($drained.Consumers -ge 1 -and $drained.Ready -eq 0 -and
                $drained.Unacked -eq 0 -and $connections.Count -gt 0 -and $allAcknowledged) { break }
            Start-Sleep -Seconds 2
        } until ((Get-Date) -gt $drainDeadline)
        if ($drained.Consumers -lt 1 -or $drained.Ready -ne 0 -or
            $drained.Unacked -ne 0 -or $connections.Count -eq 0 -or -not $allAcknowledged) {
            throw "Payment did not reconnect and drain the probe backlog: consumers=$($drained.Consumers) ready=$($drained.Ready) unacked=$($drained.Unacked) freshConnections=$($connections.Count)."
        }

        if ($EvidencePath) {
            [ordered]@{ runId = $runId; sourceCommit = (& git rev-parse HEAD); sourceTree = (& git rev-parse 'HEAD^{tree}');
                before = $beforeQuorum; during = $degraded; after = $restoredQuorum;
                baseline = $baseline; backlog = $backlog; published = $published;
                final = $final; queue = $drained; freshConnections = $connections.Count } |
                ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $EvidencePath -Encoding utf8
        }
        Write-Output ("FINAL: quorum={0}/{1}; operations={2}; providerEffects={3}; dedupMarkers={4}; outboxPublished={5}; queue ready={6} unacked={7}" -f `
            $restoredQuorum.Online, $restoredQuorum.Total, $final.operations,
            $final.providerEffects, $final.processedMessages, $final.outboxPublished,
            $drained.Ready, $drained.Unacked)
        Write-Output "PASS: REL-15 accumulated $expectedBacklog durable deliveries during the isolated boundary, recovered exactly $ProbeCount logical Payment effects, deduplicated exact replays, confirmed every result Outbox publication and drained the queue."
    }
    finally {
        if ($primaryStopped) {
            Invoke-ComposeCommand -Compose $compose -Arguments @('start', 'rabbitmq') -AllowFailure | Out-Null
        }
        if ($paymentStopped) {
            Invoke-ComposeCommand -Compose $compose -Arguments @('start', 'payment') -AllowFailure | Out-Null
        }
    }
}
finally { Pop-Location }
