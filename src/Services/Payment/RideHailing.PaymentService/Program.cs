using RideHailing.Observability;
// Development simulator endpoints use the durable Payment execution authority.

using Npgsql;
using System.Text.Json.Serialization;
using RabbitMQ.Client;
using RideHailing.PaymentService.Domain;
using RideHailing.PaymentService.Infrastructure;
using RideHailing.Messaging.Topology;

var builder = WebApplication.CreateBuilder(args);
builder.AddRideTelemetry("payment-service");
builder.Services.AddHealthChecks();
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false)));

var connectionString = builder.Configuration.GetConnectionString("Postgres");
if (!string.IsNullOrWhiteSpace(connectionString))
{
    builder.Services.AddSingleton(NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(connectionString) { MaxPoolSize = 32 }.ConnectionString));
    builder.Services.AddSingleton<PaymentExecution>();
    builder.Services.AddSingleton<PaymentOperationRepository>();
    builder.Services.AddSingleton(new ProviderLedger(connectionString));
    builder.Services.AddSingleton<PaymentSimulator>();
}

var rabbitHost = builder.Configuration["RabbitMq:Host"];
if (!string.IsNullOrWhiteSpace(rabbitHost))
{
    var connectionFactory = new ConnectionFactory
    {
        HostName = rabbitHost,
        UserName = builder.Configuration["RabbitMq:Username"] ?? "ride_app",
        Password = builder.Configuration["RabbitMq:Password"] ?? "ride_dev_pw",
        ClientProvidedName = "payment-service-topology-bootstrap",
        AutomaticRecoveryEnabled = true
    };
    using var bootstrapTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    await using var connection = await connectionFactory.CreateConnectionAsync(bootstrapTimeout.Token);
    await RideTopology.DeclareAsync(connection, bootstrapTimeout.Token);
}

var app = builder.Build();

if (!string.IsNullOrWhiteSpace(connectionString))
{
    await app.Services.GetRequiredService<PaymentOperationRepository>()
        .InitializeAsync(CancellationToken.None);

    if (app.Environment.IsDevelopment())
    {
        app.MapPost("/simulator/operations", async (
            SimulatePaymentRequest request,
            PaymentOperationRepository repository,
            PaymentExecution execution,
            CancellationToken cancellationToken) =>
        {
            PaymentOperation operation;
            try
            {
                if (!Enum.IsDefined(request.Mode)) throw new ArgumentException("Unsupported simulator mode.");
                operation = PaymentOperation.Create(request.OperationKey, request.TripId, request.Type,
                    request.AmountMinor, request.Currency, DateTimeOffset.UtcNow);
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
            PaymentOperation result;
            try { result = await execution.ExecuteAsync(operation, request.Mode, cancellationToken); }
            catch (PaymentConflictException)
            {
                return Results.Conflict(new { error = "IDEMPOTENCY_CONFLICT" });
            }

            return result.State == PaymentOperationState.Unknown
                ? Results.Json(result, statusCode: StatusCodes.Status504GatewayTimeout)
                : result.ResultCode == "TRANSIENT_FAILURE"
                    ? Results.Json(result, statusCode: StatusCodes.Status503ServiceUnavailable)
                    : Results.Ok(result);
        });

        app.MapGet("/simulator/operations/{id:guid}", async (
            Guid id,
            PaymentOperationRepository repository,
            CancellationToken cancellationToken) =>
        {
            var operation = await repository.GetAsync(id, cancellationToken);
            return operation is null ? Results.NotFound() : Results.Ok(operation);
        });
    }
}

app.MapGet("/health/live", () => Results.Ok(new { status = "Live", service = "payment-service" }));
app.MapRideReadiness();
app.MapGet("/info", (IHostEnvironment env) =>
    Results.Ok(new
    {
        serviceName = "payment-service",
        instanceId = builder.Configuration["ServiceInstanceId"] ?? Environment.MachineName,
        environment = env.EnvironmentName
    }));

app.Run();

internal sealed record SimulatePaymentRequest(
    [property: JsonRequired] string OperationKey,
    [property: JsonRequired] Guid TripId,
    [property: JsonRequired] PaymentOperationType Type,
    [property: JsonRequired] long AmountMinor,
    [property: JsonRequired] string Currency,
    [property: JsonRequired] PaymentSimulatorMode Mode);
