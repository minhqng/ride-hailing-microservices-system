param(
    [Parameter(Mandatory=$true)][ValidateSet('rabbitmq','rabbitmq-2','rabbitmq-3')][string]$Node,
    [ValidatePattern('^[a-z0-9][a-z0-9_-]+$')][string]$ProjectName = 'ride-memc-ha',
    [string]$ManagementUrl = 'http://localhost:15672',
    [string]$RabbitUser = $env:RABBITMQ_USER,
    [string]$RabbitPassword = $env:RABBITMQ_PASSWORD,
    [ValidateRange(2, 200)][int]$ProbeCount = 12,
    [int]$WorkloadTimeoutSeconds = 240,
    [int]$RejoinTimeoutSeconds = 180,
    [string]$EvidencePath,
    [switch]$Execute
)
$ErrorActionPreference = 'Stop'
if (-not $Execute) { throw 'This stops a broker container. Re-run with -Execute against the disposable HA project.' }
if (-not (Get-Command docker -ErrorAction SilentlyContinue)) { throw 'Docker CLI is required.' }

$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
. (Join-Path $PSScriptRoot 'quorum-common.ps1')
$compose = @('compose','--project-name',$ProjectName,'-f','compose.yaml','-f','deploy/compose/compose.ha.yaml','--profile','ha')
if ([string]::IsNullOrWhiteSpace($RabbitUser)) { $RabbitUser = 'ride_app' }
if ([string]::IsNullOrWhiteSpace($RabbitPassword)) { $RabbitPassword = 'ride_dev_pw' }
$headers = New-RabbitManagementHeaders -Username $RabbitUser -Password $RabbitPassword
$runId = "rel16-$([DateTimeOffset]::UtcNow.ToString('yyyyMMddHHmmss'))-$([Guid]::NewGuid().ToString('N').Substring(0, 8))"
$queue = 'payment.commands.q'
$controlService = if ($Node -eq 'rabbitmq') { 'rabbitmq-2' } else { 'rabbitmq' }
$nodeStopped = $false
$failure = $null

