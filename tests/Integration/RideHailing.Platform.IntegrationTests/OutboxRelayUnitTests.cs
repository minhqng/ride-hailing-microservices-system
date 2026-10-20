using System.Text.Json;
using RideHailing.Messaging.Contracts;
using RideHailing.Messaging.Outbox;

namespace RideHailing.Platform.IntegrationTests;

public class OutboxRelayUnitTests
{
    [Fact]
    public async Task PublishUsesClaimThenPublishThenMarkOrder()
    {
        var store = new FakeStore();
        store.Add(Row("message-1"));
        var publisher = new FakePublisher(store.Events);

        var relay = new OutboxRelay(store, publisher, new OutboxRelayOptions { RelayId = "relay-1" });

        Assert.Equal(1, await relay.RunOnceAsync());
        Assert.Equal(new[] { "claim", "publish", "mark" }, store.Events);
        Assert.Equal(OutboxStatus.Published, store.Get("message-1").Status);
        Assert.NotNull(store.Get("message-1").PublishedAt);
        Assert.Equal("message-1", Assert.Single(publisher.Published).MessageId);
    }

    [Fact]
    public async Task PublishAcceptedBeforeLostMarkReplaysSameIdentity()
    {
        var store = new FakeStore();
        store.Add(Row("message-1"));
        var publisher = new FakePublisher(store.Events);
        var first = new OutboxRelay(store, publisher, new OutboxRelayOptions
        {
            RelayId = "relay-a",
            AfterPublishBeforeMark = (_, _) => Task.FromException(new SimulatedCrashException()),
        });

        await Assert.ThrowsAsync<SimulatedCrashException>(() => first.RunOnceAsync());
        Assert.Equal(OutboxStatus.Pending, store.Get("message-1").Status);
        Assert.Equal("relay-a", store.Get("message-1").ClaimedBy);
        Assert.Single(publisher.Published);
        Assert.Equal(new[] { "claim", "publish" }, store.Events);

        var second = new OutboxRelay(store, publisher, new OutboxRelayOptions
        {
            RelayId = "relay-b",
            ClaimLease = TimeSpan.Zero,
        });
        Assert.Equal(1, await second.RunOnceAsync());
        Assert.Equal(OutboxStatus.Published, store.Get("message-1").Status);
        Assert.Equal(2, publisher.Published.Count);
        Assert.All(publisher.Published, row => Assert.Equal("message-1", row.MessageId));
    }

    [Fact]
    public async Task ProcessDeathAfterClaimBeforePublishIsReclaimedAfterRestart()
    {
        var store = new FakeStore();
        store.Add(Row("message-1"));
        var publisher = new FakePublisher(store.Events);
        var first = new OutboxRelay(store, publisher, new OutboxRelayOptions
        {
            RelayId = "relay-a",
            BeforePublish = (_, _) => Task.FromException(new SimulatedCrashException()),
        });

        await Assert.ThrowsAsync<SimulatedCrashException>(() => first.RunOnceAsync());
        Assert.Equal(OutboxStatus.Pending, store.Get("message-1").Status);
        Assert.Equal("relay-a", store.Get("message-1").ClaimedBy);
        Assert.Empty(publisher.Published);

        var second = new OutboxRelay(store, publisher, new OutboxRelayOptions
        {
            RelayId = "relay-b",
            ClaimLease = TimeSpan.Zero,
        });
        Assert.Equal(1, await second.RunOnceAsync());
        Assert.Equal(OutboxStatus.Published, store.Get("message-1").Status);
        Assert.Equal("message-1", Assert.Single(publisher.Published).MessageId);
    }

    [Fact]
    public async Task PublishFailureMarksBoundedRetryThenVisibleTerminalFailure()
    {
        var store = new FakeStore();
        store.Add(Row("message-1"));
        var publisher = new FakePublisher(store.Events) { FailWith = _ => new TimeoutException("broker timeout") };
        var relay = new OutboxRelay(store, publisher, new OutboxRelayOptions
        {
            RelayId = "relay-1",
            MaxAttempts = 2,
            RetryDelay = TimeSpan.Zero,
        });

        Assert.Equal(1, await relay.RunOnceAsync());
        var retried = store.Get("message-1");
        Assert.Equal(OutboxStatus.Pending, retried.Status);
        Assert.Equal(1, retried.AttemptCount);
        Assert.Equal("PUBLISH_TIMEOUT", retried.LastErrorCode);
        Assert.Empty(publisher.Published);

        Assert.Equal(1, await relay.RunOnceAsync());
        var failed = store.Get("message-1");
        Assert.Equal(OutboxStatus.FailedVisible, failed.Status);
        Assert.Equal(2, failed.AttemptCount);
        Assert.Null(failed.ClaimedBy);

        Assert.Equal(0, await relay.RunOnceAsync());
        Assert.Equal(2, store.Events.Count(e => e == "publish"));
    }

