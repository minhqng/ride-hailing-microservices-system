// Bootstrap skeleton for RideHailing.PaymentService (Payment service).
// Owns: see src/Services/Payment/RideHailing.PaymentService/README.md. No business endpoints yet.
// TODO(W3+): add owner-specific domain, persistence, messaging per Docs 00-05.

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHealthChecks();

var app = builder.Build();

app.MapGet("/health/live", () => Results.Ok(new { status = "Live", service = "payment-service" }));
app.MapGet("/health/ready", () => Results.Ok(new { status = "Ready", service = "payment-service" }));
app.MapGet("/info", (IHostEnvironment env) =>
    Results.Ok(new { serviceName = "payment-service", environment = env.EnvironmentName }));

app.Run();
