# Shared helpers for the guarded RabbitMQ HA harnesses.
#
# The scripts deliberately use the RabbitMQ CLI's JSON formatter instead of
# scraping human-readable tables: node names and raft states then have stable,
# machine-checkable fields.

function Invoke-ComposeCommand {
    param(
        [Parameter(Mandatory = $true)][string[]]$Compose,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [switch]$AllowFailure
    )

    $previousErrorPreference = $ErrorActionPreference
    try {
        # PowerShell 5 wraps native stderr as ErrorRecord even for readiness
        # probes. Inspect the actual native exit code below in every case.
        $ErrorActionPreference = 'Continue'
        $output = & docker @Compose @Arguments 2>&1 | Out-String
        $exitCode = $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $previousErrorPreference }
    if (-not $AllowFailure -and $exitCode -ne 0) {
        throw "docker compose $($Arguments -join ' ') failed with exit code $exitCode`n$output"
    }

    return [pscustomobject]@{ ExitCode = $exitCode; Output = $output }
}

function Get-QuorumStatusRows {
    param(
        [Parameter(Mandatory = $true)][string[]]$Compose,
        [Parameter(Mandatory = $true)][string]$Queue,
        [string]$ControlService = 'rabbitmq'
    )

    $result = Invoke-ComposeCommand -Compose $Compose -Arguments @(
        'exec', '-T', $ControlService, 'rabbitmq-queues', '--formatter', 'json',
        'quorum_status', $Queue)
    try {
        $rows = @($result.Output | ConvertFrom-Json)
    }
    catch {
        throw "Could not parse quorum_status JSON for $Queue`n$($result.Output)"
    }
    if ($rows.Count -eq 0) {
        throw "quorum_status returned no rows for $Queue"
    }
    return $rows
}

function Get-OnlineQuorumMembers {
    param(
        [Parameter(Mandatory = $true)][string[]]$Compose,
        [Parameter(Mandatory = $true)][string]$Queue,
        [string]$ControlService = 'rabbitmq'
    )

    $rows = Get-QuorumStatusRows -Compose $Compose -Queue $Queue -ControlService $ControlService
    $online = @($rows | Where-Object { $_.'Raft State' -in @('leader', 'follower') })
    return [pscustomobject]@{
        Queue = $Queue
        Online = $online.Count
        Total = $rows.Count
        Rows = $rows
    }
}

function Get-QuorumQueueNames {
    param([Parameter(Mandatory = $true)][string[]]$Compose)

    $result = Invoke-ComposeCommand -Compose $Compose -Arguments @(
        'exec', '-T', 'rabbitmq', 'rabbitmqctl', '--formatter', 'json',
        'list_queues', 'name', 'type')
    try {
        $rows = @($result.Output | ConvertFrom-Json)
    }
    catch {
        throw "Could not parse list_queues JSON`n$($result.Output)"
    }
    return @($rows | Where-Object { $_.type -eq 'quorum' } | ForEach-Object { [string]$_.name })
}

