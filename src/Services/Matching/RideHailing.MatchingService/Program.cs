// Bootstrap skeleton for RideHailing.MatchingService (Matching service).
// Owns: see src/Services/Matching/RideHailing.MatchingService/README.md. No business endpoints yet.
// TODO(W3+): add owner-specific domain, persistence, messaging per Docs 00-05.

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHealthChecks();

var app = builder.Build();

app.MapGet("/health/live", () => Results.Ok(new { status = "Live", service = "matching-service" }));
app.MapGet("/health/ready", () => Results.Ok(new { status = "Ready", service = "matching-service" }));
app.MapGet("/info", (IHostEnvironment env) =>
    Results.Ok(new { serviceName = "matching-service", environment = env.EnvironmentName }));

app.Run();
