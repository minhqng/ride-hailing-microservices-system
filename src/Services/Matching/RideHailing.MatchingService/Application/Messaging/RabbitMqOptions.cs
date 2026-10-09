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
    public string Username { get; set; } = "ride_app";
    public string Password { get; set; } = "ride_dev_pw";
    public int Port { get; set; } = 5672;
    public string RideRequestedQueue { get; set; } = "matching.ride-requested.q";
    public string DriverProjectionQueue { get; set; } = "matching.driver-projection.q";
    public ushort Prefetch { get; set; } = 16;
    // Total handler calls including the initial delivery; platform permits at most three retries.
    public int MaxRedeliveries { get; set; } = 3;
    public bool ConsumersEnabled { get; set; } = true;

    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Host))
        {
            return "RabbitMq:Host must not be empty.";
        }

        if (string.IsNullOrWhiteSpace(RideRequestedQueue) || string.IsNullOrWhiteSpace(DriverProjectionQueue))
        {
            return "RabbitMq queue names must not be empty.";
        }

        if (Prefetch is < 1 or > 256)
        {
            return "RabbitMq:Prefetch must be within 1..256.";
        }

        if (MaxRedeliveries is < 1 or > 4)
        {
            return "RabbitMq:MaxRedeliveries must be within 1..4.";
        }

        if (Port is < 1 or > 65535)
        {
            return "RabbitMq:Port must be within 1..65535.";
        }

        return null;
    }
}
