using RideHailing.Observability;
// Bootstrap skeleton for RideHailing.PaymentService (Payment service).
// Owns: see src/Services/Payment/RideHailing.PaymentService/README.md. No business endpoints yet.
// TODO(W3+): add owner-specific domain, persistence, messaging per Docs 00-05.

var builder = WebApplication.CreateBuilder(args);
builder.AddRideTelemetry("payment-service");
builder.Services.AddHealthChecks();

var app = builder.Build();

if (builder.Configuration["RabbitMq:Host"] is { Length: > 0 } rabbitHost)
{
    using var bootstrapTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    await RideHailing.Messaging.Topology.BrokerBootstrapV1.DeclareAsync(rabbitHost,
        builder.Configuration["RabbitMq:Username"] ?? "ride_app",
        builder.Configuration["RabbitMq:Password"] ?? "ride_dev_pw", bootstrapTimeout.Token);
}

app.MapGet("/health/live", () => Results.Ok(new { status = "Live", service = "payment-service" }));
app.MapRideReadiness();
app.MapGet("/info", (IHostEnvironment env) =>
    Results.Ok(new
    {
        serviceName = "payment-service",
        instanceId = builder.Configuration["ServiceInstanceId"] ?? Environment.MachineName,
        environment = env.EnvironmentName
    }));

app.Run();