    [Fact]
    public async Task EmptyClaimDoesNotCallPublisher()
    {
        var store = new FakeStore();
        var publisher = new FakePublisher(store.Events);
        var relay = new OutboxRelay(store, publisher, new OutboxRelayOptions { RelayId = "relay-1" });

        Assert.Equal(0, await relay.RunOnceAsync());
        Assert.Equal(new[] { "claim" }, store.Events);
        Assert.Empty(publisher.Published);
    }

    [Fact]
    public void InvalidRelayOptionsAreRejectedBeforeAnyWorkStarts()
    {
        var store = new FakeStore();
        var publisher = new FakePublisher(store.Events);

        Assert.Throws<ArgumentOutOfRangeException>(() => new OutboxRelay(store, publisher,
            new OutboxRelayOptions { RelayId = "relay-invalid", BatchSize = 0 }));
        Assert.Empty(store.Events);
    }

    [Fact]
    public void OptionsRejectUnboundedValues()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new OutboxRelayOptions { BatchSize = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new OutboxRelayOptions { ClaimLease = TimeSpan.FromDays(2) }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new OutboxRelayOptions { MaxAttempts = 0 }.Validate());
        Assert.Throws<ArgumentException>(() => new OutboxRelayOptions { RelayId = " " }.Validate());
        new OutboxRelayOptions { ClaimLease = TimeSpan.Zero }.Validate();
    }

    private static OutboxMessage Row(string messageId) => OutboxMessage.Create(
        MessageEnvelope<JsonElement>.Create("W9ProofV1", "correlation-" + messageId,
            JsonSerializer.SerializeToElement(new { messageId }), messageId: messageId),
        "ride.commands", "proof.queue");

    private sealed class FakeStore : IOutboxStore
    {
        private readonly Dictionary<string, OutboxMessage> rows = new(StringComparer.Ordinal);
        public List<string> Events { get; } = [];
        public void Add(OutboxMessage row) => rows[row.MessageId] = row;
        public OutboxMessage Get(string messageId) => rows[messageId];

        public Task<IReadOnlyList<OutboxMessage>> ClaimBatchAsync(string relayId, int batchSize, TimeSpan lease,
            DateTimeOffset now, CancellationToken token = default)
        {
            Events.Add("claim");
            var claimed = rows.Values
                .Where(row => row.Status == OutboxStatus.Pending)
                .Where(row => row.NextAttemptAt <= now)
                .Where(row => row.ClaimedAt is null || row.ClaimedAt <= now - lease)
                .OrderBy(row => row.OccurredAt).ThenBy(row => row.MessageId)
                .Take(batchSize)
                .Select(row => row with { ClaimedBy = relayId, ClaimedAt = now })
                .ToList();
            foreach (var row in claimed) rows[row.MessageId] = row;
            return Task.FromResult<IReadOnlyList<OutboxMessage>>(claimed);
        }

        public Task<bool> MarkPublishedAsync(string messageId, DateTimeOffset publishedAt,
            CancellationToken token = default)
        {
            Events.Add("mark");
            if (!rows.TryGetValue(messageId, out var row) || row.Status == OutboxStatus.Published)
                return Task.FromResult(false);
            rows[messageId] = row with
            {
                Status = OutboxStatus.Published,
                PublishedAt = publishedAt,
                ClaimedBy = null,
                ClaimedAt = null,
            };
            return Task.FromResult(true);
        }

        public Task<bool> MarkFailedAsync(OutboxMessage message, string relayId, string errorCode,
            DateTimeOffset failedAt, TimeSpan retryDelay, int maxAttempts, CancellationToken token = default)
        {
            Events.Add("mark_failed");
            if (!rows.TryGetValue(message.MessageId, out var row) || row.Status != OutboxStatus.Pending ||
                row.ClaimedBy != relayId) return Task.FromResult(false);
            var attempts = row.AttemptCount + 1;
            rows[message.MessageId] = row with
            {
                AttemptCount = attempts,
                Status = attempts >= maxAttempts ? OutboxStatus.FailedVisible : OutboxStatus.Pending,
                NextAttemptAt = failedAt + retryDelay,
                ClaimedBy = null,
                ClaimedAt = null,
                LastErrorCode = errorCode,
                LastErrorAt = failedAt,
            };
            return Task.FromResult(true);
        }

        public Task<OutboxStats> GetStatsAsync(CancellationToken token = default)
        {
            var pending = rows.Values.Where(row => row.Status == OutboxStatus.Pending).ToList();
            return Task.FromResult(new OutboxStats(
                pending.Count,
                pending.Count == 0 ? null : pending.Min(row => row.OccurredAt),
                rows.Values.Count(row => row.Status == OutboxStatus.FailedVisible)));
        }
    }

    private sealed class FakePublisher(List<string> events) : IOutboxPublisher
    {
        public List<OutboxMessage> Published { get; } = [];
        public Func<OutboxMessage, Exception>? FailWith { get; init; }

        public Task PublishAsync(OutboxMessage message, CancellationToken token = default)
        {
            events.Add("publish");
            if (FailWith is not null) throw FailWith(message);
            Published.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed class SimulatedCrashException : Exception;
}
