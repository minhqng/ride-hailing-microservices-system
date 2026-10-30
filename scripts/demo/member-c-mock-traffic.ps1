param(
    [ValidateRange(3, 300)]
    [int]$Count = 24,
    [string]$RabbitApi = "http://localhost:15672",
    [string]$Username = $(if ($env:RABBITMQ_USER) { $env:RABBITMQ_USER } else { "ride_app" }),
    [string]$Password = $(if ($env:RABBITMQ_PASSWORD) { $env:RABBITMQ_PASSWORD } else { "ride_dev_pw" })
)

$ErrorActionPreference = "Stop"
$runId = "demo-$([DateTimeOffset]::UtcNow.ToString('yyyyMMddHHmmss'))"
$credentialBytes = [Text.Encoding]::ASCII.GetBytes("${Username}:${Password}")
$headers = @{ Authorization = "Basic $([Convert]::ToBase64String($credentialBytes))" }
$publishUri = "$($RabbitApi.TrimEnd('/'))/api/exchanges/%2F/ride.commands/publish"
$routed = 0
$duplicates = 0

function Publish-Envelope {
    param(
        [Parameter(Mandatory)] [string]$RoutingKey,
        [Parameter(Mandatory)] [hashtable]$Envelope
    )

    $payload = $Envelope | ConvertTo-Json -Depth 10 -Compress
    $request = @{
        properties = @{
            content_type = "application/json"
            delivery_mode = 2
            message_id = $Envelope.messageId
            correlation_id = $Envelope.correlationId
            type = $Envelope.messageType
        }
        routing_key = $RoutingKey
        payload = $payload
        payload_encoding = "string"
    } | ConvertTo-Json -Depth 10 -Compress

    $response = Invoke-RestMethod -Method Post -Uri $publishUri -Headers $headers `
        -ContentType "application/json" -Body $request -TimeoutSec 10
    if (-not $response.routed) {
        throw "RabbitMQ did not route $($Envelope.messageType) message $($Envelope.messageId)."
    }
}

$published = [System.Collections.Generic.List[object]]::new()
for ($index = 0; $index -lt $Count; $index++) {
    $tripId = [Guid]::NewGuid()
    $messageId = "$runId-$($index.ToString('D3'))"
    $correlationId = "$runId-correlation-$($index.ToString('D3'))"
    $amountMinor = 12000 + $index

    switch ($index % 3) {
        0 {
            $messageType = "AuthorizePaymentV1"
            $routingKey = "payment.authorize.v1"
            $operationKey = "${runId}:authorize:$($tripId.ToString('N'))"
            $payload = @{
                tripId = $tripId
                operationKey = $operationKey
                amountMinor = $amountMinor
                currency = "VND"
                paymentType = 0
            }
        }
        1 {
            $messageType = "CapturePaymentV1"
            $routingKey = "payment.capture.v1"
            $operationKey = "${runId}:capture:$($tripId.ToString('N'))"
            $payload = @{
                tripId = $tripId
                operationKey = $operationKey
                amountMinor = $amountMinor
                currency = "VND"
            }
        }
        default {
            $messageType = "VoidPaymentAuthorizationV1"
            $routingKey = "payment.void-authorization.v1"
            $operationKey = "${runId}:void:$($tripId.ToString('N'))"
            $payload = @{
                tripId = $tripId
                operationKey = $operationKey
                reason = "W16_DEMO"
                amountMinor = $amountMinor
                currency = "VND"
            }
        }
    }

    $envelope = @{
        messageId = $messageId
        messageType = $messageType
        schemaVersion = 1
        occurredAt = [DateTimeOffset]::UtcNow.ToString("O")
        correlationId = $correlationId
        causationId = $null
        payload = $payload
        aggregateType = "Trip"
        aggregateId = $tripId.ToString("D")
        aggregateVersion = $null
        sagaId = "$runId-saga-$($index.ToString('D3'))"
        operationKey = $operationKey
    }

    Publish-Envelope -RoutingKey $routingKey -Envelope $envelope
    $routed++
    $published.Add([pscustomobject]@{
        MessageId = $messageId
        MessageType = $messageType
        TripId = $tripId
        OperationKey = $operationKey
    })

    # Exact transport replay: exercises processed-message dedup without
    # creating a second Payment effect or a second result Outbox message.
    if ($index % 6 -eq 0) {
        Publish-Envelope -RoutingKey $routingKey -Envelope $envelope
        $routed++
        $duplicates++
    }
}

[pscustomobject]@{
    RunId = $runId
    UniqueCommands = $Count
    ExactDuplicateDeliveries = $duplicates
    RoutedDeliveries = $routed
    FirstMessageId = $published[0].MessageId
    FirstTripId = $published[0].TripId
}
