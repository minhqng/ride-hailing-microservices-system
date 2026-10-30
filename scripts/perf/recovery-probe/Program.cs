using System.Diagnostics;
using System.Text.Json;
using RabbitMQ.Client;
using RideHailing.B12Audit.Tests;
using RideHailing.Messaging.Contracts;
using RideHailing.Messaging.Reliability;

// Verification tooling reuses the application publisher and the read-only B auditor.
// Credentials enter only through environment variables and never enter receipts.
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
if (args is ["audit"])
{
    var auditor = new BInvariantAuditor(
        Environment.GetEnvironmentVariable("W14_DRIVER_CONNECTION")!,
        Environment.GetEnvironmentVariable("W14_MATCHING_CONNECTION"), null);
    var result = await auditor.RunAsync("backlog-recovery", "POST_SCENARIO_RELAY_QUIESCED");
    Console.WriteLine(JsonSerializer.Serialize(result, json));
    return;
}

var factory = new ConnectionFactory
{
    HostName = Environment.GetEnvironmentVariable("W14_RABBIT_HOST") ?? "127.0.0.1",
    Port = int.Parse(Environment.GetEnvironmentVariable("W14_RABBIT_PORT") ?? "25673"),
    UserName = Environment.GetEnvironmentVariable("RABBITMQ_USER") ?? "ride_app",
    Password = Environment.GetEnvironmentVariable("RABBITMQ_PASSWORD") ?? "ride_dev_pw",
    AutomaticRecoveryEnabled = true,
    ClientProvidedName = "backlog-confirm-probe",
};
await using var publisher = new ConfirmedPublisher(factory);
while (await Console.In.ReadLineAsync() is { } line)
{
    var envelope = JsonSerializer.Deserialize<MessageEnvelope<JsonElement>>(line, json)
        ?? throw new InvalidDataException("Missing envelope");
    var started = Stopwatch.GetTimestamp();
    var sentAtUtc = DateTimeOffset.UtcNow;
    bool confirmed;
    string? error = null;
    try
    {
        await publisher.PublishAsync("ride.events", "trip.ride-requested.v1", envelope);
        confirmed = true;
    }
    catch (Exception exception)
    {
        confirmed = false;
        error = exception.GetType().Name;
    }
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        envelope.MessageId, sentAtUtc, confirmedAtUtc = DateTimeOffset.UtcNow,
        confirmed, error, latencyMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
    }, json));
    await Console.Out.FlushAsync();
}
