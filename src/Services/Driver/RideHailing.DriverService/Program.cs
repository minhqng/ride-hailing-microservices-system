// Driver service (Owner B). W3: FSM + location ingress, Npgsql raw, no Redis/Matching yet.
// W9: authoritative Driver events use C's shared transactional Outbox and relay.
using Microsoft.AspNetCore.RateLimiting;
using Npgsql;
using RabbitMQ.Client;
using RideHailing.DriverService.Api;
using RideHailing.DriverService.Application.Location;
using RideHailing.DriverService.Application.Messaging;
using RideHailing.DriverService.Infrastructure.Faults;
using RideHailing.DriverService.Infrastructure.Outbox;
using RideHailing.DriverService.Infrastructure.Persistence;
using RideHailing.DriverService.Infrastructure.Telemetry;
using RideHailing.Messaging.Outbox;
using RideHailing.Messaging.Reliability;
using RideHailing.Observability;
using RideHailing.Security;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRideAuthentication(builder.Configuration);
RideHailing.Messaging.FaultPoints.Current = RideHailing.Messaging.Faults.FaultPoint.ForHostEnvironment(builder.Environment.EnvironmentName);
builder.AddRideTelemetry("driver-service");
builder.AddDriverTelemetry();
DriverIngressLimits.Apply(builder);
builder.Services.AddHealthChecks();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy(DriverRateLimitPolicy.Location, DriverRateLimitPolicy.LocationPartition);
});
builder.Services.Configure<LocationIngressOptions>(builder.Configuration.GetSection("Location"));

// Fail-fast on missing configuration like TripService: no hard-coded
// password, no fallback credential, no foreign database. Compose and
// test fixtures always supply ConnectionStrings__Postgres explicitly.
var connectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")
    ?? throw new InvalidOperationException(
        "Missing Driver PostgreSQL connection string 'ConnectionStrings:Postgres'. " +
        "Provide ConnectionStrings__Postgres (see compose.yaml / deploy/postgres/init).");

builder.Services.AddSingleton(NpgsqlDataSource.Create(connectionString));
builder.Services.AddSingleton<NpgsqlOutboxStore>();
builder.Services.AddSingleton<IOutboxStore>(sp => sp.GetRequiredService<NpgsqlOutboxStore>());
builder.Services.AddSingleton<DriverMetrics>();
builder.Services.AddSingleton(sp => new DriverRepository(
    sp.GetRequiredService<NpgsqlDataSource>(),
    sp.GetRequiredService<NpgsqlOutboxStore>(),
    sp.GetRequiredService<DriverMetrics>()));
builder.Services.AddSingleton<DriverCommandHandler>();
builder.Services.AddSingleton<LocationAcceptor>();
builder.Services.AddSingleton(sp => DriverFaultPoint.FromEnvironment(
    builder.Environment, sp.GetRequiredService<ILogger<DriverFaultPoint>>()));

var brokerOptions = new DriverBrokerOptions();
builder.Configuration.GetSection(DriverBrokerOptions.SectionName).Bind(brokerOptions);
var brokerError = brokerOptions.Validate();
if (brokerError is not null)
{
    throw new InvalidOperationException($"Invalid RabbitMQ configuration: {brokerError}");
}

builder.Services.Configure<DriverBrokerOptions>(
    builder.Configuration.GetSection(DriverBrokerOptions.SectionName));
builder.Services.AddSingleton<DriverEventPublisher>();

var outboxOptions = new OutboxRelayOptions();
builder.Configuration.GetSection("Outbox").Bind(outboxOptions);
outboxOptions.Validate();

var connectionFactory = RideHailing.Messaging.Topology.RabbitConnectionFactory.Create(
    brokerOptions.Hosts ?? brokerOptions.Host, brokerOptions.Port,
    brokerOptions.Username, brokerOptions.Password, "driver-service/outbox-relay");
builder.Services.AddSingleton(connectionFactory);
builder.Services.AddSingleton<ConfirmedPublisher>();
builder.Services.AddSingleton<IOutboxPublisher, RabbitOutboxPublisher>();
builder.Services.AddSingleton(outboxOptions);
builder.Services.AddSingleton<OutboxRelay>();

if (brokerOptions.PublishingEnabled)
{
    builder.Services.AddHostedService<DriverOutboxRelayHost>();
}

if (brokerOptions.CommandsEnabled)
{
    builder.Services.AddHostedService<DriverCommandConsumerHost>();
}

var app = builder.Build();
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();
app.UseDriverAccessGuard();

app.MapGet("/health/live", () => Results.Ok(new { status = "Live", service = "driver-service" }));
app.MapRideReadiness();
app.MapGet("/info", (IHostEnvironment env) =>
    Results.Ok(new { serviceName = "driver-service", environment = env.EnvironmentName }));

DriverEndpoints.Map(app);

var faultPoint = app.Services.GetRequiredService<DriverFaultPoint>();
if (app.Environment.IsEnvironment("Testing") && faultPoint.Enabled)
{
    // FromEnvironment refuses to enable the seam without this token, so the
    // non-empty value here is part of the same fail-closed contract.
    var controlToken = Environment.GetEnvironmentVariable(DriverFaultPoint.ControlTokenVariable)!;
    DriverFaultEndpoints.Map(app, faultPoint, controlToken);
    app.Logger.LogWarning(
        "B-W11 fault control endpoints enabled for the explicit Testing environment.");
}

// Idempotent baseline + canonical shared Outbox schema. Both are applied
// before the host starts serving. If PostgreSQL is down the process still
// boots (same W2 behaviour), but every authoritative write fails closed
// because the state row and its required Outbox intent share one transaction.
try
{
    await app.Services.GetRequiredService<DriverRepository>().EnsureTablesAsync();
}
catch (Exception ex)
{
    app.Logger.LogWarning(ex,
        "Driver EnsureTables failed at startup; API still serves, persistence will fail until PG recovers.");
}

try
{
    await DriverOutboxSchema.EnsureAsync(
        app.Services.GetRequiredService<NpgsqlDataSource>());
}
catch (Exception ex)
{
    app.Logger.LogError(ex,
        "Driver Outbox schema bootstrap failed. Authoritative state writes will fail closed " +
        "until the shared schema is available.");
}

app.Run();
