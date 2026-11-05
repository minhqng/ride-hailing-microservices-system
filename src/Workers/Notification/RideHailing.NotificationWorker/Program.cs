using RideHailing.Observability;
using RideHailing.NotificationWorker;
using Npgsql;
using RideHailing.Messaging.Reliability;
using RideHailing.Messaging.Topology;

var builder = WebApplication.CreateBuilder(args);
builder.AddRideTelemetry("notification-service");
RideHailing.Security.RideAuthentication.AddRideAuthentication(builder.Services, builder.Configuration);

var connectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("Notification requires its owned ConnectionStrings:Postgres database.");
var source = NpgsqlDataSource.Create(connectionString);
builder.Services.AddSingleton(source);
builder.Services.AddSingleton<NotificationProjectionStore>();
builder.Services.AddSingleton<INotificationProjectionStore>(services => services.GetRequiredService<NotificationProjectionStore>());
builder.Services.AddSingleton<INotificationInboxStore>(services => services.GetRequiredService<NotificationProjectionStore>());
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<NotificationEventProcessor>();

if (builder.Configuration.GetValue("NotificationMessaging:Enabled", true))
{
    string Required(string key) => !string.IsNullOrWhiteSpace(builder.Configuration[key])
        ? builder.Configuration[key]!
        : throw new InvalidOperationException($"Missing notification configuration '{key}'.");
    var factory = RabbitConnectionFactory.Create(
        builder.Configuration["RabbitMq:Hosts"] ?? Required("RabbitMq:Host"),
        builder.Configuration.GetValue("RabbitMq:Port", 5672),
        Required("RabbitMq:Username"), Required("RabbitMq:Password"), "notification-service");
    builder.Services.AddSingleton(factory);
    builder.Services.AddSingleton<ConfirmedPublisher>();
    builder.Services.AddHostedService<NotificationMessagingHostedService>();
}

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health/live", () => Results.Ok(new { status = "Live", service = "notification-service" }));
app.MapGet("/health/ready", async (NotificationProjectionStore store, CancellationToken token) =>
    await store.PingAsync(token)
        ? Results.Ok(new { status = "Ready", service = "notification-service" })
        : Results.Json(new { status = "NotReady", service = "notification-service" },
            statusCode: StatusCodes.Status503ServiceUnavailable));
app.MapGet("/info", (IHostEnvironment env) =>
    Results.Ok(new { serviceName = "notification-service", environment = env.EnvironmentName }));

// DS03: recipient-addressed in-app inbox, served by the existing Notification
// component with its own notification_db and asynchronous worker.
app.MapNotificationInboxEndpoints();

await app.Services.GetRequiredService<NotificationProjectionStore>().EnsureSchemaAsync();
await app.RunAsync();

public partial class Program
{
}