Push-Location $root
try {
    try {
        Wait-RabbitManagementApi -ManagementUrl $ManagementUrl -Headers $headers
        Invoke-ComposeCommand -Compose $compose -Arguments @(
            'exec', '-T', $Node, 'rabbitmq-queues', 'check_if_node_is_quorum_critical') | Out-Null

        $before = Wait-ForQuorumOnline -Compose $compose -Queue $queue -ControlService $controlService -Online 3 -TimeoutSeconds 60
        $leaderBefore = @($before.Rows | Where-Object { $_.'Raft State' -eq 'leader' })
        if ($leaderBefore.Count -ne 1) { throw 'Baseline must have exactly one quorum leader.' }
        $baseline = Get-PaymentQueueState -ManagementUrl $ManagementUrl -Headers $headers
        if ($baseline.Consumers -lt 1) { throw 'Payment has no consumer before REL-16.' }
        if ($baseline.Ready -ne 0 -or $baseline.Unacked -ne 0) {
            throw "REL-16 requires an idle baseline; observed ready=$($baseline.Ready) unacked=$($baseline.Unacked)."
        }
        Write-Output "BEFORE: $queue online=$($before.Online)/$($before.Total) consumers=$($baseline.Consumers) ready=0 unacked=0"

        Invoke-ComposeCommand -Compose $compose -Arguments @('kill', '-s', 'SIGKILL', $Node) | Out-Null
        $nodeStopped = $true

        $deadline = (Get-Date).AddSeconds(90)
        $degraded = $null
        do {
            try { $degraded = Get-OnlineQuorumMembers -Compose $compose -Queue $queue -ControlService $controlService }
            catch { $degraded = $null }
            if ($null -ne $degraded -and $degraded.Total -eq 3 -and $degraded.Online -eq 2) { break }
            Start-Sleep -Seconds 2
        } until ((Get-Date) -gt $deadline)
        if ($null -eq $degraded -or $degraded.Total -ne 3 -or $degraded.Online -ne 2) {
            $observed = if ($null -eq $degraded) { '<unreadable>' } else { "$($degraded.Online)/$($degraded.Total)" }
            throw "Expected 2/3 online quorum members with $Node stopped; observed $observed."
        }

        $published = Publish-PaymentProbeBatch -ManagementUrl $ManagementUrl -Headers $headers `
            -RunId $runId -Count $ProbeCount -Copies 2
        $final = Wait-PaymentProbeConvergence -Compose $compose -RunId $runId `
            -Expected $ProbeCount -TimeoutSeconds $WorkloadTimeoutSeconds
        $during = Get-OnlineQuorumMembers -Compose $compose -Queue $queue -ControlService $controlService
        $leaderDuring = @($during.Rows | Where-Object { $_.'Raft State' -eq 'leader' })
        if ($leaderDuring.Count -ne 1) { throw 'Node-loss state must have exactly one live leader.' }
        $drainDeadline = (Get-Date).AddSeconds($WorkloadTimeoutSeconds)
        do {
            $queueState = Get-PaymentQueueState -ManagementUrl $ManagementUrl -Headers $headers
            $allAcknowledged = $null -ne $queueState.Ack -and
                ($queueState.Ack - [long]$baseline.Ack) -ge $published.RoutedDeliveries
            if ($queueState.Consumers -ge 1 -and $queueState.Ready -eq 0 -and $queueState.Unacked -eq 0 -and $allAcknowledged) { break }
            Start-Sleep -Seconds 2
        } until ((Get-Date) -gt $drainDeadline)
        if (-not $allAcknowledged) { throw 'Actual broker acknowledgements do not cover every unique and duplicate probe.' }
        if ($during.Total -ne 3 -or $during.Online -ne 2) {
            throw "Quorum did not remain 2/3 through the workload; observed $($during.Online)/$($during.Total)."
        }
        if ($queueState.Consumers -lt 1 -or $queueState.Ready -ne 0 -or $queueState.Unacked -ne 0) {
            throw "Consumer did not continue and drain under one-node loss: consumers=$($queueState.Consumers) ready=$($queueState.Ready) unacked=$($queueState.Unacked)."
        }
        Write-Output ("DURING OUTAGE: quorum={0}/{1}; routed={2}; operations={3}; providerEffects={4}; dedupMarkers={5}; outboxPublished={6}; consumers={7}" -f `
            $during.Online, $during.Total, $published.RoutedDeliveries, $final.operations,
            $final.providerEffects, $final.processedMessages, $final.outboxPublished,
            $queueState.Consumers)
    }
    catch { $failure = $_ }

    if ($nodeStopped) {
        Invoke-ComposeCommand -Compose $compose -Arguments @('start', $Node) | Out-Null
        $nodeStopped = $false
    }
    $rejoined = Wait-ForQuorumOnline -Compose $compose -Queue $queue -ControlService $controlService -Online 3 `
        -TimeoutSeconds $RejoinTimeoutSeconds
    Write-Output "AFTER REJOIN: $queue online=$($rejoined.Online)/$($rejoined.Total)"
}
finally {
    if ($nodeStopped) {
        Invoke-ComposeCommand -Compose $compose -Arguments @('start', $Node) -AllowFailure | Out-Null
    }
    Pop-Location
}

if ($null -ne $failure) { throw $failure }
if ($EvidencePath) {
    [ordered]@{ runId = $runId; sourceCommit = (& git rev-parse HEAD); sourceTree = (& git rev-parse 'HEAD^{tree}');
        node = $Node; fault = 'SIGKILL'; baseline = $baseline; before = $before; during = $during; after = $rejoined;
        published = $published; final = $final; queue = $queueState } |
        ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $EvidencePath -Encoding utf8
}
Write-Output "PASS: REL-16 kept a 2/3 quorum while $Node was stopped, processed exactly $ProbeCount unique commands plus exact duplicates, confirmed all result Outbox publications, kept the consumer draining and returned to 3/3 after rejoin."
