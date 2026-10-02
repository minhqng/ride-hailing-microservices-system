using RabbitMQ.Client;
namespace RideHailing.Messaging.Topology;

// W2 versioned baseline. W4 adds queues/bindings without changing these exchanges.
public static class BrokerBootstrapV1
{
    public static async Task DeclareAsync(string host, string username, string password, CancellationToken cancellationToken)
    {
        var factory = new ConnectionFactory { HostName = host, UserName = username, Password = password,
            ClientProvidedName = "platform-bootstrap-v1" };
        await using var connection = await factory.CreateConnectionAsync(cancellationToken);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);
        foreach (var exchange in new[] { "ride.commands", "ride.events" })
            await channel.ExchangeDeclareAsync(exchange, ExchangeType.Topic, durable: true,
                autoDelete: false, cancellationToken: cancellationToken);
    }
}
