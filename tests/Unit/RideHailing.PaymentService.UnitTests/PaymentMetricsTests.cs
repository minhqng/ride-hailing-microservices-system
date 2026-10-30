using System.Diagnostics.Metrics;
using RideHailing.PaymentService.Domain;
using RideHailing.PaymentService.Observability;

namespace RideHailing.PaymentService.UnitTests;

public sealed class PaymentMetricsTests
{
    [Fact]
    public void MetricsExposeOnlyBoundedTypeAndOutcomeDimensions()
    {
        var captures = new List<(string Name, KeyValuePair<string, object?>[] Tags)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == PaymentMetrics.MeterName)
                    meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
            captures.Add((instrument.Name, tags.ToArray())));
        listener.SetMeasurementEventCallback<double>((instrument, _, tags, _) =>
            captures.Add((instrument.Name, tags.ToArray())));
        listener.Start();

        var operation = PaymentOperation.Create(
            "trip:test:authorize:v1", Guid.NewGuid(), PaymentOperationType.Authorize,
            10_000, "VND", DateTimeOffset.UtcNow);
        var started = PaymentMetrics.Start();
        PaymentMetrics.Record(operation, idempotencyHit: true, started);
        PaymentMetrics.RecordReconciliation(PaymentOperationState.Unknown);

        Assert.Contains(captures, capture => capture.Name == "payment.operation");
        Assert.Contains(captures, capture => capture.Name == "payment.idempotency.hit");
        Assert.Contains(captures, capture => capture.Name == "payment.reconciliation");
        var allowed = new[] { "type", "outcome" };
        Assert.DoesNotContain(captures.SelectMany(capture => capture.Tags),
            tag => !allowed.Contains(tag.Key, StringComparer.Ordinal));
    }
}
