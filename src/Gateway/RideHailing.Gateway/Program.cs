using RideHailing.Observability;
using RideHailing.Security;
using Yarp.ReverseProxy.Configuration;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRideAuthentication(builder.Configuration);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 64 * 1024);
builder.AddRideTelemetry("gateway-service");
builder.Services.AddHealthChecks();

RouteConfig Route(string id, string cluster, string path, string[] methods, string policy = "default") => new()
{
    RouteId = id,
    ClusterId = cluster,
    Match = new RouteMatch { Path = path, Methods = methods },
    AuthorizationPolicy = policy,
    Transforms = new IReadOnlyDictionary<string, string>[]
    {
        new Dictionary<string, string> { ["RequestHeaderRemove"] = "X-Driver-Id" },
        new Dictionary<string, string> { ["RequestHeaderRemove"] = "X-Rider-Id" },
        new Dictionary<string, string> { ["RequestHeaderRemove"] = "X-User-Id" },
        new Dictionary<string, string> { ["RequestHeaderRemove"] = "X-Role" },
    },
};

ClusterConfig Cluster(string name, string fallback) => new()
{
    ClusterId = name,
    Destinations = new Dictionary<string, DestinationConfig>
    {
        ["primary"] = new() { Address = builder.Configuration[$"Services:{name}BaseUrl"] ?? fallback },
    },
};

builder.Services.AddReverseProxy().LoadFromMemory(
    [Route("register", "Identity", "/auth/register", ["POST"], "anonymous"),
     Route("login", "Identity", "/auth/login", ["POST"], "anonymous"),
     Route("trips", "Trip", "/trips/{**remainder}", ["GET", "POST"]),
     Route("driver-create", "Driver", "/drivers", ["POST"]),
     Route("driver-read", "Driver", "/drivers/{id:guid}", ["GET"]),
     Route("driver-online", "Driver", "/drivers/{id:guid}/online", ["POST"]),
     Route("driver-offline", "Driver", "/drivers/{id:guid}/offline", ["POST"]),
     Route("driver-location", "Driver", "/drivers/{id:guid}/location", ["POST"]),
     Route("notifications", "Notification", "/notifications/{**remainder}", ["GET", "POST"])],
    [Cluster("Identity", "http://identity:8080/"), Cluster("Trip", "http://trip:8080/"),
     Cluster("Driver", "http://driver:8080/"), Cluster("Notification", "http://notification:8080/")]);

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapReverseProxy();

app.MapGet("/health/live", () => Results.Ok(new { status = "Live", service = "gateway-service" }));
// Stateless edge: no owned database, so process-only readiness is correct.
app.MapRideReadiness(requiresOwnedDatabase: false);
app.MapGet("/info", (IHostEnvironment env) =>
    Results.Ok(new { serviceName = "gateway-service", environment = env.EnvironmentName }));

app.Run();
