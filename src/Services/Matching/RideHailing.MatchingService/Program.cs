// Matching service (Owner B). W4: Redis GEO projection ownership (derived only).
// W8: broker consumption (RideRequestedV1 durable intake; Driver
// state/location events into the derived projection) with manual ACK after
// durable effect. W9: NO_DRIVER terminal state and MatchingFailedV1 Outbox
// intent commit in one transaction; C's shared relay publishes committed
// intents. B-REC-01: the intake persists pickup + auto_execute before ACK and
// MatchingWorkDispatcher autonomously executes reserve/reconcile/stages.
// Broker retry/DLQ mechanics beyond bounded requeue remain C platform scope.
// Startup loads the Lua function best-effort; the service still boots if Redis is down.
// Calls self-heal a missing function library (reload-once-and-retry on the
// missing-function server error), so recovery needs no restart after Redis
// restarts or FUNCTION FLUSH.
// B-W02-02 seam: typed Driver HTTP client (no retry handlers by design, Doc 02 §19).
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using RabbitMQ.Client;
using RideHailing.MatchingService.Api;
using RideHailing.MatchingService.Application.Matching;
using RideHailing.MatchingService.Application.Messaging;
using RideHailing.MatchingService.Application.Projection;
using RideHailing.MatchingService.Application.Reserve;
using RideHailing.MatchingService.Domain.Matching;
using RideHailing.MatchingService.Infrastructure.DriverClient;
using RideHailing.MatchingService.Infrastructure.Outbox;
using RideHailing.MatchingService.Infrastructure.Persistence;
using RideHailing.Messaging.Outbox;
using RideHailing.Messaging.Reliability;
using RideHailing.Observability;
using RideHailing.Messaging.Topology;
using StackExchange.Redis;
using RideHailing.Security;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRideAuthentication(builder.Configuration);
builder.Services.AddTransient<ServiceTokenHandler>();
RideHailing.Messaging.FaultPoints.Current = RideHailing.Messaging.Faults.FaultPoint.ForHostEnvironment(builder.Environment.EnvironmentName);
builder.AddRideTelemetry("matching-service");
builder.AddMatchingTelemetry();
builder.Services.AddHealthChecks();

var postgresConnectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")
    ?? "Host=localhost;Port=5432;Database=matching_db;Username=matching_app;Password=matching_dev_pw";

builder.Services.AddOptions<MatchingPolicy>()
    .Bind(builder.Configuration.GetSection("Matching"))
    .Validate(p => MatchingPolicyValidator.Validate(p) is null, "Invalid Matching policy configuration.")
    .ValidateOnStart();
