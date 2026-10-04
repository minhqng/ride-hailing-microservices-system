using RideHailing.Observability;
// Bootstrap skeleton for RideHailing.IdentityService (Identity service).
// Owns: see src/Services/Identity/RideHailing.IdentityService/README.md. No business endpoints yet.
// TODO(W3+): add owner-specific domain, persistence, messaging per Docs 00-05.

var builder = WebApplication.CreateBuilder(args);
builder.AddRideTelemetry("identity-service");
builder.Services.AddHealthChecks();

var app = builder.Build();

app.MapGet("/health/live", () => Results.Ok(new { status = "Live", service = "identity-service" }));
app.MapRideReadiness();
app.MapGet("/info", (IHostEnvironment env) =>
    Results.Ok(new { serviceName = "identity-service", environment = env.EnvironmentName }));

app.Run();
