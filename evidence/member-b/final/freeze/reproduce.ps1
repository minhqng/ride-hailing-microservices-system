param([string]$ProjectName = 'team-w12-b-freeze')
$ErrorActionPreference = 'Stop'
if ($ProjectName -ne 'team-w12-b-freeze') { throw 'Use the dedicated freeze project.' }
$root = (git -C $PSScriptRoot rev-parse --show-toplevel).Trim()
Set-Location $root
$manifest = Get-Content -Raw (Join-Path $PSScriptRoot 'manifest.json') | ConvertFrom-Json
if (-not $manifest.sourceCommit -or -not $manifest.sourceTree) { throw 'Missing verified source anchor.' }
$gitRoot = Split-Path (Split-Path (Get-Command git).Source -Parent) -Parent
$gitBash = Join-Path $gitRoot 'bin/bash.exe'
if (-not (Test-Path $gitBash)) { throw 'Git Bash is required for the repository source guard on Windows.' }
& $gitBash scripts/test/verify-source-anchor.sh $manifest.sourceCommit $manifest.sourceTree
if ($LASTEXITCODE -ne 0) { throw 'Source anchor rejected this checkout.' }
$existing = docker ps -aq --filter "label=com.docker.compose.project=$ProjectName"
if ($existing) { throw 'Disposable project already exists; inspect its owner before continuing.' }
$runId = (Get-Date -Format 'yyyyMMddTHHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8)
$output = Join-Path $PSScriptRoot ('outputs/' + $runId)
New-Item -ItemType Directory -Force $output | Out-Null
$oldProject = $env:COMPOSE_PROJECT_NAME
$oldMessaging = $env:TRIP_MESSAGING_ENABLED
$env:COMPOSE_PROJECT_NAME = $ProjectName
$env:TRIP_MESSAGING_ENABLED = 'true'
function Invoke-NativeLog([string]$Label, [string]$Executable, [string[]]$Arguments) {
    $savedPreference = $ErrorActionPreference
    try {
        # PowerShell 5.1 promotes native stderr to an ErrorRecord even on exit0.
        $ErrorActionPreference = 'Continue'
        & $Executable @Arguments *> (Join-Path $output "$Label.log")
        $nativeExit = $LASTEXITCODE
    } finally { $ErrorActionPreference = $savedPreference }
    return $nativeExit
}
function Run-Checked([string]$Label, [string]$Executable, [string[]]$Arguments) {
    $nativeExit = Invoke-NativeLog $Label $Executable $Arguments
    if ($nativeExit -ne 0) { throw "$Label failed; inspect $output" }
    Write-Host "PASS: $Label"
}
try {
    $testDirectory = '/work/evidence/member-b/final/freeze/outputs/' + $runId + '/tests'
    $sdk = 'dotnet restore RideHailing.Distributed.slnx && dotnet build RideHailing.Distributed.slnx -c Release --no-restore && dotnet test RideHailing.Distributed.slnx -c Release --no-build -m:1 --logger trx --results-directory ' + $testDirectory + ' && dotnet build scripts/test/w10-g3-harness/RideHailing.W10Harness.csproj -c Release'
    Run-Checked 'regression' 'docker' @('run','--rm','--name','rh-b-freeze-regression','-v',"${root}:/work",'-v','/var/run/docker.sock:/var/run/docker.sock','-e','TESTCONTAINERS_HOST_OVERRIDE=host.docker.internal','-e','B12_AUDIT_OUTPUT_DIR=/work/TestResults/b12-audit','-w','/work','mcr.microsoft.com/dotnet/sdk:10.0','bash','-lc',$sdk)
    $passed = 0
    $projectPasses = @{}
    foreach ($trx in Get-ChildItem (Join-Path $output 'tests') -Filter '*.trx') {
        [xml]$result = Get-Content -Raw $trx.FullName
        $counters = $result.SelectSingleNode("//*[local-name()='Counters']")
        if ([int]$counters.failed -gt 0) { throw 'Failed regression case in TRX.' }
        $passed += [int]$counters.passed
        $method = $result.SelectSingleNode("//*[local-name()='TestMethod']")
        if ($method) { $projectPasses[[IO.Path]::GetFileNameWithoutExtension($method.codeBase)] = [int]$counters.passed }
    }
    if ($passed -ne 1223) { throw "Expected 1223 frozen regression passes, observed $passed. Inspect discovery and source." }
    foreach ($entry in @{'RideHailing.DriverService.UnitTests'=131;'RideHailing.DriverService.IntegrationTests'=118;'RideHailing.MatchingService.UnitTests'=115;'RideHailing.MatchingService.IntegrationTests'=200}.GetEnumerator()) {
        if ($projectPasses[$entry.Key] -ne $entry.Value) { throw "Unexpected discovery in $($entry.Key)." }
    }
    Run-Checked 'claims' 'python' @('scripts/evidence/validate-driver-matching-claims.py','--output',"$output/claims.json")
    Run-Checked 'auth' 'python' @('scripts/security/init-local-auth.py')
    Run-Checked 'compose' 'docker' @('compose','config','--quiet')
    Run-Checked 'infra' 'docker' @('compose','up','-d','--wait','postgres','redis','rabbitmq')
    $ready = $false
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        $tcpExit = Invoke-NativeLog 'postgres-tcp' 'docker' @('compose','exec','-T','postgres','pg_isready','-h','127.0.0.1')
        if ($tcpExit -eq 0) { $ready = $true; break }
        Start-Sleep -Seconds 1
    }
    if (-not $ready) { throw 'PostgreSQL final TCP listener never became ready.' }
    Run-Checked 'startup' 'docker' @('compose','--profile','obs','up','-d','--build','--wait')
    Run-Checked 'platform' 'python' @('scripts/test/platform-smoke.py')
    Run-Checked 'runtime' 'python' @('scripts/test/core-runtime-gate.py')
    Run-Checked 'security' 'python' @('scripts/test/security-gate.py','--identity-tests')
} finally {
    $teardownExit = Invoke-NativeLog 'teardown' 'docker' @('compose','--profile','obs','down','-v')
    $env:COMPOSE_PROJECT_NAME = $oldProject
    $env:TRIP_MESSAGING_ENABLED = $oldMessaging
    if ($teardownExit -ne 0) { throw 'Disposable project teardown failed.' }
}
