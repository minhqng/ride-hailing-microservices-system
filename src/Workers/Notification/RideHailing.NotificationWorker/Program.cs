using RideHailing.Observability;
using RideHailing.NotificationWorker;
using Npgsql;
using RabbitMQ.Client;
using RideHailing.Messaging.Reliability;

var builder = Host.CreateApplicationBuilder(args);
builder.AddRideTelemetry("notification-service");

var connectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("Notification requires its owned ConnectionStrings:Postgres database.");
var source = NpgsqlDataSource.Create(connectionString);
builder.Services.AddSingleton(source);
builder.Services.AddSingleton<NotificationProjectionStore>();
builder.Services.AddSingleton<INotificationProjectionStore>(services => services.GetRequiredService<NotificationProjectionStore>());
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<NotificationEventProcessor>();

string Required(string key) => !string.IsNullOrWhiteSpace(builder.Configuration[key])
    ? builder.Configuration[key]!
    : throw new InvalidOperationException($"Missing notification configuration '{key}'.");
var factory = new ConnectionFactory
{
    HostName = Required("RabbitMq:Host"),
    Port = builder.Configuration.GetValue("RabbitMq:Port", 5672),
    UserName = Required("RabbitMq:Username"),
    Password = Required("RabbitMq:Password"),
    ClientProvidedName = "notification-service",
    AutomaticRecoveryEnabled = false,
    TopologyRecoveryEnabled = false,
};
builder.Services.AddSingleton(factory);
builder.Services.AddSingleton<ConfirmedPublisher>();
builder.Services.AddHostedService<NotificationMessagingHostedService>();

var host = builder.Build();
await host.Services.GetRequiredService<NotificationProjectionStore>().EnsureSchemaAsync();
await host.RunAsync();
