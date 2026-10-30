using System.Text.Json;
using RideHailing.Messaging.Contracts;
using RideHailing.NotificationWorker;
using Xunit;

namespace RideHailing.NotificationWorker.UnitTests;

public sealed class NotificationEventProcessorTests
{
    [Fact]
    public async Task DuplicateDeliveryIsAVisibleNoOp()
    {
        var store = new MemoryStore();
        var processor = new NotificationEventProcessor(store, TimeProvider.System);
        var envelope = MessageEnvelope<JsonElement>.Create(
            "PaymentCapturedV1", "correlation-1",
            JsonSerializer.SerializeToElement(new { tripId = Guid.NewGuid() }),
            messageId: "message-1", aggregateType: "Trip", aggregateId: Guid.NewGuid().ToString("D"));

        Assert.True(await processor.ProcessAsync(envelope, default));
        Assert.False(await processor.ProcessAsync(envelope, default));
        Assert.Single(store.Rows);
    }

    [Fact]
    public async Task UnsupportedEventCannotBeSilentlyAcknowledged()
    {
        var processor = new NotificationEventProcessor(new MemoryStore(), TimeProvider.System);
        var envelope = MessageEnvelope<JsonElement>.Create(
            "UnexpectedEventV1", "correlation-2", JsonSerializer.SerializeToElement(new { }), messageId: "message-2");

        await Assert.ThrowsAsync<ArgumentException>(() => processor.ProcessAsync(envelope, default));
    }

    private sealed class MemoryStore : INotificationProjectionStore
    {
        public HashSet<(string Scope, string Message)> Rows { get; } = [];

        public Task<bool> RecordDeliveredAsync(string consumerScope, string messageId, string messageType,
            string correlationId, string? aggregateId, DateTimeOffset deliveredAt, CancellationToken token) =>
            Task.FromResult(Rows.Add((consumerScope, messageId)));
    }
}