builder.Services.AddSingleton(_ => new MatchingRepository(postgresConnectionString));
builder.Services.AddSingleton(NpgsqlDataSource.Create(postgresConnectionString));
builder.Services.AddSingleton<NpgsqlOutboxStore>();
builder.Services.AddSingleton<IOutboxStore>(sp => sp.GetRequiredService<NpgsqlOutboxStore>());
builder.Services.AddSingleton(sp => new MatchingTerminalizer(
    sp.GetRequiredService<NpgsqlDataSource>(),
    sp.GetRequiredService<NpgsqlOutboxStore>(),
    sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<MatchingMetrics>()));builder.Services.AddSingleton(_ =>
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
builder.Services.AddSingleton(sp => new MatchingWorkRepository(
    postgresConnectionString, sp.GetRequiredService<MatchingMetrics>()));
builder.Services.AddSingleton(sp => new MatchingStageService(
    sp.GetRequiredService<MatchingAttemptRepository>(),
    sp.GetRequiredService<MatchingWorkRepository>(),
    sp.GetRequiredService<CandidateDiscovery>(),
    sp.GetRequiredService<MatchingMetrics>(),
    sp.GetRequiredService<TimeProvider>()));
builder.Services.AddHttpClient<IDriverClient, HttpDriverClient>((_, http) =>
{
    var baseUrl = builder.Configuration["Services:DriverBaseUrl"]
        ?? Environment.GetEnvironmentVariable("Services__DriverBaseUrl")
        ?? "http://localhost:8102";
    http.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
    var seconds = builder.Configuration.GetValue("DriverClient:TimeoutSeconds", 5);
    http.Timeout = TimeSpan.FromSeconds(seconds);
}).AddHttpMessageHandler<ServiceTokenHandler>();
builder.Services.AddScoped(sp => new MatchingReserveOrchestrator(
    sp.GetRequiredService<MatchingAttemptRepository>(),
    sp.GetRequiredService<IDriverClient>(),
    sp.GetRequiredService<MatchingTerminalizer>(),
    metrics: sp.GetRequiredService<MatchingMetrics>()));

// B-REC-01: the autonomous production trigger. The broker intake commits
// durable work before ACK; this hosted worker discovers it from PostgreSQL
// and drives reserve/reconcile/terminalization without any test-only call.
builder.Services.AddOptions<MatchingWorkerOptions>()
    .Bind(builder.Configuration.GetSection(MatchingWorkerOptions.SectionName))
    .Validate(o => o.Validate() is null, "Invalid Matching worker configuration.")
    .ValidateOnStart();
builder.Services.AddSingleton(sp => new MatchingWorkDispatcher(
    sp.GetRequiredService<IServiceScopeFactory>(),
    sp.GetRequiredService<MatchingAttemptRepository>(),
    sp.GetRequiredService<MatchingWorkRepository>(),
    sp.GetRequiredService<MatchingStageService>(),
    sp.GetRequiredService<MatchingTerminalizer>(),
    sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<MatchingPolicy>>(),
    sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<MatchingWorkerOptions>>(),
    sp.GetRequiredService<TimeProvider>(),
    postgresConnectionString,
    sp.GetRequiredService<ILogger<MatchingWorkDispatcher>>()));
builder.Services.AddHostedService(sp => sp.GetRequiredService<MatchingWorkDispatcher>());

builder.Services.Configure<RabbitMqOptions>(builder.Configuration.GetSection(RabbitMqOptions.SectionName));
builder.Services.AddSingleton(sp => new MatchingAttemptRepository(
    postgresConnectionString, sp.GetRequiredService<MatchingMetrics>()));

RabbitMqOptions BrokerOptions()
{
    var options = new RabbitMqOptions();
    builder.Configuration.GetSection(RabbitMqOptions.SectionName).Bind(options);
    var error = options.Validate();
    if (error is not null)
    {
        throw new InvalidOperationException($"Invalid RabbitMQ configuration: {error}");
    }

    return options;
}

var brokerOptions = BrokerOptions();
builder.Services.AddSingleton(_ => RabbitConnectionFactory.Create(
    brokerOptions.Hosts ?? brokerOptions.Host,
    brokerOptions.Port,
    brokerOptions.Username,
    brokerOptions.Password,
    "matching-service/broker-consumers",
    automaticRecovery: true));
builder.Services.AddSingleton<RideRequestedHandler>();
builder.Services.AddHostedService<RideRequestedConsumer>();

// B-REC-01: canonical cancellation input (Doc 02 §12.9). The durable intent
// survives a cancel arriving before its RideRequestedV1.
builder.Services.AddSingleton<CancelMatchingRoundHandler>();
builder.Services.AddHostedService<CancelMatchingRoundConsumer>();

// W9: C's shared Outbox relay publishes only committed intents. Matching owns
// only the intent creation (same transaction as the NO_DRIVER row) and this
// lifecycle/topology adapter; claim/publish-confirm/mark semantics stay in
// RideHailing.Messaging.Outbox.
var outboxOptions = new OutboxRelayOptions();
builder.Configuration.GetSection("Outbox").Bind(outboxOptions);
outboxOptions.Validate();
builder.Services.AddSingleton(outboxOptions);
builder.Services.AddSingleton<MatchingOutboxBroker>();
builder.Services.AddSingleton(sp =>
    new ConfirmedPublisher(sp.GetRequiredService<MatchingOutboxBroker>().Factory));
builder.Services.AddSingleton<IOutboxPublisher, RabbitOutboxPublisher>();
builder.Services.AddSingleton<OutboxRelay>();
if (brokerOptions.PublishingEnabled)
{
    builder.Services.AddHostedService<MatchingOutboxRelayHost>();
}

// Projection ingestion needs Redis. Connect once here: on success the mux,
// updater, handler and consumer are all registered; on failure only a
// warning is logged and the projection path stays down until restart — the
// round consumer above is unaffected (blast-radius isolation; derived state
// rebuilds from authority later).
IConnectionMultiplexer? mux = null;
using (var bootLogs = LoggerFactory.Create(logging => logging.AddConsole()))
{
    var bootLog = bootLogs.CreateLogger("MatchingStartup");
    try
    {
        var redisCs = builder.Configuration["Redis__ConnectionString"]
            ?? Environment.GetEnvironmentVariable("Redis__ConnectionString")
            ?? "localhost:6379";
        mux = await RedisProjectionUpdater.ConnectAsync(redisCs);
        await new RedisProjectionUpdater(mux).EnsureFunctionAsync();
        bootLog.LogInformation("Redis projection function loaded.");
    }
    catch (Exception ex)
    {
        bootLog.LogWarning(
            ex,
            "Redis unavailable at startup; projection ingestion is disabled until restart. Round consumption unaffected.");
    }
}

if (mux is not null)
{
    builder.Services.AddSingleton(mux);
    builder.Services.AddSingleton(sp => new RedisProjectionUpdater(
        sp.GetRequiredService<IConnectionMultiplexer>(),
        metrics: sp.GetRequiredService<MatchingMetrics>()));
    builder.Services.AddSingleton<DriverProjectionHandler>();
    builder.Services.AddHostedService<DriverProjectionConsumer>();
}

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/internal/matching"))
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        if (!RideAuthentication.HasServicePermission(context.User, "trip", "matching:manage"))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
    }
    await next(context);
});

