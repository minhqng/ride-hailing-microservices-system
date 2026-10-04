using Microsoft.EntityFrameworkCore;
using RideHailing.Observability;
using RideHailing.TripService.Infrastructure.Persistence;

// Bootstrap skeleton for RideHailing.TripService (Trip service).
// Owns: see src/Services/Trip/RideHailing.TripService/README.md. No business endpoints yet.
// TODO(W3+): add owner-specific domain, persistence, messaging per Docs 00-05.

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

var app = builder.Build();

app.MapGet("/health/live", () => Results.Ok(new { status = "Live", service = "trip-service" }));
app.MapRideReadiness();
app.MapGet("/info", (IHostEnvironment env) =>
    Results.Ok(new { serviceName = "trip-service", environment = env.EnvironmentName }));

app.Run();

// Minimal-host accessibility for WebApplicationFactory<Program> integration
// tests (standard ASP.NET Core test-host pattern). Exposes no other internals.
public partial class Program
{
}
