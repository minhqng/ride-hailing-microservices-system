# W8 RabbitMQ backbone

`ConfirmedPublisher` maintains a connection/channel and serializes channel use. Publisher confirmation tracking is enabled and all sends are mandatory/persistent. Await publication before marking local acceptance; NACK, unroutable return, timeout and connection failure are visible exceptions. No automatic resend and no in-memory promise of durable buffering. A later explicit call can reconnect. The W9 Outbox must preserve the same messageId across uncertain publication.

`ManualAckConsumer.StartAsync` accepts an explicit queue, supported message types and a handler returning Task. Return from the handler **only after the local transaction commits**. The consumer validates V1 payloads/identities, extracts W3C context, bounds prefetch (1–256) and ACKs individually after durable handling. Separate publisher/consumer channels are used.

Transient failures must be expressed as `DeliveryFailureException(FailureKind.Transient, "SAFE_CODE")`. At most three republish retries retain the original messageId and increment `x-ride-attempt`; ACK follows confirmed transfer. Poison, permanent, UNKNOWN and exhausted failures go to the queue's durable quorum `<queue>.dead` error queue through `ride.deadletter`. UNKNOWN is visible for owner reconciliation, never a blind normal retry. Error envelopes omit raw payloads and stack traces. Broker outage during transfer leaves the original unacknowledged.

## Lifecycle integration

Create ConnectionFactory with explicit configured host/port/credentials, an instance-specific ClientProvidedName and AutomaticRecoveryEnabled=false. Keep publisher and consumer objects alive for the host lifetime. Declare RideTopology before consumption. Register the `RideHailing.Messaging` ActivitySource with OTel (platform bootstrap does this).

```csharp
await using var publisher = new ConfirmedPublisher(factory);
await using var consumer = await ManualAckConsumer.StartAsync(factory, publisher,
    "payment.commands.q", supportedTypes, CommitOwnedTransactionAsync,
    prefetch: 16, token: stoppingToken);
await consumer.Completion.WaitAsync(stoppingToken);
```

Broker cancellation (including queue deletion), channel shutdown and failed retry/error transfer fault `Completion` and stop further handling. The host must observe Completion, dispose on failure/shutdown, and restart with bounded backoff. Failed initial connect fails startup. Closed consumer connections release unacknowledged work for redelivery. Handler cancellation may occur after commit; W9 processed-message dedup must make repeated durable effects harmless. This library does not claim exactly-once delivery or implement A/B business handlers.

Reference: [RabbitMQ .NET client guide](https://www.rabbitmq.com/client-libraries/dotnet-api-guide). Confirms cover publisher-to-broker responsibility; consumer ACK is independent. Real tests: RabbitBackboneTests (confirm before any consumer; persisted commit before ACK; mandatory return; trace/ACK ordering; poison/retry exhaustion; connection loss and explicit reconnect).


## Broker delivery limit and quarantine

Compose applies `deploy/rabbitmq/reliability.json` through the broker readiness script after normal user/vhost bootstrap. The policy updates existing business quorum queues without deleting/redeclaring them: delivery limit 20, 64 MiB queue bound with reject-publish, and at-least-once dead-letter transfer to `ride.quarantine`. Readiness fails if policy import fails. When upgrading an existing Compose deployment, recreate the RabbitMQ container to mount these files; retain the `rabbitdata` volume.

`ride.quarantine.q` stores the original bytes/properties plus broker `x-death` headers when broker-level delivery failures exhaust the limit. Application `<queue>.dead` queues continue to store sanitized `DeliveryErrorV1` envelopes. Quarantine is an operator-only holding queue without an automatic replay consumer or expiry; its delivery limit is disabled to retain forensic evidence during repeated inspections. Monitor its depth/disk use and make an explicit remediation/replay decision while preserving messageId. It can contain sensitive original payloads: restrict broker access. Do not feed quarantined messages automatically back into business queues.

See [RabbitMQ quorum dead-letter guarantees](https://www.rabbitmq.com/docs/quorum-queues#dead-lettering). App retry metadata and broker delivery counts solve separate failure paths; both need bounded handling and visible failure destinations.
