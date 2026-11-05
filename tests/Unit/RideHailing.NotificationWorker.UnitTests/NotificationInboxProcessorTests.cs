// DS03 notification inbox semantics (Doc 01 §40.2, Doc 02 §43, Doc 05C §48).

using System.Text.Json;
using RideHailing.Messaging.Contracts;
using RideHailing.Messaging.Reliability;
using Xunit;

namespace RideHailing.NotificationWorker.UnitTests;

public sealed class NotificationInboxProcessorTests
{
    private static MessageEnvelope<JsonElement> Envelope(
        Guid recipient, string role, string type, string messageId = "notify-1",
        string? reasonCode = null, Guid? tripId = null)
    {
        var intent = new NotificationRequestedV1
        {
            RecipientUserId = recipient,
            RecipientRole = role,
            NotificationType = type,
            TripId = tripId ?? Guid.NewGuid(),
            ReasonCode = reasonCode,
        };
        return MessageEnvelope<JsonElement>.Create(
            NotificationMessage.MessageType,
            "correlation-1",
            JsonSerializer.SerializeToElement(intent),
            messageId: messageId,
            aggregateType: "Trip",
            aggregateId: intent.TripId.ToString("D"));
    }

    [Fact]
    public async Task ValidIntent_IsStoredWithDeterministicIdentityAndSafeContent()
    {
        var inbox = new FakeInbox();
        var processor = Processor(inbox);
        var recipient = Guid.NewGuid();
        var tripId = Guid.NewGuid();
        var envelope = Envelope(recipient, NotificationMessage.Roles.Rider,
            NotificationMessage.Types.RiderDriverRejected, "notify-reject",
            "DRIVER_REJECTED", tripId);

        Assert.True(await processor.ProcessAsync(envelope, default));

        var row = Assert.Single(inbox.Rows);
        Assert.Equal(recipient, row.Entry.RecipientUserId);
        Assert.Equal(NotificationMessage.Roles.Rider, row.Entry.RecipientRole);
        Assert.Equal(NotificationMessage.Types.RiderDriverRejected, row.Entry.NotificationType);
        Assert.Equal(tripId, row.Entry.TripId);
        Assert.Equal("notify-reject", row.Entry.SourceMessageId);
        Assert.Equal("correlation-1", row.Entry.CorrelationId);
        Assert.Equal(NotificationContent.DeriveNotificationId("notify-reject", recipient,
            NotificationMessage.Types.RiderDriverRejected), row.Entry.NotificationId);
        Assert.Equal("INBOX_AVAILABLE", row.Entry.DeliveryStatus);
        Assert.Null(row.Entry.ReadAt);
        Assert.Contains("rejected", row.Entry.Content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Bearer", row.Entry.Content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", row.Entry.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DuplicateIntent_IsDuplicateWithoutSecondRow()
    {
        var inbox = new FakeInbox();
        var processor = Processor(inbox);
        var envelope = Envelope(Guid.NewGuid(), NotificationMessage.Roles.Driver,
            NotificationMessage.Types.DriverTripAssigned, "notify-assigned");

        Assert.True(await processor.ProcessAsync(envelope, default));
        Assert.False(await processor.ProcessAsync(envelope, default));

        Assert.Single(inbox.Rows);
        Assert.Equal(1, inbox.AppliedCount);
    }

    [Fact]
    public async Task TwoRecipientsForOneMessage_AreSeparateRows()
    {
        var inbox = new FakeInbox();
        var processor = Processor(inbox);
        var tripId = Guid.NewGuid();
        var rider = Guid.NewGuid();
        var driver = Guid.NewGuid();

        await processor.ProcessAsync(Envelope(rider, NotificationMessage.Roles.Rider,
            NotificationMessage.Types.RiderDriverAssigned, "notify-shared-rider", tripId: tripId), default);
        await processor.ProcessAsync(Envelope(driver, NotificationMessage.Roles.Driver,
            NotificationMessage.Types.DriverTripAssigned, "notify-shared-driver", tripId: tripId), default);

        Assert.Equal(2, inbox.Rows.Count);
    }

    [Fact]
    public async Task InvalidRoleTypeCombination_IsPoison()
    {
        var processor = Processor(new FakeInbox());
        var envelope = Envelope(Guid.NewGuid(), NotificationMessage.Roles.Driver,
            NotificationMessage.Types.RiderDriverRejected, "notify-invalid");

        var error = await Assert.ThrowsAsync<DeliveryFailureException>(
            () => processor.ProcessAsync(envelope, default));
        Assert.Equal(FailureKind.Poison, error.Kind);
        Assert.Equal("INVALID_NOTIFICATION_EVENT", error.Code);
    }

    [Fact]
    public async Task SameMessageIdWithDifferentContent_IsPoison()
    {
        var inbox = new FakeInbox();
        var processor = Processor(inbox);
        var recipient = Guid.NewGuid();
        var first = Envelope(recipient, NotificationMessage.Roles.Rider,
            NotificationMessage.Types.RiderRideRequested, "notify-conflict");
        Assert.True(await processor.ProcessAsync(first, default));

        var second = Envelope(recipient, NotificationMessage.Roles.Rider,
            NotificationMessage.Types.RiderRideCancelled, "notify-conflict");
        var error = await Assert.ThrowsAsync<DeliveryFailureException>(
            () => processor.ProcessAsync(second, default));

        Assert.Equal(FailureKind.Poison, error.Kind);
        Assert.Equal("NOTIFICATION_CONTENT_CONFLICT", error.Code);
        Assert.Single(inbox.Rows);
    }

    [Fact]
    public void EverySupportedNotificationType_HasBoundedSafeContent()
    {
        foreach (var type in new[]
                 {
                     NotificationMessage.Types.RiderRideRequested,
                     NotificationMessage.Types.RiderDriverAssigned,
                     NotificationMessage.Types.RiderDriverRejected,
                     NotificationMessage.Types.RiderNoDriver,
                     NotificationMessage.Types.RiderTripCompleted,
                     NotificationMessage.Types.RiderRideCancelled,
                     NotificationMessage.Types.RiderPaymentFailed,
                     NotificationMessage.Types.DriverTripAssigned,
                     NotificationMessage.Types.DriverReservationReleased,
                 })
        {
            var content = NotificationContent.For(type);
            Assert.False(string.IsNullOrWhiteSpace(content));
            Assert.DoesNotContain("Bearer", content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("password", content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("token", content, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void DeterministicId_DiffersByRecipientAndType()
    {
        var message = "message-1";
        var rider = Guid.NewGuid();
        var driver = Guid.NewGuid();
        var a = NotificationContent.DeriveNotificationId(message, rider,
            NotificationMessage.Types.RiderRideRequested);
        var b = NotificationContent.DeriveNotificationId(message, driver,
            NotificationMessage.Types.RiderRideRequested);
        var c = NotificationContent.DeriveNotificationId(message, rider,
            NotificationMessage.Types.RiderRideCancelled);
        Assert.NotEqual(a, b);
        Assert.NotEqual(a, c);
        Assert.Equal(a, NotificationContent.DeriveNotificationId(message, rider,
            NotificationMessage.Types.RiderRideRequested));
    }

    [Theory]
    [InlineData("", NotificationMessage.Roles.Rider, NotificationMessage.Types.RiderRideRequested)]
    [InlineData("00000000-0000-0000-0000-000000000001", "Admin", NotificationMessage.Types.RiderRideRequested)]
    [InlineData("00000000-0000-0000-0000-000000000001", NotificationMessage.Roles.Driver,
        NotificationMessage.Types.RiderRideRequested)]
    [InlineData("00000000-0000-0000-0000-000000000001", NotificationMessage.Roles.Rider, "UNKNOWN")]
    public void Validate_RejectsMalformedIntent(string recipient, string role, string type)
    {
        var intent = new NotificationRequestedV1
        {
            RecipientUserId = Guid.Parse(recipient == "" ? Guid.Empty.ToString() : recipient),
            RecipientRole = role,
            NotificationType = type,
            TripId = Guid.NewGuid(),
        };
        Assert.Throws<ArgumentException>(intent.Validate);
    }

    private static NotificationEventProcessor Processor(FakeInbox inbox) =>
        new(new NoopProjectionStore(), TimeProvider.System, null, inbox);

    private sealed class NoopProjectionStore : INotificationProjectionStore
    {
        public Task<bool> RecordDeliveredAsync(string consumerScope, string messageId, string messageType,
            string correlationId, string? aggregateId, DateTimeOffset deliveredAt, CancellationToken token) =>
            Task.FromResult(true);
    }

    private sealed class FakeInbox : INotificationInboxStore
    {
        public List<(NotificationInboxEntry Entry, string Fingerprint)> Rows { get; } = [];
        private readonly Dictionary<string, string> _sourceFingerprints = new(StringComparer.Ordinal);
        public int AppliedCount { get; private set; }

        public Task<NotificationWriteResult> RecordNotificationAsync(
            NotificationInboxEntry entry, string fingerprint, CancellationToken token)
        {
            if (_sourceFingerprints.TryGetValue(entry.SourceMessageId, out var sourceFingerprint) &&
                sourceFingerprint != fingerprint)
                throw new NotificationContentConflictException("source conflict");
            _sourceFingerprints[entry.SourceMessageId] = fingerprint;

            var existing = Rows.SingleOrDefault(r =>
                r.Entry.SourceMessageId == entry.SourceMessageId &&
                r.Entry.RecipientUserId == entry.RecipientUserId);
            if (existing.Entry is not null)
            {
                if (existing.Fingerprint != fingerprint)
                    throw new NotificationContentConflictException("conflict");
                return Task.FromResult(NotificationWriteResult.Duplicate);
            }

            Rows.Add((entry, fingerprint));
            AppliedCount++;
            return Task.FromResult(NotificationWriteResult.Applied);
        }

        public Task<IReadOnlyList<NotificationInboxEntry>> ListForRecipientAsync(
            Guid recipientUserId, int limit, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<NotificationInboxEntry>>(Rows
                .Where(r => r.Entry.RecipientUserId == recipientUserId)
                .Select(r => r.Entry).Take(limit).ToList());

        public Task<NotificationInboxEntry?> GetForRecipientAsync(
            Guid recipientUserId, Guid notificationId, CancellationToken token) =>
            Task.FromResult(Rows.Select(r => r.Entry).SingleOrDefault(e =>
                e.RecipientUserId == recipientUserId && e.NotificationId == notificationId));

        public Task<NotificationInboxEntry?> MarkReadAsync(
            Guid recipientUserId, Guid notificationId, DateTimeOffset readAt, CancellationToken token)
        {
            var index = Rows.FindIndex(r => r.Entry.RecipientUserId == recipientUserId &&
                r.Entry.NotificationId == notificationId);
            if (index < 0) return Task.FromResult<NotificationInboxEntry?>(null);
            var entry = Rows[index].Entry with { ReadAt = readAt, DeliveryStatus = "READ" };
            Rows[index] = (entry, Rows[index].Fingerprint);
            return Task.FromResult<NotificationInboxEntry?>(entry);
        }
    }
}
