using System.Collections.Concurrent;
using System.Text.Json;
using Npgsql;
using RabbitMQ.Client;
using RideHailing.Messaging.Contracts;
using RideHailing.Messaging.Dedup;
using RideHailing.Messaging.Outbox;
using RideHailing.Messaging.Reliability;

namespace RideHailing.Platform.IntegrationTests;

[Collection(W9Collection.Name)]
public class W9OutboxRelayTests(RabbitBackboneFixture fixture)
{
    [Fact]
    public async Task RelayPublishesConfirmedMessageThenMarksPublished()
    {
        await PrepareAsync();
        var queue = await fixture.QueueAsync();
        var envelope = Message(NewId());
        var row = OutboxMessage.Create(envelope, "ride.commands", queue);
        await EnqueueAsync(fixture.Source, row);

        var store = new NpgsqlOutboxStore(fixture.Source);
        await using var publisher = new ConfirmedPublisher(fixture.Factory);
        var relay = new OutboxRelay(store, new RabbitOutboxPublisher(publisher), new OutboxRelayOptions
        {
            RelayId = "w9-relay",
            BatchSize = 10,
            ClaimLease = TimeSpan.FromMinutes(1),
        });

        Assert.Equal(1, await relay.RunOnceAsync());
        var stored = await store.LoadAsync(row.MessageId);
        Assert.NotNull(stored);
        Assert.Equal(OutboxStatus.Published, stored!.Status);
        Assert.NotNull(stored.PublishedAt);
        Assert.Null(stored.ClaimedBy);

        await using var connection = await fixture.Factory.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        BasicGetResult? delivery = null;
        await W9TestSupport.EventuallyAsync(
            async () => (delivery = await channel.BasicGetAsync(queue, false)) is not null,
            "confirmed Outbox delivery");
        Assert.Equal(row.MessageId, delivery!.BasicProperties.MessageId);
        Assert.Equal(row.CorrelationId, delivery.BasicProperties.CorrelationId);
        var wire = JsonSerializer.Deserialize<MessageEnvelope<JsonElement>>(delivery.Body.Span)!;
        Assert.Equal(row.MessageId, wire.MessageId);
        Assert.Equal(row.CorrelationId, wire.CorrelationId);
        Assert.Equal(row.MessageId, wire.Payload.GetProperty("messageId").GetString());
        await channel.BasicAckAsync(delivery.DeliveryTag, false);
    }

    [Fact]
    public async Task PublishAcceptedThenMarkLostRepublishesSameIdentityAndDedupKeepsOneEffect()
    {
        await PrepareAsync();
        var queue = await fixture.QueueAsync();
        var envelope = Message(NewId());
        var row = OutboxMessage.Create(envelope, "ride.commands", queue);
        await EnqueueAsync(fixture.Source, row);

        var store = new NpgsqlOutboxStore(fixture.Source);
        var dedup = new ProcessedMessageStore(fixture.Source);
        var scope = "w9-rel05-" + Guid.NewGuid().ToString("N");
        var deliveries = 0;
        var messageIds = new ConcurrentBag<string>();
        await using var publisher = new ConfirmedPublisher(fixture.Factory);
        await using var consumer = await ManualAckConsumer.StartAsync(fixture.Factory, publisher, queue,
            new HashSet<string> { "W9OutboxProofV1" }, async (delivered, token) =>
            {
                Interlocked.Increment(ref deliveries);
                messageIds.Add(delivered.MessageId);
                await dedup.ExecuteOnceAsync(scope, delivered.MessageId, delivered.MessageType,
                    (connection, transaction, ct) => InsertEffectAsync(connection, transaction, scope,
                        delivered.MessageId, delivered.CorrelationId, ct), token);
            }, prefetch: 1);

        var relayA = new OutboxRelay(store, new RabbitOutboxPublisher(publisher), new OutboxRelayOptions
        {
            RelayId = "relay-a",
            ClaimLease = TimeSpan.Zero,
            AfterPublishBeforeMark = (_, _) => Task.FromException(new SimulatedCrashException()),
        });
        await Assert.ThrowsAsync<SimulatedCrashException>(() => relayA.RunOnceAsync());

        await W9TestSupport.EventuallyAsync(async () =>
                Volatile.Read(ref deliveries) >= 1 &&
                await EffectCountAsync(fixture.Source, scope, row.MessageId) == 1,
            "first effect after accepted publish");
        var afterCrash = await store.LoadAsync(row.MessageId);
        Assert.NotNull(afterCrash);
        Assert.Equal(OutboxStatus.Pending, afterCrash!.Status);
        Assert.Equal("relay-a", afterCrash.ClaimedBy);
        Assert.Null(afterCrash.PublishedAt);
        await W9TestSupport.EventuallyAsync(() => Task.FromResult(consumer.AcknowledgedCount == 1),
            "first delivery ACK");

        var relayB = new OutboxRelay(store, new RabbitOutboxPublisher(publisher), new OutboxRelayOptions
        {
            RelayId = "relay-b",
            ClaimLease = TimeSpan.Zero,
        });
        Assert.Equal(1, await relayB.RunOnceAsync());

        await W9TestSupport.EventuallyAsync(async () =>
                Volatile.Read(ref deliveries) >= 2 &&
                await EffectCountAsync(fixture.Source, scope, row.MessageId) == 1,
            "second delivery deduped");
        await W9TestSupport.EventuallyAsync(() => Task.FromResult(consumer.AcknowledgedCount >= 2),
            "second delivery ACK");

        var final = await store.LoadAsync(row.MessageId);
        Assert.Equal(OutboxStatus.Published, final!.Status);
        Assert.Equal(1, await EffectCountAsync(fixture.Source, scope, row.MessageId));
        Assert.Single(messageIds.Distinct());
        Assert.Equal(row.MessageId, messageIds.Distinct().Single());
    }

