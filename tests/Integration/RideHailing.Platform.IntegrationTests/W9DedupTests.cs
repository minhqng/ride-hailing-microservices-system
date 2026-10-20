using System.Text.Json;
using Npgsql;
using RideHailing.Messaging.Contracts;
using RideHailing.Messaging.Dedup;
using RideHailing.Messaging.Reliability;

namespace RideHailing.Platform.IntegrationTests;

[Collection(W9Collection.Name)]
public class W9DedupTests(RabbitBackboneFixture fixture)
{
    [Fact]
    public async Task MarkerAndBusinessEffectCommitTogether()
    {
        await W9TestSupport.EnsureSchemaAsync(fixture.Source);
        var store = new ProcessedMessageStore(fixture.Source);
        var scope = "w9-atomic-" + Guid.NewGuid().ToString("N");
        var messageId = Guid.NewGuid().ToString("N");
        var effectCalls = 0;

        var first = await store.ExecuteOnceAsync(scope, messageId, "W9DedupProofV1",
            (connection, transaction, token) =>
            {
                effectCalls++;
                return InsertEffectAsync(connection, transaction, scope, messageId, "correlation-1", token);
            });

        Assert.Equal(DedupOutcome.Applied, first);
        var second = await store.ExecuteOnceAsync(scope, messageId, "W9DedupProofV1",
            (_, _, _) =>
            {
                effectCalls++;
                return Task.CompletedTask;
            });

        Assert.Equal(DedupOutcome.Duplicate, second);
        Assert.Equal(1, effectCalls);
        Assert.Equal(1, await MarkerCountAsync(fixture.Source, scope, messageId));
        Assert.Equal(1, await EffectCountAsync(fixture.Source, scope, messageId));
        var marker = await store.LoadAsync(scope, messageId);
        Assert.NotNull(marker);
        Assert.Equal("W9DedupProofV1", marker!.MessageType);
    }

    [Fact]
    public async Task CallerOwnedTransactionKeepsMarkerInvisibleUntilCommit()
    {
        await W9TestSupport.EnsureSchemaAsync(fixture.Source);
        var store = new ProcessedMessageStore(fixture.Source);
        var scope = "w9-owner-" + Guid.NewGuid().ToString("N");
        var messageId = Guid.NewGuid().ToString("N");

        await using var connection = await fixture.Source.OpenConnectionAsync();
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            Assert.True(await store.TryMarkAsync(connection, transaction, scope, messageId,
                "W9DedupProofV1", DateTimeOffset.UtcNow));
            await InsertEffectAsync(connection, transaction, scope, messageId, "correlation-1", CancellationToken.None);

            // Nothing is visible to another connection before the owning service commits.
            Assert.Equal(0, await MarkerCountAsync(fixture.Source, scope, messageId));
            Assert.Equal(0, await EffectCountAsync(fixture.Source, scope, messageId));
            await transaction.CommitAsync();
        }

        Assert.Equal(1, await MarkerCountAsync(fixture.Source, scope, messageId));
        Assert.Equal(1, await EffectCountAsync(fixture.Source, scope, messageId));

