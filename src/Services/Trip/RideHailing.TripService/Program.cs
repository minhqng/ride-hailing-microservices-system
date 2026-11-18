using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using RideHailing.Observability;
using RideHailing.TripService.Api;
using RideHailing.TripService.Application.Trips.CreateRide;
using RideHailing.TripService.Application.Trips.Lifecycle;
using RideHailing.TripService.Infrastructure.Persistence;
using RideHailing.TripService.Integration.Messaging;
using RideHailing.TripService.Integration.Observability;

// RideHailing.TripService host: Trip lifecycle, fare snapshot, persisted Saga
// and transactional Outbox. Owns: see
// src/Services/Trip/RideHailing.TripService/README.md.
// Registers Trip-local CreateRide application dependencies and maps the
// POST /trips transport adapter. Authentication infrastructure (JWT
// issuance/validation, Gateway forwarding) is a shared platform concern: the
// endpoint consumes an already-authenticated ClaimsPrincipal and anonymous
// requests safely receive 401 until shared middleware populates it.

var builder = WebApplication.CreateBuilder(args);
RideHailing.Security.RideAuthentication.AddRideAuthentication(builder.Services, builder.Configuration);
RideHailing.Messaging.FaultPoints.Current = RideHailing.Messaging.Faults.FaultPoint.ForHostEnvironment(builder.Environment.EnvironmentName);
builder.AddRideTelemetry("trip-service");
// Trip domain spans and metrics ride the SAME OpenTelemetry
// pipeline as the shared messaging instruments — one registry, no second
// exporter. The meter is observed regardless of messaging enablement so
// saga.active reflects durable workflow state even without a broker.
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddSource(TripDiagnostics.ActivitySourceName))
    .WithMetrics(m => m.AddMeter(TripDiagnostics.MeterName));
builder.Services.AddHealthChecks();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy(TripRateLimitPolicy.CreateRide, TripRateLimitPolicy.CreateRidePartition);
});

//  Trip-owned PostgreSQL context. Reads ONLY the Trip-owned
// connection string (key "Postgres", supplied as ConnectionStrings__Postgres
// by compose / platform bootstrap). Fail-fast on missing configuration: no
// hard-coded password, no fallback credential, no foreign database.
// NOTE: production startup does NOT auto-migrate; integration tests apply
// migrations explicitly via Database.MigrateAsync() on disposable databases.
var postgresConnection = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException(
        "Missing Trip PostgreSQL connection string 'ConnectionStrings:Postgres'. " +
        "Provide ConnectionStrings__Postgres (see compose.yaml / deploy/postgres/init).");

// One pooled data source serves both EF and the shared Outbox store. The
// relay must read the SAME Trip database that owns the transaction; sharing
// the data source avoids a second credential/pool and guarantees identical
// connection settings. EF does not dispose an externally supplied source.
//
// Creation is deferred to first use: readiness must be able to report
// NotReady (503) for an unparsable owned-database configuration instead of
// crashing the host during startup. Nothing resolves the context or the relay
// before the readiness gates pass.
builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(postgresConnection));
builder.Services.AddDbContext<TripDbContext>((sp, options) =>
    options.UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>()));

