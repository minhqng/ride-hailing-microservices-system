using Microsoft.EntityFrameworkCore;
using RideHailing.Observability;
using RideHailing.TripService.Api;
using RideHailing.TripService.Application.Trips.CreateRide;
using RideHailing.TripService.Infrastructure.Persistence;

// Bootstrap skeleton for RideHailing.TripService (Trip service).
// Owns: see src/Services/Trip/RideHailing.TripService/README.md.
// W4: registers Trip-local CreateRide application dependencies and maps the
// frozen POST /trips transport adapter. Authentication infrastructure (JWT
// issuance/validation, Gateway forwarding) is a shared/C-owned concern: the
// endpoint consumes an already-authenticated ClaimsPrincipal and anonymous
// requests safely receive 401 until shared middleware populates it.

var builder = WebApplication.CreateBuilder(args);
builder.AddRideTelemetry("trip-service");
builder.Services.AddHealthChecks();

// (A-W02-02) Trip-owned PostgreSQL context. Reads ONLY the Trip-owned
// connection string (key "Postgres", supplied as ConnectionStrings__Postgres
// by compose / C-owned bootstrap). Fail-fast on missing configuration: no
// hard-coded password, no fallback credential, no foreign database.
// NOTE: production startup does NOT auto-migrate; integration tests apply
// migrations explicitly via Database.MigrateAsync() on disposable databases.
var postgresConnection = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException(
        "Missing Trip PostgreSQL connection string 'ConnectionStrings:Postgres'. " +
        "Provide ConnectionStrings__Postgres (see compose.yaml / deploy/postgres/init).");
builder.Services.AddDbContext<TripDbContext>(options => options.UseNpgsql(postgresConnection));

// W4-local application dependencies: scoped handler/DbContext per request,
// single system clock, stateless deterministic fare policy needs no service.
builder.Services.AddScoped<CreateRideHandler>();
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);

var app = builder.Build();

app.MapGet("/health/live", () => Results.Ok(new { status = "Live", service = "trip-service" }));
app.MapRideReadiness();
app.MapGet("/info", (IHostEnvironment env) =>
    Results.Ok(new { serviceName = "trip-service", environment = env.EnvironmentName }));

app.MapTripEndpoints();

app.Run();

// Minimal-host accessibility for WebApplicationFactory<Program> integration
// tests (standard ASP.NET Core test-host pattern). Exposes no other internals.
public partial class Program
{
}
