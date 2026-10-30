using RabbitMQ.Client;

namespace RideHailing.Messaging.Topology;

/// <summary>
/// Creates a RabbitMQ factory whose resolver tries every configured cluster
/// endpoint in shuffled order. This provides reconnect choices; it does not
/// buffer publications and therefore never replaces the Outbox.
/// </summary>
public static class RabbitConnectionFactory
{
    public static ConnectionFactory Create(
        string hosts,
        int port,
        string username,
        string password,
        string clientName,
        string virtualHost = "/",
        bool automaticRecovery = false)
    {
        var hostnames = hosts.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (hostnames.Length == 0) throw new ArgumentException("At least one RabbitMQ host is required.", nameof(hosts));
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        if (string.IsNullOrWhiteSpace(username)) throw new ArgumentException("RabbitMQ username is required.", nameof(username));
        if (string.IsNullOrWhiteSpace(password)) throw new ArgumentException("RabbitMQ password is required.", nameof(password));
        if (string.IsNullOrWhiteSpace(clientName)) throw new ArgumentException("RabbitMQ client name is required.", nameof(clientName));

        var endpoints = hostnames.Select(host => new AmqpTcpEndpoint(host, port)).ToArray();
        return new ConnectionFactory
        {
            HostName = hostnames[0],
            Port = port,
            UserName = username,
            Password = password,
            VirtualHost = virtualHost,
            ClientProvidedName = clientName,
            AutomaticRecoveryEnabled = automaticRecovery,
            TopologyRecoveryEnabled = automaticRecovery,
            RequestedConnectionTimeout = TimeSpan.FromSeconds(10),
            EndpointResolverFactory = _ => new DefaultEndpointResolver(endpoints),
        };
    }
}
