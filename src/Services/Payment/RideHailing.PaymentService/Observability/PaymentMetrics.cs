using System.Diagnostics;
using System.Diagnostics.Metrics;
using RideHailing.PaymentService.Domain;

namespace RideHailing.PaymentService.Observability;

/// <summary>
/// Low-cardinality Payment authority metrics. Operation keys, trip ids and
/// provider effect ids deliberately never become metric labels.
/// </summary>
public static class PaymentMetrics
{
    public const string MeterName = "RideHailing.Payment";
    public static readonly Meter Meter = new(MeterName);

    private static readonly Counter<long> Operations =
        Meter.CreateCounter<long>("payment.operation", "operations");
    private static readonly Counter<long> IdempotencyHits =
        Meter.CreateCounter<long>("payment.idempotency.hit", "operations");
    private static readonly Counter<long> Unknown =
        Meter.CreateCounter<long>("payment.unknown", "operations");
    private static readonly Counter<long> RequiresReview =
        Meter.CreateCounter<long>("payment.requires_review", "operations");
    private static readonly Counter<long> Reconciliations =
        Meter.CreateCounter<long>("payment.reconciliation", "operations");
    private static readonly Histogram<double> Duration =
        Meter.CreateHistogram<double>("payment.operation.duration", "ms");

    public static long Start() => Stopwatch.GetTimestamp();

    public static void Record(PaymentOperation operation, bool idempotencyHit, long started)
    {
        var tags = new TagList
        {
            { "type", operation.Type.ToString().ToLowerInvariant() },
            { "outcome", operation.State.ToString().ToLowerInvariant() }
        };
        Operations.Add(1, tags);
        Duration.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds, tags);
        if (idempotencyHit) IdempotencyHits.Add(1, new TagList { { "type", operation.Type.ToString().ToLowerInvariant() } });
        if (operation.State == PaymentOperationState.Unknown) Unknown.Add(1);
        if (operation.State == PaymentOperationState.RequiresReview) RequiresReview.Add(1);
    }

    public static void RecordReconciliation(PaymentOperationState outcome)
    {
        var tags = new TagList { { "outcome", outcome.ToString().ToLowerInvariant() } };
        Reconciliations.Add(1, tags);
    }
}