app.MapGet("/health/live", () => Results.Ok(new { status = "Live", service = "matching-service" }));
app.MapRideReadiness();
app.MapGet("/info", (IHostEnvironment env) =>
    Results.Ok(new { serviceName = "matching-service", environment = env.EnvironmentName }));

MatchingEndpoints.Map(app);

if (app.Environment.IsEnvironment("Testing"))
{
    var controlToken = Environment.GetEnvironmentVariable("RIDE_FAULT_CONTROL_TOKEN");
    if (!string.IsNullOrWhiteSpace(controlToken))
    {
        MatchingTestEndpoints.Map(app, controlToken);
        app.Logger.LogWarning(
            "B-W11 Matching test-control endpoints enabled for the explicit Testing environment.");
    }
}

// Best-effort baseline ensure (idempotent SQL). Service still boots if PG is down.
try
{
    await RideHailing.MatchingService.Infrastructure.Persistence.MatchingSchemaBootstrap
        .EnsureBaselineAsync(postgresConnectionString);
    app.Logger.LogInformation("Matching PostgreSQL baseline ensured.");
}
catch (Exception ex)
{
    app.Logger.LogWarning(ex, "Matching EnsureTables failed at startup; API still serves, persistence will fail until PG recovers.");
}

// Canonical shared Outbox schema. If this fails the service still boots, but
// every terminal write fails closed (NO_DRIVER + intent are indivisible), so
// no terminal can be committed without its durable publication intent.
try
{
    await MatchingOutboxSchema.EnsureAsync(
        app.Services.GetRequiredService<NpgsqlDataSource>());
    app.Logger.LogInformation("Matching Outbox schema ensured.");
}
catch (Exception ex)
{
    app.Logger.LogError(ex,
        "Matching Outbox schema bootstrap failed. Terminal writes will fail closed until the shared schema is available.");
}

try
{
    var redisCs = builder.Configuration["Redis__ConnectionString"]
        ?? Environment.GetEnvironmentVariable("Redis__ConnectionString")
        ?? "localhost:6379";
    using var pipelineMux = await RedisProjectionUpdater.ConnectAsync(redisCs);
    await new RedisProjectionUpdater(pipelineMux).EnsureFunctionAsync();
    app.Logger.LogInformation("Redis projection function loaded.");
}
catch (Exception ex)
{
    app.Logger.LogWarning(ex, "Redis projection function load failed at startup; projection calls will report Degraded until Redis recovers.");
}

app.Run();

// Minimal-host accessibility for WebApplicationFactory<Program> integration
// proofs (same pattern as the Trip service). No production behavior.
public partial class Program;
