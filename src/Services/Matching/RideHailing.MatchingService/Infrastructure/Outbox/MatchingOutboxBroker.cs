// Broker adapter for the shared Outbox relay. The relay publisher and
// lifecycle host share one dedicated factory: automatic client recovery is
// disabled because the host owns restart/backoff and the shared ConfirmedPublisher
// reopens a lost connection on the next attempt.
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RideHailing.MatchingService.Application.Messaging;

namespace RideHailing.MatchingService.Infrastructure.Outbox;

public sealed class MatchingOutboxBroker
{
    public MatchingOutboxBroker(IOptions<RabbitMqOptions> options)
    {
        var value = options.Value;
        Factory = new ConnectionFactory
        {
            HostName = value.Host,
            Port = value.Port,
            UserName = value.Username,
            Password = value.Password,
            ClientProvidedName = "matching-service/outbox-relay",
            AutomaticRecoveryEnabled = false,
        };
    }

    public ConnectionFactory Factory { get; }
}