function Wait-ForQuorumOnline {
    param(
        [Parameter(Mandatory = $true)][string[]]$Compose,
        [Parameter(Mandatory = $true)][string]$Queue,
        [Parameter(Mandatory = $true)][int]$Online,
        [string]$ControlService = 'rabbitmq',
        [int]$TimeoutSeconds = 180
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $last = $null
    do {
        try {
            $last = Get-OnlineQuorumMembers -Compose $Compose -Queue $Queue `
                -ControlService $ControlService
        }
        catch { $last = $null }
        if ($null -ne $last -and $last.Online -ge $Online) { return $last }
        if ((Get-Date) -le $deadline) { Start-Sleep -Seconds 2 }
    } until ((Get-Date) -gt $deadline)

    $observed = if ($null -eq $last) { 'no readable quorum status' } else { "$($last.Online)/$($last.Total) online" }
    throw "Queue $Queue did not reach $Online online members within $TimeoutSeconds s ($observed)."
}

function New-RabbitManagementHeaders {
    param(
        [Parameter(Mandatory = $true)][string]$Username,
        [Parameter(Mandatory = $true)][string]$Password
    )

    if ([string]::IsNullOrWhiteSpace($Username) -or [string]::IsNullOrWhiteSpace($Password)) {
        throw 'RabbitMQ management credentials are required.'
    }
    return @{
        Authorization = 'Basic ' + [Convert]::ToBase64String(
            [Text.Encoding]::UTF8.GetBytes("${Username}:${Password}"))
    }
}

function Invoke-RabbitManagementApi {
    param(
        [Parameter(Mandatory = $true)][string]$ManagementUrl,
        [Parameter(Mandatory = $true)][hashtable]$Headers,
        [Parameter(Mandatory = $true)][string]$Path,
        [ValidateSet('Get', 'Post')][string]$Method = 'Get',
        [string]$Body
    )

    $parameters = @{
        Method = $Method
        Uri = "$($ManagementUrl.TrimEnd('/'))$Path"
        Headers = $Headers
        TimeoutSec = 10
    }
    if ($Method -eq 'Post') {
        $parameters.ContentType = 'application/json'
        $parameters.Body = $Body
    }
    try {
        return Invoke-RestMethod @parameters
    }
    catch {
        throw "RabbitMQ management API request failed at $ManagementUrl$Path`n$($_.Exception.Message)"
    }
}

function Wait-RabbitManagementApi {
    param(
        [Parameter(Mandatory = $true)][string]$ManagementUrl,
        [Parameter(Mandatory = $true)][hashtable]$Headers,
        [int]$TimeoutSeconds = 90
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        try {
            Invoke-RabbitManagementApi -ManagementUrl $ManagementUrl -Headers $Headers `
                -Path '/api/overview' | Out-Null
            return
        }
        catch {
            if ((Get-Date) -le $deadline) { Start-Sleep -Seconds 2 }
        }
    } until ((Get-Date) -gt $deadline)
    throw "RabbitMQ management API did not become ready at $ManagementUrl."
}

function Get-PaymentQueueState {
    param(
        [Parameter(Mandatory = $true)][string]$ManagementUrl,
        [Parameter(Mandatory = $true)][hashtable]$Headers
    )

    $queue = Invoke-RabbitManagementApi -ManagementUrl $ManagementUrl -Headers $Headers `
        -Path '/api/queues/%2F/payment.commands.q'
    return [pscustomobject]@{
        Consumers = [int]$queue.consumers
        Ready = [int]$queue.messages_ready
        Unacked = [int]$queue.messages_unacknowledged
    }
}

function Publish-PaymentProbeBatch {
    param(
        [Parameter(Mandatory = $true)][string]$ManagementUrl,
        [Parameter(Mandatory = $true)][hashtable]$Headers,
        [Parameter(Mandatory = $true)][ValidatePattern('^[a-z0-9-]+$')][string]$RunId,
        [Parameter(Mandatory = $true)][ValidateRange(1, 200)][int]$Count,
        [ValidateRange(1, 3)][int]$Copies = 2
    )

    $routed = 0
    for ($index = 0; $index -lt $Count; $index++) {
        $tripId = [Guid]::NewGuid().ToString('D')
        $messageId = "${RunId}-message-$($index.ToString('D3'))"
        $operationKey = "${RunId}:authorize:$tripId"
        $envelope = [ordered]@{
            messageId = $messageId
            messageType = 'AuthorizePaymentV1'
            schemaVersion = 1
            occurredAt = [DateTimeOffset]::UtcNow.ToString('O')
            correlationId = $RunId
            causationId = $null
            payload = [ordered]@{
                tripId = $tripId
                operationKey = $operationKey
                amountMinor = 10000 + $index
                currency = 'VND'
                paymentType = 0
            }
            aggregateType = 'Trip'
            aggregateId = $tripId
            aggregateVersion = $null
            sagaId = "${RunId}-saga-$($index.ToString('D3'))"
            operationKey = $operationKey
        }
        $payload = $envelope | ConvertTo-Json -Depth 10 -Compress
        $request = [ordered]@{
            properties = [ordered]@{
                content_type = 'application/json'
                delivery_mode = 2
                message_id = $messageId
                correlation_id = $RunId
                type = 'AuthorizePaymentV1'
            }
            routing_key = 'payment.authorize.v1'
            payload = $payload
            payload_encoding = 'string'
        } | ConvertTo-Json -Depth 10 -Compress

        for ($copy = 0; $copy -lt $Copies; $copy++) {
            $response = Invoke-RabbitManagementApi -ManagementUrl $ManagementUrl -Headers $Headers `
                -Path '/api/exchanges/%2F/ride.commands/publish' -Method Post -Body $request
            if (-not $response.routed) {
                throw "Probe $messageId copy $copy was not routed to payment.commands.q."
            }
            $routed++
        }
    }

    return [pscustomobject]@{
        RunId = $RunId
        UniqueCommands = $Count
        Copies = $Copies
        RoutedDeliveries = $routed
    }
}

function Get-PaymentProbeState {
    param(
        [Parameter(Mandatory = $true)][string[]]$Compose,
        [Parameter(Mandatory = $true)][ValidatePattern('^[a-z0-9-]+$')][string]$RunId
    )

    $sql = @"
SELECT json_build_object(
  'operations', (SELECT count(*) FROM payment_operations WHERE operation_key LIKE '${RunId}:%'),
  'succeeded', (SELECT count(*) FROM payment_operations WHERE operation_key LIKE '${RunId}:%' AND state = 'SUCCEEDED'),
  'providerEffects', (SELECT count(*) FROM provider_effects WHERE operation_key LIKE '${RunId}:%'),
  'processedMessages', (SELECT count(*) FROM processed_messages WHERE consumer_scope = 'payment.commands.v1' AND message_id LIKE '${RunId}-message-%'),
  'outboxMessages', (SELECT count(*) FROM outbox_messages WHERE operation_key LIKE '${RunId}:%'),
  'outboxPublished', (SELECT count(*) FROM outbox_messages WHERE operation_key LIKE '${RunId}:%' AND status = 'PUBLISHED'),
  'outboxFailedVisible', (SELECT count(*) FROM outbox_messages WHERE operation_key LIKE '${RunId}:%' AND status = 'FAILED_VISIBLE')
);
"@
    $result = Invoke-ComposeCommand -Compose $Compose -Arguments @(
        'exec', '-T', '-e', 'PGOPTIONS=-c default_transaction_read_only=on', 'postgres', 'psql', '-X', '-q', '-A', '-t',
        '-v', 'ON_ERROR_STOP=1', '-U', 'postgres', '-d', 'payment_db', '-c', $sql)
    $json = @($result.Output -split "`r?`n" | Where-Object { $_.TrimStart().StartsWith('{') }) | Select-Object -Last 1
    if ([string]::IsNullOrWhiteSpace($json)) {
        throw "Payment evidence query returned no JSON for run $RunId.`n$($result.Output)"
    }
    try { return $json | ConvertFrom-Json }
    catch { throw "Payment evidence JSON could not be parsed for run $RunId.`n$json" }
}

function Wait-PaymentProbeConvergence {
    param(
        [Parameter(Mandatory = $true)][string[]]$Compose,
        [Parameter(Mandatory = $true)][ValidatePattern('^[a-z0-9-]+$')][string]$RunId,
        [Parameter(Mandatory = $true)][ValidateRange(1, 200)][int]$Expected,
        [int]$TimeoutSeconds = 180
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $last = $null
    do {
        try { $last = Get-PaymentProbeState -Compose $Compose -RunId $RunId }
        catch { $last = $null }
        if ($null -ne $last) {
            $counts = @(
                [int]$last.operations, [int]$last.succeeded, [int]$last.providerEffects,
                [int]$last.processedMessages, [int]$last.outboxMessages, [int]$last.outboxPublished)
            if (@($counts | Where-Object { $_ -gt $Expected }).Count -gt 0) {
                throw "Probe $RunId exceeded its expected unique-effect count ${Expected}: $($last | ConvertTo-Json -Compress)"
            }
            $complete = @($counts | Where-Object { $_ -ne $Expected }).Count -eq 0 -and
                [int]$last.outboxFailedVisible -eq 0
            if ($complete) { return $last }
        }
        if ((Get-Date) -le $deadline) { Start-Sleep -Seconds 2 }
    } until ((Get-Date) -gt $deadline)

    $observed = if ($null -eq $last) { '<unreadable>' } else { $last | ConvertTo-Json -Compress }
    throw "Payment probe $RunId did not converge to exactly $Expected effects within $TimeoutSeconds s: $observed"
}
