// Bootstrap skeleton for RideHailing.Gateway (Gateway service).
// Owns: see src/Gateway/RideHailing.Gateway/README.md. No business endpoints yet.
// TODO(W3+): add owner-specific domain, persistence, messaging per Docs 00-05.

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHealthChecks();

var app = builder.Build();

app.MapGet("/health/live", () => Results.Ok(new { status = "Live", service = "gateway-service" }));
app.MapGet("/health/ready", () => Results.Ok(new { status = "Ready", service = "gateway-service" }));
app.MapGet("/info", (IHostEnvironment env) =>
    Results.Ok(new { serviceName = "gateway-service", environment = env.EnvironmentName }));

app.Run();