        await using (var duplicate = await connection.BeginTransactionAsync())
        {
            Assert.False(await store.TryMarkAsync(connection, duplicate, scope, messageId,
                "W9DedupProofV1", DateTimeOffset.UtcNow));
            await duplicate.CommitAsync();
        }
    }

    [Fact]
    public async Task EffectFailureRollsBackMarkerAndAllowsCleanRetry()
    {
        await W9TestSupport.EnsureSchemaAsync(fixture.Source);
        var store = new ProcessedMessageStore(fixture.Source);
        var scope = "w9-rollback-" + Guid.NewGuid().ToString("N");
        var messageId = Guid.NewGuid().ToString("N");

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await store.ExecuteOnceAsync(scope, messageId, "W9DedupProofV1",
                async (connection, transaction, token) =>
                {
                    await InsertEffectAsync(connection, transaction, scope, messageId, "correlation-1", token);
                    throw new InvalidOperationException("effect failed");
                });
        });

        Assert.Equal(0, await MarkerCountAsync(fixture.Source, scope, messageId));
        Assert.Equal(0, await EffectCountAsync(fixture.Source, scope, messageId));

        var retry = await store.ExecuteOnceAsync(scope, messageId, "W9DedupProofV1",
            (connection, transaction, token) =>
                InsertEffectAsync(connection, transaction, scope, messageId, "correlation-1", token));
        Assert.Equal(DedupOutcome.Applied, retry);
        Assert.Equal(1, await MarkerCountAsync(fixture.Source, scope, messageId));
        Assert.Equal(1, await EffectCountAsync(fixture.Source, scope, messageId));
    }

    [Fact]
    public async Task ConcurrentDuplicatesApplyExactlyOnce()
    {
        await W9TestSupport.EnsureSchemaAsync(fixture.Source);
        var store = new ProcessedMessageStore(fixture.Source);
        var scope = "w9-concurrent-" + Guid.NewGuid().ToString("N");
        var messageId = Guid.NewGuid().ToString("N");

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ =>
            store.ExecuteOnceAsync(scope, messageId, "W9DedupProofV1",
                (connection, transaction, token) =>
                    InsertEffectAsync(connection, transaction, scope, messageId, "correlation-1", token))));

        Assert.Equal(1, outcomes.Count(outcome => outcome == DedupOutcome.Applied));
        Assert.Equal(11, outcomes.Count(outcome => outcome == DedupOutcome.Duplicate));
        Assert.Equal(1, await MarkerCountAsync(fixture.Source, scope, messageId));
        Assert.Equal(1, await EffectCountAsync(fixture.Source, scope, messageId));
    }

    [Fact]
    public async Task CommitBeforeAckRedeliveryFindsCommittedMarkerAndAppliesNoSecondEffect()
    {
        await W9TestSupport.EnsureSchemaAsync(fixture.Source);
        var queue = await fixture.QueueAsync();
        var message = MessageEnvelope<JsonElement>.Create("W9DedupProofV1", "correlation-1",
            JsonSerializer.SerializeToElement(new { value = 1 }));
        var scope = "w9-commit-before-ack-" + Guid.NewGuid().ToString("N");
        var store = new ProcessedMessageStore(fixture.Source);
        await using var publisher = new ConfirmedPublisher(fixture.Factory);
        var firstCommitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondHandled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deliveries = 0;

        async Task Handler(MessageEnvelope<JsonElement> envelope, CancellationToken token)
        {
            var outcome = await store.ExecuteOnceAsync(scope, envelope.MessageId, envelope.MessageType,
                (connection, transaction, ct) => InsertEffectAsync(connection, transaction, scope,
                    envelope.MessageId, envelope.CorrelationId, ct), token);
            var delivery = Interlocked.Increment(ref deliveries);
            if (delivery == 1)
            {
                Assert.Equal(DedupOutcome.Applied, outcome);
                firstCommitted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return;
            }

            Assert.Equal(DedupOutcome.Duplicate, outcome);
            secondHandled.TrySetResult();
        }

        await using var first = await ManualAckConsumer.StartAsync(fixture.Factory, publisher, queue,
            new HashSet<string> { "W9DedupProofV1" }, Handler, prefetch: 1);
        await publisher.PublishAsync("ride.commands", queue, message);
        await firstCommitted.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // The local commit is durable, but the consumer has not ACKed.
        Assert.Equal(0, first.AcknowledgedCount);
        Assert.Equal(1, await MarkerCountAsync(fixture.Source, scope, message.MessageId));
        Assert.Equal(1, await EffectCountAsync(fixture.Source, scope, message.MessageId));

        await first.DisposeAsync(); // Connection close without ACK: broker must redeliver.

        await using var second = await ManualAckConsumer.StartAsync(fixture.Factory, publisher, queue,
            new HashSet<string> { "W9DedupProofV1" }, Handler, prefetch: 1);
        await secondHandled.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await W9TestSupport.EventuallyAsync(() => Task.FromResult(second.AcknowledgedCount == 1),
            "second delivery ACK");

        Assert.Equal(1, await MarkerCountAsync(fixture.Source, scope, message.MessageId));
        Assert.Equal(1, await EffectCountAsync(fixture.Source, scope, message.MessageId));
        Assert.True(Volatile.Read(ref deliveries) >= 2);
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

    private static Task<long> MarkerCountAsync(NpgsqlDataSource source, string scope, string messageId) =>
        W9TestSupport.ScalarAsync(source,
            "SELECT count(*) FROM processed_messages WHERE consumer_scope=@scope AND message_id=@message_id",
            CancellationToken.None, ("scope", scope), ("message_id", messageId));

    private static Task<long> EffectCountAsync(NpgsqlDataSource source, string scope, string messageId) =>
        W9TestSupport.ScalarAsync(source,
            "SELECT count(*) FROM w9_effects WHERE consumer_scope=@scope AND message_id=@message_id",
            CancellationToken.None, ("scope", scope), ("message_id", messageId));
}
