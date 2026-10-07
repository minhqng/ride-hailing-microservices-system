// Matching service (Owner B). W4: Redis GEO projection ownership (derived only).
// W8: broker consumption (RideRequestedV1 round lookup/create; Driver
// state/location events into the derived projection) with manual ACK after
// durable effect. No reserve execution from broker messages (candidates come
// from a future pipeline); no Outbox (W9); no broker retry/DLQ mechanics
// beyond bounded requeue (C platform scope).
// Startup loads the Lua function best-effort; the service still boots if Redis is down.
// Calls self-heal a missing function library (reload-once-and-retry on the
// missing-function server error), so recovery needs no restart after Redis
// restarts or FUNCTION FLUSH.
// B-W02-02 seam: typed Driver HTTP client (no retry handlers by design, Doc 02 §19).
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RideHailing.MatchingService.Api;
using RideHailing.MatchingService.Application.Matching;
using RideHailing.MatchingService.Application.Messaging;
using RideHailing.MatchingService.Application.Projection;
using RideHailing.MatchingService.Domain.Matching;
using RideHailing.MatchingService.Infrastructure.DriverClient;
using RideHailing.MatchingService.Infrastructure.Persistence;
using RideHailing.Observability;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);
builder.AddRideTelemetry("matching-service");
builder.Services.AddHealthChecks();
builder.Services.AddOptions<MatchingPolicy>()
    .Bind(builder.Configuration.GetSection("Matching"))
    .Validate(p => MatchingPolicyValidator.Validate(p) is null, "Invalid Matching policy configuration.")
    .ValidateOnStart();
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

builder.Services.Configure<RabbitMqOptions>(builder.Configuration.GetSection(RabbitMqOptions.SectionName));
builder.Services.AddSingleton(_ =>
{
    var postgres = builder.Configuration.GetConnectionString("Postgres")
        ?? Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")
        ?? throw new InvalidOperationException(
            "Missing Matching PostgreSQL connection string 'ConnectionStrings:Postgres'.");
    return new MatchingAttemptRepository(postgres);
});

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
builder.Services.AddSingleton(_ => new ConnectionFactory
{
    HostName = brokerOptions.Host,
    Port = brokerOptions.Port,
    UserName = brokerOptions.Username,
    Password = brokerOptions.Password,
    ClientProvidedName = "matching-service/broker-consumers",
    AutomaticRecoveryEnabled = true,
});
builder.Services.AddSingleton<RideRequestedHandler>();
builder.Services.AddHostedService<RideRequestedConsumer>();

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
    builder.Services.AddSingleton<RedisProjectionUpdater>();
    builder.Services.AddSingleton<DriverProjectionHandler>();
    builder.Services.AddHostedService<DriverProjectionConsumer>();
}

var app = builder.Build();

app.MapGet("/health/live", () => Results.Ok(new { status = "Live", service = "matching-service" }));
app.MapRideReadiness();
app.MapGet("/info", (IHostEnvironment env) =>
    Results.Ok(new { serviceName = "matching-service", environment = env.EnvironmentName }));

<<<<<<< HEAD
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
    using var pipelineMux = await RedisProjectionUpdater.ConnectAsync(redisCs);
    await new RedisProjectionUpdater(pipelineMux).EnsureFunctionAsync();
    app.Logger.LogInformation("Redis projection function loaded.");
}
catch (Exception ex)
{
    app.Logger.LogWarning(ex, "Redis projection function load failed at startup; projection calls will report Degraded until Redis recovers.");
}

app.Run();
