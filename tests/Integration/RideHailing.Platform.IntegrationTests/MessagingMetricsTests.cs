using System.Diagnostics.Metrics;
using RideHailing.Messaging.Observability;

namespace RideHailing.Platform.IntegrationTests;

public class MessagingMetricsTests
{
    [Fact]
    public void OutboxAndDedupMetricsUseOnlyBoundedOutcomeDimensions()
    {
        var captures = new List<Capture>();
        var gate = new object();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == MessagingMetrics.MeterName)
                    meterListener.EnableMeasurementEvents(instrument);
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            lock (gate) captures.Add(new Capture(instrument.Name, value, tags.ToArray()));
        });
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
        {
            lock (gate) captures.Add(new Capture(instrument.Name, value, tags.ToArray()));
        });
        listener.Start();

        MessagingMetrics.RecordClaim(3);
        MessagingMetrics.RecordPublishAttempt();
        MessagingMetrics.RecordPublishAccepted();
        MessagingMetrics.RecordPublishFailed();
        MessagingMetrics.RecordMark(published: true);
        MessagingMetrics.RecordMark(published: false);
        MessagingMetrics.RecordConfirmLatency(12.5);
        MessagingMetrics.RecordDedup(duplicate: false);
        MessagingMetrics.RecordDedup(duplicate: true);
        MessagingMetrics.RecordDelivery(redelivered: false);
        MessagingMetrics.RecordDelivery(redelivered: true);
        MessagingMetrics.RecordAcknowledged();
        MessagingMetrics.RecordRetry("Transient");
        MessagingMetrics.RecordDeadLetter("Poison");
        MessagingMetrics.RecordProcessingDuration(7.5, "success");
        MessagingMetrics.RecordOutboxBacklog(pending: 4, failedVisible: 2, oldestAgeSeconds: 9.5);
        listener.RecordObservableInstruments();

        Capture[] snapshot;
        lock (gate) snapshot = captures.ToArray();

        Assert.Contains(snapshot, c => c.Instrument == "outbox.claim" && c.Value == 3d);
        Assert.Contains(snapshot, c => c.Instrument == "outbox.publish.attempts" && c.Value == 1d);
        Assert.Contains(snapshot, c => c.Instrument == "outbox.publish" && HasTag(c, "outcome", "accepted"));
        Assert.Contains(snapshot, c => c.Instrument == "outbox.publish" && HasTag(c, "outcome", "failed"));
        Assert.Contains(snapshot, c => c.Instrument == "outbox.mark" && HasTag(c, "outcome", "published"));
        Assert.Contains(snapshot, c => c.Instrument == "outbox.mark" && HasTag(c, "outcome", "already_published"));
        Assert.Contains(snapshot, c => c.Instrument == "outbox.confirm.duration" && c.Value == 12.5d);
        Assert.Contains(snapshot, c => c.Instrument == "dedup.processed" && HasTag(c, "outcome", "applied"));
        Assert.Contains(snapshot, c => c.Instrument == "dedup.processed" && HasTag(c, "outcome", "duplicate"));
        Assert.Contains(snapshot, c => c.Instrument == "consumer.delivery" && c.Value == 1d);
        Assert.Contains(snapshot, c => c.Instrument == "consumer.redelivery" && c.Value == 1d);
        Assert.Contains(snapshot, c => c.Instrument == "consumer.ack" && c.Value == 1d);
        Assert.Contains(snapshot, c => c.Instrument == "consumer.retry" && HasTag(c, "reason", "Transient"));
        Assert.Contains(snapshot, c => c.Instrument == "consumer.deadletter" && HasTag(c, "kind", "Poison"));
        Assert.Contains(snapshot, c => c.Instrument == "consumer.processing.duration" && HasTag(c, "outcome", "success"));
        Assert.Contains(snapshot, c => c.Instrument == "outbox.pending" && c.Value == 4d);
        Assert.Contains(snapshot, c => c.Instrument == "outbox.failed_visible" && c.Value == 2d);
        Assert.Contains(snapshot, c => c.Instrument == "outbox.oldest_pending.age" && c.Value == 9.5d);

        var forbidden = new[] { "message_id", "messageId", "correlationId", "operationKey", "aggregateId", "tripId" };
        Assert.DoesNotContain(snapshot.SelectMany(capture => capture.Tags),
            tag => forbidden.Contains(tag.Key, StringComparer.Ordinal));
        var allowedKeys = new[] { "outcome", "reason", "kind" };
        Assert.All(snapshot, capture => Assert.All(capture.Tags,
            tag => Assert.Contains(tag.Key, allowedKeys)));
    }

    private static bool HasTag(Capture capture, string key, string value) =>
        capture.Tags.Any(tag => tag.Key == key && string.Equals(tag.Value as string, value, StringComparison.Ordinal));

    private sealed record Capture(string Instrument, double Value,
        IReadOnlyList<KeyValuePair<string, object?>> Tags);
}
