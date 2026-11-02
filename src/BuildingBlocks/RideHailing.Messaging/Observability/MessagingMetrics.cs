using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace RideHailing.Messaging.Observability;

/// <summary>
/// Low-cardinality Outbox/dedup instruments. Tags are bounded outcome values
/// only; message ids, correlation ids, trips and operation keys never become
/// metric dimensions. Backlog gauges are refreshed after each relay cycle.
/// </summary>
public static class MessagingMetrics
{
    public const string MeterName = "RideHailing.Messaging";

    public static readonly Meter Meter = new(MeterName);

    private static readonly Counter<long> OutboxClaim =
        Meter.CreateCounter<long>("outbox.claim", "messages", "Outbox rows claimed by a relay.");
    private static readonly Counter<long> OutboxPublish =
        Meter.CreateCounter<long>("outbox.publish", "messages", "Outbox publish outcomes.");
    private static readonly Counter<long> OutboxPublishAttempts =
        Meter.CreateCounter<long>("outbox.publish.attempts", "messages", "Outbox publish attempts.");
    private static readonly Counter<long> OutboxMark =
        Meter.CreateCounter<long>("outbox.mark", "messages", "Outbox mark outcomes.");
    private static readonly Histogram<double> OutboxConfirm =
        Meter.CreateHistogram<double>("outbox.confirm.duration", "ms", "Publisher-confirm latency for Outbox rows.");
    private static readonly Counter<long> DedupProcessed =
        Meter.CreateCounter<long>("dedup.processed", "messages", "Processed-message dedup outcomes.");
    private static readonly Counter<long> ConsumerDelivery =
        Meter.CreateCounter<long>("consumer.delivery", "messages", "Consumer deliveries, including redeliveries.");
    private static readonly Counter<long> ConsumerAck =
        Meter.CreateCounter<long>("consumer.ack", "messages", "Deliveries acknowledged only after durable handling or confirmed transfer.");
    private static readonly Counter<long> ConsumerRedelivery =
        Meter.CreateCounter<long>("consumer.redelivery", "messages", "Broker deliveries marked as redelivered.");
    private static readonly Counter<long> ConsumerRetry =
        Meter.CreateCounter<long>("consumer.retry", "messages", "Bounded consumer retry publications.");
    private static readonly Counter<long> ConsumerDeadLetter =
        Meter.CreateCounter<long>("consumer.deadletter", "messages", "Confirmed transfers to visible error handling.");
    private static readonly Histogram<double> ConsumerProcessing =
        Meter.CreateHistogram<double>("consumer.processing.duration", "ms", "End-to-end delivery handling time before ACK.");

    private static long pendingCount;
    private static long failedVisibleCount;
    private static double oldestPendingAgeSeconds;

    private static readonly ObservableGauge<long> PendingGauge = Meter.CreateObservableGauge(
        "outbox.pending", () => Interlocked.Read(ref pendingCount), "messages", "Pending Outbox rows.");
    private static readonly ObservableGauge<long> FailedVisibleGauge = Meter.CreateObservableGauge(
        "outbox.failed_visible", () => Interlocked.Read(ref failedVisibleCount), "messages", "Outbox rows in visible terminal failure.");
    private static readonly ObservableGauge<double> OldestPendingAgeGauge = Meter.CreateObservableGauge(
        "outbox.oldest_pending.age", () => Volatile.Read(ref oldestPendingAgeSeconds), "s", "Age of the oldest pending Outbox row.");

    public static void RecordClaim(int count)
    {
        if (count > 0) OutboxClaim.Add(count);
    }

    public static void RecordPublishAccepted()
    {
        var tags = new TagList { { "outcome", "accepted" } };
        OutboxPublish.Add(1, tags);
    }

    public static void RecordPublishAttempt()
    {
        OutboxPublishAttempts.Add(1);
    }

    public static void RecordPublishFailed()
    {
        var tags = new TagList { { "outcome", "failed" } };
        OutboxPublish.Add(1, tags);
    }

    public static void RecordMark(bool published)
    {
        var tags = new TagList { { "outcome", published ? "published" : "already_published" } };
        OutboxMark.Add(1, tags);
    }

    public static void RecordConfirmLatency(double milliseconds)
    {
        if (milliseconds < 0) return;
        OutboxConfirm.Record(milliseconds);
    }

    public static void RecordDedup(bool duplicate)
    {
        var tags = new TagList { { "outcome", duplicate ? "duplicate" : "applied" } };
        DedupProcessed.Add(1, tags);
    }

    public static void RecordDelivery(bool redelivered)
    {
        ConsumerDelivery.Add(1);
        ConsumerRedelivery.Add(redelivered ? 1 : 0);
    }

    public static void RecordAcknowledged() => ConsumerAck.Add(1);

    public static void RecordRetry(string reason)
    {
        var tags = new TagList { { "reason", BoundedReason(reason) } };
        ConsumerRetry.Add(1, tags);
    }

    public static void RecordDeadLetter(string kind)
    {
        var tags = new TagList { { "kind", BoundedReason(kind) } };
        ConsumerDeadLetter.Add(1, tags);
    }

    public static void RecordProcessingDuration(double milliseconds, string outcome)
    {
        if (milliseconds < 0) return;
        var tags = new TagList { { "outcome", BoundedReason(outcome) } };
        ConsumerProcessing.Record(milliseconds, tags);
    }

    public static void RecordOutboxBacklog(long pending, long failedVisible, double oldestAgeSeconds)
    {
        Interlocked.Exchange(ref pendingCount, Math.Max(0, pending));
        Interlocked.Exchange(ref failedVisibleCount, Math.Max(0, failedVisible));
        Interlocked.Exchange(ref oldestPendingAgeSeconds, Math.Max(0, oldestAgeSeconds));
    }

    private static string BoundedReason(string value) => value switch
    {
        "success" or "retry" or "deadletter" or "cancelled" or "failed" => value,
        "Validation" or "Authorization" or "DomainConflict" or "Transient" or
        "UnknownEffect" or "Poison" or "ResourcePressure" or "Configuration" => value,
        _ => "other"
    };
}
