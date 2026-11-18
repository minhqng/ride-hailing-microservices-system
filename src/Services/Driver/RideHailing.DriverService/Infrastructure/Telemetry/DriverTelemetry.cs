// Telemetry identity and additive registration. PlatformBootstrap remains the
// owner of the OpenTelemetry provider and exporter; this extension only adds
// the service's ActivitySource/Meter to the existing pipeline.
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace RideHailing.DriverService.Infrastructure.Telemetry;

public static class DriverTelemetry
{
    public const string SourceName = "RideHailing.DriverService";
    public const string MeterName = SourceName;

    public static readonly ActivitySource Activities = new(SourceName);

    public static void AddDriverTelemetry(this IHostApplicationBuilder builder)
    {
        builder.Services.AddOpenTelemetry()
            .WithTracing(tracing => tracing.AddSource(SourceName))
            .WithMetrics(metrics => metrics.AddMeter(MeterName));
    }
}
