param(
    [string]$PostgresContainer = "ride-hailing-postgres-1",
    [string]$WslDistro = "CodexDocker",
    [ValidateRange(5, 120)]
    [int]$ObservationWaitSeconds = 18
)

$ErrorActionPreference = "Stop"

function Invoke-DockerPsql {
    param([Parameter(Mandatory)] [string]$Sql)

    $docker = Get-Command docker -ErrorAction SilentlyContinue
    if ($docker) {
        try {
            $null = & docker info 2>$null
            if ($LASTEXITCODE -eq 0) {
                $Sql | & docker exec -i $PostgresContainer psql -v ON_ERROR_STOP=1 -U postgres -d trip_db
                if ($LASTEXITCODE -ne 0) { throw "psql failed with exit code $LASTEXITCODE." }
                return
            }
        }
        catch { }
    }

    $wsl = Get-Command wsl -ErrorAction SilentlyContinue
    if (-not $wsl) { throw "Docker is unavailable and WSL was not found." }
    $Sql | & wsl -d $WslDistro -- docker exec -i $PostgresContainer `
        psql -v ON_ERROR_STOP=1 -U postgres -d trip_db
    if ($LASTEXITCODE -ne 0) { throw "WSL Docker psql failed with exit code $LASTEXITCODE." }
}

$rows = @(
    @{ State = "DRIVER_ACCEPTED"; SagaStatus = "ACTIVE"; Step = "AUTHORIZE_PENDING"; Error = $null; Review = $null },
    @{ State = "READY_TO_START"; SagaStatus = "ACTIVE"; Step = "AUTHORIZED"; Error = $null; Review = $null },
    @{ State = "COMPLETED"; SagaStatus = "ACTIVE"; Step = "CAPTURE_PENDING"; Error = $null; Review = $null },
    @{ State = "CANCELLED"; SagaStatus = "COMPENSATING"; Step = "RELEASE_PENDING"; Error = "PAYMENT_DECLINED"; Review = $null },
    @{ State = "COMPLETED"; SagaStatus = "REQUIRES_REVIEW"; Step = "CAPTURE_PENDING"; Error = "CAPTURE_UNKNOWN"; Review = "Provider outcome remains unknown after bounded reconciliation." },
    @{ State = "COMPLETED"; SagaStatus = "COMPLETED"; Step = "CAPTURED"; Error = $null; Review = $null }
)

$tripValues = [System.Collections.Generic.List[string]]::new()
$sagaValues = [System.Collections.Generic.List[string]]::new()
$summary = [System.Collections.Generic.List[object]]::new()

foreach ($row in $rows) {
    $tripId = [Guid]::NewGuid()
    $riderId = [Guid]::NewGuid()
    $driverId = [Guid]::NewGuid()
    $reservationId = [Guid]::NewGuid()
    $sagaId = [Guid]::NewGuid()
    $tripText = $tripId.ToString("D")
    $authorizeKey = "${tripText}:authorize:v1"
    $captureKey = if ($row.Step -in @("CAPTURE_PENDING", "CAPTURED")) { "${tripText}:capture:v1" } else { $null }
    $voidKey = if ($row.Step -eq "VOID_PENDING") { "${tripText}:void:v1" } else { $null }
    $captureSql = if ($captureKey) { "'$captureKey'" } else { "NULL" }
    $voidSql = if ($voidKey) { "'$voidKey'" } else { "NULL" }
    $errorSql = if ($row.Error) { "'$($row.Error)'" } else { "NULL" }
    $reviewSql = if ($row.Review) { "'$($row.Review.Replace("'", "''"))'" } else { "NULL" }

    $tripValues.Add("('$tripText','$($riderId.ToString("D"))','$($row.State)',now(),1,'$($driverId.ToString("D"))',1,'$($reservationId.ToString("D"))',10.776,106.700,10.801,106.714,75000,'VND','member-a-demo',now(),1,now())")
    $sagaValues.Add("('$($sagaId.ToString("D"))','$tripText','$($row.SagaStatus)','$($row.Step)',1,'$($reservationId.ToString("D"))','$authorizeKey',$captureSql,$voidSql,$errorSql,$reviewSql,now(),now())")
    $summary.Add([pscustomobject]@{ TripId = $tripText; TripState = $row.State; SagaStatus = $row.SagaStatus; SagaStep = $row.Step })
}

$sql = @"
BEGIN;
DELETE FROM ride_sagas WHERE trip_id IN (SELECT trip_id FROM trips WHERE fare_policy_version = 'member-a-demo');
DELETE FROM request_idempotency WHERE resource_id IN (SELECT trip_id FROM trips WHERE fare_policy_version = 'member-a-demo');
DELETE FROM trips WHERE fare_policy_version = 'member-a-demo';
INSERT INTO trips
    (trip_id,rider_id,state,created_at,version,assigned_driver_id,matching_round,reservation_id,
     pickup_latitude,pickup_longitude,dropoff_latitude,dropoff_longitude,fare_amount,fare_currency,
     fare_policy_version,fare_created_at,current_matching_round,updated_at)
VALUES
$($tripValues -join ",`n");
INSERT INTO ride_sagas
    (saga_id,trip_id,status,current_step,step_version,reservation_id,authorize_operation_key,
     capture_operation_key,void_operation_key,last_error_code,requires_review_reason,created_at,updated_at)
VALUES
$($sagaValues -join ",`n");
COMMIT;
"@

Invoke-DockerPsql -Sql $sql

Write-Host "`nMember A mock workflow rows:" -ForegroundColor Cyan
$summary | Format-Table -AutoSize
Write-Host "Waiting $ObservationWaitSeconds seconds for the durable Saga observer and Prometheus scrape..."
Start-Sleep -Seconds $ObservationWaitSeconds

try {
    $query = [Uri]::EscapeDataString('sum by (step) ({__name__=~"saga_active(_sagas)?"})')
    $result = Invoke-RestMethod "http://localhost:9090/api/v1/query?query=$query" -TimeoutSec 10
    Write-Host "`nPrometheus saga.active snapshot:" -ForegroundColor Cyan
    $result.data.result | ForEach-Object {
        [pscustomobject]@{ Step = $_.metric.step; Active = $_.value[1] }
    } | Format-Table -AutoSize
}
catch {
    Write-Warning "Mock rows were committed, but Prometheus was not reachable: $($_.Exception.Message)"
}

Write-Host "`nOpen: http://localhost:3000/d/ride-trip-saga/trip-saga-and-outbox?orgId=1&from=now-1h&to=now" -ForegroundColor Green
Write-Host "These are explicitly labelled mock rows. Real state transitions and crash windows are exercised by TripW09/W10/W11/W12 integration suites."
