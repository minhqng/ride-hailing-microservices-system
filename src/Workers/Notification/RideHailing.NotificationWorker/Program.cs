using RideHailing.Observability;
using RideHailing.NotificationWorker;

var builder = Host.CreateApplicationBuilder(args);
builder.AddRideTelemetry("notification-service");
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