    [Fact]
    public async Task RelayRestartReclaimsClaimedPendingRowWithoutSilentLoss()
    {
        await PrepareAsync();
        var queue = await fixture.QueueAsync();
        var envelope = Message(NewId());
        var row = OutboxMessage.Create(envelope, "ride.commands", queue);
        await EnqueueAsync(fixture.Source, row);

        var store = new NpgsqlOutboxStore(fixture.Source);
        await using var publisher = new ConfirmedPublisher(fixture.Factory);
        var relayA = new OutboxRelay(store, new RabbitOutboxPublisher(publisher), new OutboxRelayOptions
        {
            RelayId = "relay-a",
            ClaimLease = TimeSpan.Zero,
            BeforePublish = (_, _) => Task.FromException(new SimulatedCrashException()),
        });
        await Assert.ThrowsAsync<SimulatedCrashException>(() => relayA.RunOnceAsync());

        var afterCrash = await store.LoadAsync(row.MessageId);
        Assert.NotNull(afterCrash);
        Assert.Equal(OutboxStatus.Pending, afterCrash!.Status);
        Assert.Equal("relay-a", afterCrash.ClaimedBy);
        Assert.Null(afterCrash.PublishedAt);

        var dedup = new ProcessedMessageStore(fixture.Source);
        var scope = "w9-rel18-" + Guid.NewGuid().ToString("N");
        await using var consumer = await ManualAckConsumer.StartAsync(fixture.Factory, publisher, queue,
            new HashSet<string> { "W9OutboxProofV1" }, async (delivered, token) =>
            {
                await dedup.ExecuteOnceAsync(scope, delivered.MessageId, delivered.MessageType,
                    (connection, transaction, ct) => InsertEffectAsync(connection, transaction, scope,
                        delivered.MessageId, delivered.CorrelationId, ct), token);
            }, prefetch: 1);

        var relayB = new OutboxRelay(store, new RabbitOutboxPublisher(publisher), new OutboxRelayOptions
        {
            RelayId = "relay-b",
            ClaimLease = TimeSpan.Zero,
        });
        Assert.Equal(1, await relayB.RunOnceAsync());
        await W9TestSupport.EventuallyAsync(() => Task.FromResult(consumer.AcknowledgedCount == 1),
            "restarted relay delivery ACK");

        var final = await store.LoadAsync(row.MessageId);
        Assert.Equal(OutboxStatus.Published, final!.Status);
        Assert.Equal(1, await EffectCountAsync(fixture.Source, scope, row.MessageId));
    }

