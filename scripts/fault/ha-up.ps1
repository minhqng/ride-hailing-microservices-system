param(
    [switch]$Applications,
    [ValidatePattern('^[a-z0-9][a-z0-9_-]+$')][string]$ProjectName = 'ride-memc-ha'
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if (-not (Get-Command docker -ErrorAction SilentlyContinue)) { throw 'Docker CLI is required.' }
$compose = @('compose','--project-name',$ProjectName,'-f','compose.yaml','-f','deploy/compose/compose.ha.yaml','--profile','ha')

Push-Location $root
try {
    & docker @compose up -d rabbitmq rabbitmq-2 rabbitmq-3
    if ($LASTEXITCODE -ne 0) { throw 'RabbitMQ HA startup failed.' }

    $deadline = (Get-Date).AddMinutes(3)
    do {
        $status = (& docker @compose exec -T rabbitmq rabbitmqctl cluster_status 2>&1 | Out-String)
        $ready = $LASTEXITCODE -eq 0 -and $status.Contains('rabbit@rabbitmq-2') -and $status.Contains('rabbit@rabbitmq-3')
        if (-not $ready) { Start-Sleep -Seconds 2 }
    } until ($ready -or (Get-Date) -gt $deadline)
    if (-not $ready) { throw "Three-node cluster did not form.`n$status" }

    if ($Applications) {
        & docker @compose up -d payment notification
        if ($LASTEXITCODE -ne 0) { throw 'Payment/Notification startup failed.' }
    }

    & docker @compose exec -T rabbitmq rabbitmq-queues grow 'rabbit@rabbitmq-2' all --vhost-pattern '.*' --queue-pattern '.*' --errors-only
    & docker @compose exec -T rabbitmq rabbitmq-queues grow 'rabbit@rabbitmq-3' all --vhost-pattern '.*' --queue-pattern '.*' --errors-only
    & docker @compose exec -T rabbitmq rabbitmqctl cluster_status
    Write-Output 'PASS: three RabbitMQ nodes are clustered. Quorum membership growth requested.'
}
finally { Pop-Location }
