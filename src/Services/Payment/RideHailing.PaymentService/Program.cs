using RideHailing.Observability;
// Development Payment simulator. Production operation deduplication starts in W5.

using Npgsql;
using System.Text.Json.Serialization;
using RideHailing.PaymentService.Domain;
using RideHailing.PaymentService.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
builder.AddRideTelemetry("payment-service");
builder.Services.AddHealthChecks();
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false)));

var connectionString = builder.Configuration.GetConnectionString("Postgres");
if (!string.IsNullOrWhiteSpace(connectionString))
{
    builder.Services.AddSingleton(NpgsqlDataSource.Create(connectionString));
    builder.Services.AddSingleton<PaymentOperationRepository>();
    builder.Services.AddSingleton<PaymentSimulator>();
}

var app = builder.Build();

if (builder.Configuration["RabbitMq:Host"] is { Length: > 0 } rabbitHost)
{
    using var bootstrapTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    await RideHailing.Messaging.Topology.BrokerBootstrapV1.DeclareAsync(rabbitHost,
        builder.Configuration["RabbitMq:Username"] ?? "ride_app",
        builder.Configuration["RabbitMq:Password"] ?? "ride_dev_pw", bootstrapTimeout.Token);
}



if (!string.IsNullOrWhiteSpace(connectionString))
{
    await app.Services.GetRequiredService<PaymentOperationRepository>()
        .InitializeAsync(CancellationToken.None);

    if (app.Environment.IsDevelopment())
    {
        app.MapPost("/simulator/operations", async (
            SimulatePaymentRequest request,
            PaymentOperationRepository repository,
            PaymentSimulator simulator,
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
            var started = operation.BeginAttempt(DateTimeOffset.UtcNow);
            await repository.SaveAsync(started, cancellationToken);
            // Once execution starts, caller disconnect must not cancel durable outcome recording.
            // A process crash can still leave PROCESSING; W7 owns reconciliation.
            using var completionTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var result = await simulator.ExecuteAsync(started, request.Mode, completionTimeout.Token);
            await repository.SaveAsync(result, completionTimeout.Token);
            // Return the canonical stored representation, including PostgreSQL timestamp precision.
            result = await repository.GetAsync(result.Id, completionTimeout.Token)
                ?? throw new InvalidOperationException("Persisted operation is missing.");

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