    [Fact]
    public async Task BusinessEffectAndOutboxIntentShareTheCallerTransaction()
    {
        await PrepareAsync();
        var store = new NpgsqlOutboxStore(fixture.Source);
        var scope = "w9-outbox-atomic-" + Guid.NewGuid().ToString("N");
        var row = OutboxMessage.Create(Message(NewId()), "ride.commands", "unused.queue");

        await using (var connection = await fixture.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await InsertEffectAsync(connection, transaction, scope, row.MessageId, row.CorrelationId,
                CancellationToken.None);
            Assert.True(await store.EnqueueAsync(connection, transaction, row));
            await transaction.RollbackAsync();
        }

        Assert.Null(await store.LoadAsync(row.MessageId));
        Assert.Equal(0, await W9TestSupport.ScalarAsync(fixture.Source,
            "SELECT count(*) FROM w9_effects WHERE consumer_scope=@scope AND message_id=@message_id",
            CancellationToken.None, ("scope", scope), ("message_id", row.MessageId)));
    }

    [Fact]
    public async Task ConcurrentRelaysClaimDisjointBatches()
    {
        await PrepareAsync();
        var queue = await fixture.QueueAsync();
        var ids = Enumerable.Range(0, 20).Select(_ => NewId()).ToArray();
        foreach (var id in ids)
            await EnqueueAsync(fixture.Source, OutboxMessage.Create(Message(id), "ride.commands", queue));

        var store = new NpgsqlOutboxStore(fixture.Source);
        var now = DateTimeOffset.UtcNow.AddMilliseconds(1);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(1, 3).Select(async relay =>
        {
            await start.Task;
            return await store.ClaimBatchAsync($"relay-{relay}", 8, TimeSpan.FromMinutes(1), now);
        }).ToArray();
        start.SetResult();
        var batches = await Task.WhenAll(tasks);

        Assert.Equal(new[] { 4, 8, 8 }, batches.Select(batch => batch.Count).OrderBy(count => count));
        var claimed = batches.SelectMany(batch => batch).ToList();
        Assert.Equal(20, claimed.Select(row => row.MessageId).Distinct().Count());
        for (var index = 0; index < batches.Length; index++)
            Assert.All(batches[index], row => Assert.Equal($"relay-{index + 1}", row.ClaimedBy));
        Assert.All(claimed, row => Assert.NotNull(row.ClaimedAt));
    }

    private async Task PrepareAsync()
    {
        await W9TestSupport.EnsureSchemaAsync(fixture.Source);
        await W9TestSupport.TruncateOutboxAsync(fixture.Source);
    }

    private static string NewId() => Guid.NewGuid().ToString("N");

    private static MessageEnvelope<JsonElement> Message(string messageId) =>
        MessageEnvelope<JsonElement>.Create("W9OutboxProofV1", "correlation-" + messageId,
            JsonSerializer.SerializeToElement(new { messageId }), messageId: messageId);

    private static async Task EnqueueAsync(NpgsqlDataSource source, OutboxMessage row)
    {
        var store = new NpgsqlOutboxStore(source);
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        Assert.True(await store.EnqueueAsync(connection, transaction, row));
        await transaction.CommitAsync();
    }

    private static async Task InsertEffectAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string scope, string messageId, string correlationId, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO w9_effects (consumer_scope, message_id, correlation_id, created_at)
            VALUES (@scope, @message_id, @correlation_id, @created_at)
            ON CONFLICT DO NOTHING;
            """, connection, transaction);
        command.Parameters.AddWithValue("scope", scope);
        command.Parameters.AddWithValue("message_id", messageId);
        command.Parameters.AddWithValue("correlation_id", correlationId);
        command.Parameters.AddWithValue("created_at", DateTimeOffset.UtcNow);
        await command.ExecuteNonQueryAsync(token);
    }

    private static Task<long> EffectCountAsync(NpgsqlDataSource source, string scope, string messageId) =>
        W9TestSupport.ScalarAsync(source,
            "SELECT count(*) FROM w9_effects WHERE consumer_scope=@scope AND message_id=@message_id",
            CancellationToken.None, ("scope", scope), ("message_id", messageId));

    private sealed class SimulatedCrashException : Exception;
}
