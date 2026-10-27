// B-owned telemetry identity and additive registration. C's PlatformBootstrap
// remains the owner of the OpenTelemetry provider and exporter; this extension
// only adds B's ActivitySource/Meter and the HTTP client instrumentation needed
// for the Driver reserve/reconcile seam.
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace RideHailing.MatchingService.Application.Matching;

public static class MatchingTelemetry
{
    public const string SourceName = "RideHailing.MatchingService";
    public const string MeterName = SourceName;

    public static readonly ActivitySource Activities = new(SourceName);

    public static void AddMatchingTelemetry(this IHostApplicationBuilder builder)
    {
        builder.Services.AddOpenTelemetry()
            .WithTracing(tracing => tracing
                .AddSource(SourceName)
                .AddHttpClientInstrumentation())
            .WithMetrics(metrics => metrics.AddMeter(MeterName));
    }
}
