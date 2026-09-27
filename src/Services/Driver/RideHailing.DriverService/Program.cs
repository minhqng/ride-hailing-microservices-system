// Bootstrap skeleton for RideHailing.DriverService (Driver service).
// Owns: see src/Services/Driver/RideHailing.DriverService/README.md. No business endpoints yet.
// TODO(W3+): add owner-specific domain, persistence, messaging per Docs 00-05.

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHealthChecks();

var app = builder.Build();

app.MapGet("/health/live", () => Results.Ok(new { status = "Live", service = "driver-service" }));
app.MapGet("/health/ready", () => Results.Ok(new { status = "Ready", service = "driver-service" }));
app.MapGet("/info", (IHostEnvironment env) =>
    Results.Ok(new { serviceName = "driver-service", environment = env.EnvironmentName }));

app.Run();
