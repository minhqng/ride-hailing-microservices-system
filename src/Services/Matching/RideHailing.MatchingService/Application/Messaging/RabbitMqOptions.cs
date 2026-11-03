// B-W08 broker consumption options (05B §17). Queue names default to the
// frozen catalog (RideTopology: matching.ride-requested.q,
// matching.driver-projection.q); operator may override via configuration but
// never per request. Numeric policy is bounded operational tuning (Doc 02
// §20.3), validated at startup. This is interim B-owned glue shaped to match
// the frozen platform contracts; it converges onto C's consumer base when
// that lands (same exchanges/queues/envelope/ACK discipline).
namespace RideHailing.MatchingService.Application.Messaging;

public sealed class RabbitMqOptions
{
    public const string SectionName = "RabbitMq";

    public string Host { get; set; } = "localhost";
    public string? Hosts { get; set; }
    // No credential fallback: broker access must be explicit in every
    // environment. compose/.env.example provide the local values.
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public int Port { get; set; } = 5672;
    public string RideRequestedQueue { get; set; } = "matching.ride-requested.q";
    public string DriverProjectionQueue { get; set; } = "matching.driver-projection.q";
    public string CancelMatchingQueue { get; set; } = "matching.commands.q";
    public ushort Prefetch { get; set; } = 16;
    public int MaxRedeliveries { get; set; } = 3;
    public bool ConsumersEnabled { get; set; } = true;
    public bool PublishingEnabled { get; set; } = true;

    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Host))
        {
            return "RabbitMq:Host must not be empty.";
        }

        if ((ConsumersEnabled || PublishingEnabled) && string.IsNullOrWhiteSpace(Username))
        {
            return "RabbitMq:Username must be configured explicitly when the broker is enabled.";
        }

        if ((ConsumersEnabled || PublishingEnabled) && string.IsNullOrWhiteSpace(Password))
        {
            return "RabbitMq:Password must be configured explicitly when the broker is enabled.";
        }

        if (Hosts is not null && Hosts.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length == 0)
        {
            return "RabbitMq:Hosts must contain at least one host when supplied.";
        }

        if (string.IsNullOrWhiteSpace(RideRequestedQueue)
            || string.IsNullOrWhiteSpace(DriverProjectionQueue)
            || string.IsNullOrWhiteSpace(CancelMatchingQueue))
        {
            return "RabbitMq queue names must not be empty.";
        }

        if (Prefetch is < 1 or > 256)
        {
            return "RabbitMq:Prefetch must be within 1..256.";
        }

        if (MaxRedeliveries is < 1 or > 10)
        {
            return "RabbitMq:MaxRedeliveries must be within 1..10.";
        }

        if (Port is < 1 or > 65535)
        {
            return "RabbitMq:Port must be within 1..65535.";
        }

        return null;
    }
}
