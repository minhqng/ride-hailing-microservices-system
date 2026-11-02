param(
    [switch]$Applications,
    [ValidatePattern('^[a-z0-9][a-z0-9_-]+$')][string]$ProjectName = 'ride-memc-ha'
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if (-not (Get-Command docker -ErrorAction SilentlyContinue)) { throw 'Docker CLI is required.' }
. (Join-Path $PSScriptRoot 'quorum-common.ps1')
$compose = @('compose','--project-name',$ProjectName,'-f','compose.yaml','-f','deploy/compose/compose.ha.yaml','--profile','ha')

Push-Location $root
try {
    & docker @compose up -d rabbitmq rabbitmq-2 rabbitmq-3
    if ($LASTEXITCODE -ne 0) { throw 'RabbitMQ HA startup failed.' }

    $deadline = (Get-Date).AddMinutes(3)
    do {
        $probe = Invoke-ComposeCommand -Compose $compose -Arguments @('exec', '-T', 'rabbitmq', 'rabbitmqctl', 'cluster_status') -AllowFailure
        $status = $probe.Output
        $ready = $probe.ExitCode -eq 0 -and $status.Contains('rabbit@rabbitmq-2') -and $status.Contains('rabbit@rabbitmq-3')
        if (-not $ready) { Start-Sleep -Seconds 2 }
    } until ($ready -or (Get-Date) -gt $deadline)
    if (-not $ready) { throw "Three-node cluster did not form.`n$status" }

    if ($Applications) {
        & docker @compose --profile obs up -d payment notification prometheus
        if ($LASTEXITCODE -ne 0) { throw 'Payment/Notification/Prometheus startup failed.' }

        $deadline = (Get-Date).AddMinutes(3)
        do {
            $queues = @(Get-QuorumQueueNames -Compose $compose)
            $topologyReady = $queues -contains 'payment.commands.q' -and
                $queues -contains 'notification.business-events.q'
            if (-not $topologyReady) { Start-Sleep -Seconds 2 }
        } until ($topologyReady -or (Get-Date) -gt $deadline)
        if (-not $topologyReady) { throw 'Payment/Notification did not declare the shared topology.' }
    }

    & docker @compose exec -T rabbitmq rabbitmq-queues grow 'rabbit@rabbitmq-2' all --vhost-pattern '.*' --queue-pattern '.*' --errors-only
    if ($LASTEXITCODE -ne 0) { throw 'Quorum membership growth on rabbitmq-2 failed.' }
    & docker @compose exec -T rabbitmq rabbitmq-queues grow 'rabbit@rabbitmq-3' all --vhost-pattern '.*' --queue-pattern '.*' --errors-only
    if ($LASTEXITCODE -ne 0) { throw 'Quorum membership growth on rabbitmq-3 failed.' }

    if ($Applications) {
        $queues = @(Get-QuorumQueueNames -Compose $compose)
        if ($queues.Count -eq 0) { throw 'No quorum queues were found after topology declaration.' }
        foreach ($queue in $queues) {
            $state = Wait-ForQuorumOnline -Compose $compose -Queue $queue -Online 3 -TimeoutSeconds 180
            Write-Output "QUORUM: $queue online=$($state.Online)/$($state.Total)"
        }
    }

    & docker @compose exec -T rabbitmq rabbitmqctl cluster_status
    if ($LASTEXITCODE -ne 0) { throw 'cluster_status failed after membership growth.' }
    Write-Output 'PASS: three RabbitMQ nodes are clustered and quorum membership reached the requested size.'
}
finally { Pop-Location }
