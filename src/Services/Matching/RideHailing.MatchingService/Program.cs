// Matching service (Owner B). W4: Redis GEO projection ownership (derived only).
// The projection updater is exercised directly in W4 (broker wiring is W8).
// Startup loads the Lua function best-effort; the service still boots if Redis is down.
// Calls self-heal a missing function library (reload-once-and-retry on the
// missing-function server error), so recovery needs no restart after Redis
// restarts or FUNCTION FLUSH.
// B-W02-02 seam: typed Driver HTTP client (no retry handlers by design, Doc 02 §19).
using RideHailing.MatchingService.Api;
using RideHailing.MatchingService.Application.Matching;
using RideHailing.MatchingService.Application.Projection;
using RideHailing.MatchingService.Domain.Matching;
using RideHailing.MatchingService.Infrastructure.DriverClient;
using RideHailing.MatchingService.Infrastructure.Persistence;
using RideHailing.Observability;

var builder = WebApplication.CreateBuilder(args);
builder.AddRideTelemetry("matching-service");
builder.Services.AddHealthChecks();
builder.Services.Configure<MatchingPolicy>(builder.Configuration.GetSection("Matching"));
builder.Services.AddSingleton(_ =>
{
    var cs = builder.Configuration.GetConnectionString("Postgres")
        ?? Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")
        ?? "Host=localhost;Port=5432;Database=matching_db;Username=matching_app;Password=matching_dev_pw";
    return new MatchingRepository(cs);
});
builder.Services.AddSingleton(_ =>
{
    var cs = builder.Configuration["Redis__ConnectionString"]
        ?? Environment.GetEnvironmentVariable("Redis__ConnectionString")
        ?? "localhost:6379";
    return new SharedRedisMultiplexer(cs);
});
builder.Services.AddSingleton(sp => new CandidateDiscovery(
    ct => sp.GetRequiredService<SharedRedisMultiplexer>().GetDatabaseAsync(ct)));
builder.Services.AddSingleton<MatchingMetrics>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<MatchingRoundService>();
builder.Services.AddHttpClient<IDriverClient, HttpDriverClient>((_, http) =>
{
    var baseUrl = builder.Configuration["Services:DriverBaseUrl"]
        ?? Environment.GetEnvironmentVariable("Services__DriverBaseUrl")
        ?? "http://localhost:8102";
    http.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
    var seconds = builder.Configuration.GetValue("DriverClient:TimeoutSeconds", 5);
    http.Timeout = TimeSpan.FromSeconds(seconds);
});

var app = builder.Build();

app.MapGet("/health/live", () => Results.Ok(new { status = "Live", service = "matching-service" }));
app.MapRideReadiness();
app.MapGet("/info", (IHostEnvironment env) =>
    Results.Ok(new { serviceName = "matching-service", environment = env.EnvironmentName }));

MatchingEndpoints.Map(app);

// Best-effort baseline ensure (idempotent SQL). Service still boots if PG is down.
try
{
    var pgCs = builder.Configuration.GetConnectionString("Postgres")
        ?? Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")
        ?? "Host=localhost;Port=5432;Database=matching_db;Username=matching_app;Password=matching_dev_pw";
    await using var conn = new Npgsql.NpgsqlConnection(pgCs);
    await conn.OpenAsync();
    await using var cmd = new Npgsql.NpgsqlCommand(MatchingSqlBaseline.Load(), conn);
    await cmd.ExecuteNonQueryAsync();
    app.Logger.LogInformation("Matching PostgreSQL baseline ensured.");
}
catch (Exception ex)
{
    app.Logger.LogWarning(ex, "Matching EnsureTables failed at startup; API still serves, persistence will fail until PG recovers.");
}

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
