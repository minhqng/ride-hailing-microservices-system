// B-W08 broker options for the Driver service (05B §17). Queue/exchange
// names come from the frozen catalog (RideTopology); only connection,
// budgets and enablement are operator configuration, validated at startup.
namespace RideHailing.DriverService.Application.Messaging;

public sealed class DriverBrokerOptions
{
    public const string SectionName = "RabbitMq";

    public string Host { get; set; } = "localhost";
    public string Username { get; set; } = "ride_app";
    public string Password { get; set; } = "ride_dev_pw";
    public int Port { get; set; } = 5672;
    public int PublishTimeoutSeconds { get; set; } = 5;
    public bool PublishingEnabled { get; set; } = true;

    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Host))
        {
            return "RabbitMq:Host must not be empty.";
        }

        if (PublishTimeoutSeconds is < 1 or > 60)
        {
            return "RabbitMq:PublishTimeoutSeconds must be within 1..60.";
        }

        if (Port is < 1 or > 65535)
        {
            return "RabbitMq:Port must be within 1..65535.";
        }

        return null;
    }
}
