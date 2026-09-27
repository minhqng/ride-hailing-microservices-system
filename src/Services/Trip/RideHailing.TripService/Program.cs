// Bootstrap skeleton for RideHailing.TripService (Trip service).
// Owns: see src/Services/Trip/RideHailing.TripService/README.md. No business endpoints yet.
// TODO(W3+): add owner-specific domain, persistence, messaging per Docs 00-05.

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHealthChecks();

var app = builder.Build();

app.MapGet("/health/live", () => Results.Ok(new { status = "Live", service = "trip-service" }));
app.MapGet("/health/ready", () => Results.Ok(new { status = "Ready", service = "trip-service" }));
app.MapGet("/info", (IHostEnvironment env) =>
    Results.Ok(new { serviceName = "trip-service", environment = env.EnvironmentName }));

app.Run();
