using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using Npgsql;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using OpenTelemetry.Metrics;

namespace RideHailing.Observability;

public static class PlatformBootstrap
{
    public static void AddRideTelemetry(this IHostApplicationBuilder builder, string serviceName)
    {
        var instanceId = builder.Configuration["ServiceInstanceId"] ?? Environment.MachineName;
        builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(serviceName, serviceInstanceId: instanceId)
                .AddAttributes(new Dictionary<string, object>
                {
                    ["deployment.environment"] = builder.Environment.EnvironmentName
                }))
            .WithTracing(t =>
            {
                t.AddAspNetCoreInstrumentation();
                if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
                    t.AddOtlpExporter();
            })
            .WithMetrics(m =>
            {
                m.AddMeter("Microsoft.AspNetCore.Hosting", "Microsoft.AspNetCore.Server.Kestrel");
                if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
                    m.AddOtlpExporter();
            });
    }

    public static void MapRideReadiness(this WebApplication app)
    {
        app.MapGet("/health/ready", async (CancellationToken requestCancellation) =>
        {
            var connectionString = app.Configuration.GetConnectionString("Postgres");
            if (string.IsNullOrWhiteSpace(connectionString))
                return Results.Ok(new { status = "Ready", scope = "process-only; no database configured" });
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(requestCancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                await using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync(timeout.Token);
                await using var command = new NpgsqlCommand("SELECT 1", connection);
                await command.ExecuteScalarAsync(timeout.Token);
                return Results.Ok(new { status = "Ready", scope = "process-and-owned-database" });
            }
            catch (Exception exception) when (exception is NpgsqlException or OperationCanceledException)
            {
                return Results.Json(new { status = "NotReady", dependency = "owned-database" }, statusCode: 503);
            }
        });
    }
}
