// Test-side capture utilities. These use the REAL OpenTelemetry SDK
// pipeline (capturing exporter) and a real ActivityListener — not mocks, and
// not handler call counts. Delta temporality turns each Flush() into the
// exact increments emitted since the previous flush, so replay/rollback
// semantics are asserted on emitted signals.

using System.Collections.Concurrent;
using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Metrics;

namespace RideHailing.TripService.IntegrationTests;

internal sealed record MeterEvent(string Instrument, double Value, IReadOnlyDictionary<string, string> Tags);

/// <summary>Minimal public-API metrics exporter that captures batches.</summary>
internal sealed class CapturingMetricExporter(ConcurrentQueue<Metric> queue) : BaseExporter<Metric>
{
    public override ExportResult Export(in Batch<Metric> batch)
    {
        foreach (var metric in batch)
            queue.Enqueue(metric);
        return ExportResult.Success;
    }
}

internal sealed class MeterRecorder : IDisposable
{
    private readonly ConcurrentQueue<Metric> exported = new();
    private readonly MeterProvider provider;

    public MeterRecorder()
    {
        var reader = new BaseExportingMetricReader(new CapturingMetricExporter(exported))
        {
            TemporalityPreference = MetricReaderTemporalityPreference.Delta,
        };
        provider = Sdk.CreateMeterProviderBuilder()
            .AddMeter("RideHailing.Trip")
            .AddMeter("RideHailing.Messaging")
            .AddReader(reader)
            .Build();
    }

    /// <summary>
    /// Forces one collect cycle and returns the delta events emitted since
    /// the previous flush (counters: increments; gauges: current values).
    /// </summary>
    public IReadOnlyList<MeterEvent> Flush()
    {
        provider.ForceFlush();
        var events = new List<MeterEvent>();
        while (exported.TryDequeue(out var metric))
        {
            foreach (ref readonly var point in metric.GetMetricPoints())
            {
                double value;
                if (metric.MetricType.IsSum())
                    value = metric.MetricType.IsLong()
                        ? point.GetSumLong()
                        : point.GetSumDouble();
                else if (metric.MetricType is MetricType.Histogram or MetricType.ExponentialHistogram)
                    value = point.GetHistogramSum();
                else
                    value = metric.MetricType.IsLong()
                        ? point.GetGaugeLastValueLong()
                        : point.GetGaugeLastValueDouble();

                var tags = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var tag in point.Tags)
                    tags[tag.Key] = tag.Value?.ToString() ?? "";
                events.Add(new MeterEvent(metric.Name, value, tags));
            }
        }

        return events;
    }

    public void Dispose() => provider.Dispose();
}

internal sealed class ActivityRecorder : IDisposable
{
    private readonly ActivityListener listener;

    public ConcurrentBag<Activity> Activities { get; } = new();

    public ActivityRecorder()
    {
        listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name is "RideHailing.Trip" or "RideHailing.Messaging",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
        };
        listener.ActivityStarted += activity => Activities.Add(activity);
        ActivitySource.AddActivityListener(listener);
    }

    public IReadOnlyList<Activity> All(string operationName) =>
        Activities.Where(a => a.OperationName == operationName).ToList();

    public void Dispose() => listener.Dispose();
}
