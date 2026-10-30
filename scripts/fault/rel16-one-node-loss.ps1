param(
    [Parameter(Mandatory=$true)][ValidateSet('rabbitmq-2','rabbitmq-3')][string]$Node,
    [ValidatePattern('^[a-z0-9][a-z0-9_-]+$')][string]$ProjectName = 'ride-memc-ha',
    [switch]$Execute
)
$ErrorActionPreference = 'Stop'
if (-not $Execute) { throw 'This stops a broker container. Re-run with -Execute after confirming the disposable HA project.' }
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if (-not (Get-Command docker -ErrorAction SilentlyContinue)) { throw 'Docker CLI is required.' }
$compose = @('compose','--project-name',$ProjectName,'-f','compose.yaml','-f','deploy/compose/compose.ha.yaml','--profile','ha')

Push-Location $root
try {
    & docker @compose exec -T $Node rabbitmq-queues check_if_node_is_quorum_critical
    if ($LASTEXITCODE -ne 0) { throw "$Node is quorum-critical; refusing one-node-loss test." }
    & docker @compose stop $Node
    if ($LASTEXITCODE -ne 0) { throw "Failed to stop $Node." }
    & docker @compose exec -T rabbitmq rabbitmqctl cluster_status
    & docker @compose exec -T rabbitmq rabbitmq-queues quorum_status payment.commands.q
    if ($LASTEXITCODE -ne 0) { throw 'Critical queue lost quorum.' }
    Write-Output "PASS: majority remained and payment.commands.q stayed available with $Node stopped."
}
finally {
    & docker @compose start $Node
    Pop-Location
}
