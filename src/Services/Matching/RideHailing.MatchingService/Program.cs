// Matching service (Owner B). W4: Redis GEO projection ownership (derived only).
// The projection updater is exercised directly in W4 (broker wiring is W8).
// Startup loads the Lua function best-effort; the service still boots if Redis is down.
using RideHailing.MatchingService.Application.Projection;
using RideHailing.Observability;

var builder = WebApplication.CreateBuilder(args);
builder.AddRideTelemetry("matching-service");
builder.Services.AddHealthChecks();

var app = builder.Build();

app.MapGet("/health/live", () => Results.Ok(new { status = "Live", service = "matching-service" }));
app.MapRideReadiness();
app.MapGet("/info", (IHostEnvironment env) =>
    Results.Ok(new { serviceName = "matching-service", environment = env.EnvironmentName }));

try
{
    var redisCs = builder.Configuration["Redis__ConnectionString"]
        ?? Environment.GetEnvironmentVariable("Redis__ConnectionString")
        ?? "localhost:6379";
    using var mux = await RedisProjectionUpdater.ConnectAsync(redisCs);
    await new RedisProjectionUpdater(mux).EnsureFunctionAsync();
    app.Logger.LogInformation("Redis projection function loaded.");
}
catch (Exception ex)
{
    app.Logger.LogWarning(ex, "Redis projection function load failed at startup; projection calls will report Degraded until Redis recovers.");
}

app.Run();
