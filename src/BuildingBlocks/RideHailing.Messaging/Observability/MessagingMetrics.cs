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

    public static void RecordOutboxBacklog(long pending, long failedVisible, double oldestAgeSeconds)
    {
        Interlocked.Exchange(ref pendingCount, Math.Max(0, pending));
        Interlocked.Exchange(ref failedVisibleCount, Math.Max(0, failedVisible));
        Interlocked.Exchange(ref oldestPendingAgeSeconds, Math.Max(0, oldestAgeSeconds));
    }
}
