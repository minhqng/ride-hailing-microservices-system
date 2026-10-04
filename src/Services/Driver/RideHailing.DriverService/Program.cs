// Driver service (Owner B). W3: FSM + location ingress, Npgsql raw, no Redis/Matching yet.
using RideHailing.DriverService.Api;
using RideHailing.DriverService.Application.Location;
using RideHailing.DriverService.Infrastructure.Persistence;
using RideHailing.Observability;

var builder = WebApplication.CreateBuilder(args);
builder.AddRideTelemetry("driver-service");
builder.Services.AddHealthChecks();
builder.Services.Configure<LocationIngressOptions>(builder.Configuration.GetSection("Location"));
builder.Services.AddSingleton(_ =>
{
    var cs = builder.Configuration.GetConnectionString("Postgres")
        ?? Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")
        ?? "Host=localhost;Port=5432;Database=driver_db;Username=driver_app;Password=driver_dev_pw";
    return new DriverRepository(cs);
});
builder.Services.AddSingleton<LocationAcceptor>();

var app = builder.Build();

app.MapGet("/health/live", () => Results.Ok(new { status = "Live", service = "driver-service" }));
app.MapRideReadiness();
app.MapGet("/info", (IHostEnvironment env) =>
    Results.Ok(new { serviceName = "driver-service", environment = env.EnvironmentName }));

DriverEndpoints.Map(app);

// Best-effort baseline ensure (idempotent SQL). Service still boots if PG is down.
try
{
    await app.Services.GetRequiredService<DriverRepository>().EnsureTablesAsync();
}
catch (Exception ex)
{
    app.Logger.LogWarning(ex, "Driver EnsureTables failed at startup; API still serves, persistence will fail until PG recovers.");
}

app.Run();