// Application dependencies: scoped handler/DbContext per request, one system
// clock, stateless deterministic fare policy. The lifecycle intakes are also
// exercised directly by component tests with authoritative-shaped fixtures;
// production transport and consumers are registered by AddTripMessaging below
// when TripMessaging:Enabled is true.
builder.Services.AddScoped<CreateRideHandler>();
builder.Services.AddScoped<StartMatchingHandler>();
builder.Services.AddScoped<CancelRideHandler>();
builder.Services.AddScoped<ConfirmTripCleanupHandler>();
builder.Services.AddScoped<RecordDriverReservationHandler>();
builder.Services.AddScoped<RecordDriverAcceptedHandler>();
builder.Services.AddScoped<RecordDriverRejectedHandler>();
builder.Services.AddScoped<MarkPaymentAuthorizedHandler>();
builder.Services.AddScoped<MarkPaymentFailedHandler>();
builder.Services.AddScoped<MarkMatchingFailedHandler>();
builder.Services.AddScoped<StartTripHandler>();
builder.Services.AddScoped<CompleteTripHandler>();
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
// Public Driver lifecycle transport resolves the authenticated
// Driver's owning profile through the Driver service before the aggregate
// checks the exact assignment. The caller's own token is forwarded; no
// client-supplied actor identity is trusted.
builder.Services.AddHttpClient<RideHailing.TripService.Integration.DriverDirectory.IDriverDirectory,
    RideHailing.TripService.Integration.DriverDirectory.HttpDriverDirectory>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Services:DriverBaseUrl"] ?? "http://driver:8080/");
    client.Timeout = TimeSpan.FromSeconds(5);
});
builder.Services.AddTripMessaging(builder.Configuration, builder.Environment);
if (builder.Configuration.GetValue<bool>("TripMessaging:Enabled"))
{
    builder.Services.AddTransient<RideHailing.Security.ServiceTokenHandler>();
    builder.Services.AddHttpClient("matching-cleanup", client =>
    {
        client.BaseAddress = new Uri(builder.Configuration["Services:MatchingBaseUrl"] ?? "http://localhost:8104/");
        client.Timeout = TimeSpan.FromSeconds(5);
    }).AddHttpMessageHandler<RideHailing.Security.ServiceTokenHandler>();
    builder.Services.AddHttpClient("driver-cleanup", client =>
    {
        client.BaseAddress = new Uri(builder.Configuration["Services:DriverBaseUrl"] ?? "http://localhost:8102/");
        client.Timeout = TimeSpan.FromSeconds(5);
    }).AddHttpMessageHandler<RideHailing.Security.ServiceTokenHandler>();
    builder.Services.AddHostedService<MatchingCleanupReconciler>();
    builder.Services.AddHostedService<DriverCleanupReconciler>();
}
// Durable saga-backlog observation (saga.active{step}) runs in every
// host: it only reads the Trip-owned database and never touches the broker.
builder.Services.AddHostedService<TripMetricsObserver>();

var app = builder.Build();
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

//  Opt-in Trip auto-migrate for compose local/CI reproducibility.
// Platform contract: /health/ready requires zero pending Trip
// migrations (see TripDatabaseReadiness). Fresh postgres volumes only create
// roles/DBs (deploy/postgres/init), so without a migrate step readiness stays
// 503 and platform-smoke times out on :8103. Compose sets
// TripDatabase__AutoMigrate=true; production sets it false and runs
// migrations via an external job. Default false = preserve the historic
// "production never auto-migrates" rule.
if (app.Configuration.GetValue<bool>("TripDatabase:AutoMigrate"))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<TripDbContext>();
    const int maxAttempts = 10;
    for (var attempt = 1; ; attempt++)
    {
        try
        {
            await db.Database.MigrateAsync(app.Lifetime.ApplicationStopping);
            break;
        }
        catch (Exception ex) when (attempt < maxAttempts && !app.Lifetime.ApplicationStopping.IsCancellationRequested)
        {
            app.Logger.LogWarning(ex, "Trip auto-migrate attempt {Attempt}/{Max} failed; retrying.", attempt, maxAttempts);
            await Task.Delay(TimeSpan.FromSeconds(2), app.Lifetime.ApplicationStopping);
        }
    }
}

app.MapGet("/health/live", () => Results.Ok(new { status = "Live", service = "trip-service" }));
app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    ResponseWriter = (context, report) => context.Response.WriteAsJsonAsync(new
    {
        status = report.Status == Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy ? "Ready" : "NotReady",
        dependencies = report.Entries.ToDictionary(e => e.Key, e => e.Value.Status.ToString())
    })
});
app.MapGet("/info", (IHostEnvironment env) =>
    Results.Ok(new { serviceName = "trip-service", environment = env.EnvironmentName }));

app.MapTripEndpoints();
app.MapGetTripEndpoint();
app.MapCancelRideEndpoint();
app.MapDriverLifecycleEndpoints();

app.Run();

// Minimal-host accessibility for WebApplicationFactory<Program> integration
// tests (standard ASP.NET Core test-host pattern). Exposes no other internals.
public partial class Program
{
}
