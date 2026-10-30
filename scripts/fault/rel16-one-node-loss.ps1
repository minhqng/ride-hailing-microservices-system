param(
    [Parameter(Mandatory=$true)][ValidateSet('rabbitmq-2','rabbitmq-3')][string]$Node,
    [ValidatePattern('^[a-z0-9][a-z0-9_-]+$')][string]$ProjectName = 'ride-memc-ha',
    [int]$RejoinTimeoutSeconds = 180,
    [switch]$Execute
)
$ErrorActionPreference = 'Stop'
if (-not $Execute) { throw 'This stops a broker container. Re-run with -Execute after confirming the disposable HA project.' }
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if (-not (Get-Command docker -ErrorAction SilentlyContinue)) { throw 'Docker CLI is required.' }
. (Join-Path $PSScriptRoot 'quorum-common.ps1')
$compose = @('compose','--project-name',$ProjectName,'-f','compose.yaml','-f','deploy/compose/compose.ha.yaml','--profile','ha')
$queue = 'payment.commands.q'

Push-Location $root
$failure = $null
try {
    try {
        & docker @compose exec -T $Node rabbitmq-queues check_if_node_is_quorum_critical
        if ($LASTEXITCODE -ne 0) { throw "$Node is quorum-critical; refusing one-node-loss test." }

        $before = Wait-ForQuorumOnline -Compose $compose -Queue $queue -Online 3 -TimeoutSeconds 60
        Write-Output "BEFORE: $queue online=$($before.Online)/$($before.Total)"

        & docker @compose stop $Node
        if ($LASTEXITCODE -ne 0) { throw "Failed to stop $Node." }

        $deadline = (Get-Date).AddSeconds(90)
        $after = $null
        do {
            try { $after = Get-OnlineQuorumMembers -Compose $compose -Queue $queue }
            catch { $after = $null }
            if ($null -ne $after -and $after.Total -eq 3 -and $after.Online -eq 2) { break }
            if ((Get-Date) -le $deadline) { Start-Sleep -Seconds 2 }
        } until ((Get-Date) -gt $deadline)

        if ($null -eq $after -or $after.Total -ne 3 -or $after.Online -ne 2) {
            $observed = if ($null -eq $after) { 'no readable quorum status' } else { "$($after.Online)/$($after.Total) online" }
            throw "Expected 2/3 online members of $queue with $Node stopped; observed $observed."
        }
        if ($after.Online -lt 2) { throw "No online quorum majority for $queue with $Node stopped." }

        & docker @compose exec -T rabbitmq rabbitmqctl cluster_status
        if ($LASTEXITCODE -ne 0) { throw 'cluster_status failed during the outage.' }
        Write-Output "DURING OUTAGE: $queue online=$($after.Online)/$($after.Total); majority held with $Node stopped."
    }
    catch {
        $failure = $_
    }

    & docker @compose start $Node
    if ($LASTEXITCODE -ne 0) { throw "Failed to start $Node for rejoin." }
    $rejoined = Wait-ForQuorumOnline -Compose $compose -Queue $queue -Online 3 -TimeoutSeconds $RejoinTimeoutSeconds
    Write-Output "AFTER REJOIN: $queue online=$($rejoined.Online)/$($rejoined.Total)"

    & docker @compose exec -T rabbitmq rabbitmqctl cluster_status
    if ($LASTEXITCODE -ne 0) { throw 'cluster_status failed after rejoin.' }
}
finally {
    Pop-Location
}

if ($null -ne $failure) { throw $failure }
Write-Output "PASS: $queue kept a 2/3 online quorum with $Node stopped and returned to 3/3 after rejoin."
