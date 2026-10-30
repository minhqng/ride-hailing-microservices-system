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

    $output = & docker @Compose @Arguments 2>&1 | Out-String
    $exitCode = $LASTEXITCODE
    if (-not $AllowFailure -and $exitCode -ne 0) {
        throw "docker compose $($Arguments -join ' ') failed with exit code $exitCode`n$output"
    }

    return [pscustomobject]@{ ExitCode = $exitCode; Output = $output }
}

function Get-QuorumStatusRows {
    param(
        [Parameter(Mandatory = $true)][string[]]$Compose,
        [Parameter(Mandatory = $true)][string]$Queue
    )

    $result = Invoke-ComposeCommand -Compose $Compose -Arguments @(
        'exec', '-T', 'rabbitmq', 'rabbitmq-queues', '--formatter', 'json',
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
        [Parameter(Mandatory = $true)][string]$Queue
    )

    $rows = Get-QuorumStatusRows -Compose $Compose -Queue $Queue
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
        [int]$TimeoutSeconds = 180
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $last = $null
    do {
        try { $last = Get-OnlineQuorumMembers -Compose $Compose -Queue $Queue }
        catch { $last = $null }
        if ($null -ne $last -and $last.Online -ge $Online) { return $last }
        if ((Get-Date) -le $deadline) { Start-Sleep -Seconds 2 }
    } until ((Get-Date) -gt $deadline)

    $observed = if ($null -eq $last) { 'no readable quorum status' } else { "$($last.Online)/$($last.Total) online" }
    throw "Queue $Queue did not reach $Online online members within $TimeoutSeconds s ($observed)."
}
