using System.Globalization;
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

        var endpoints = hostnames
            .Select(host => ParseEndpoint(host, port))
            .GroupBy(endpoint => (endpoint.HostName.ToLowerInvariant(), endpoint.Port))
            .Select(group => group.First())
            .ToArray();
        return new ConnectionFactory
        {
            HostName = endpoints[0].HostName,
            Port = endpoints[0].Port,
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

    /// <summary>
    /// Accepts a bare host name or <c>host:port</c> (including
    /// <c>[IPv6]:port</c>) so locally mapped HA endpoints can be listed
    /// explicitly instead of all sharing the global default port.
    /// </summary>
    private static AmqpTcpEndpoint ParseEndpoint(string value, int defaultPort)
    {
        if (value.StartsWith("[", StringComparison.Ordinal))
        {
            var closingBracket = value.IndexOf(']');
            if (closingBracket < 0)
                throw new ArgumentException($"RabbitMQ endpoint '{value}' is missing the closing ']'.", nameof(value));
            var host = value[1..closingBracket];
            if (string.IsNullOrWhiteSpace(host))
                throw new ArgumentException($"RabbitMQ endpoint '{value}' has an empty host.", nameof(value));
            if (closingBracket == value.Length - 1)
                return new AmqpTcpEndpoint(host, defaultPort);
            if (value[closingBracket + 1] != ':')
                throw new ArgumentException($"RabbitMQ endpoint '{value}' is malformed.", nameof(value));
            return new AmqpTcpEndpoint(host, ParsePort(value[(closingBracket + 2)..], value));
        }

        var firstColon = value.IndexOf(':');
        if (firstColon < 0) return new AmqpTcpEndpoint(value, defaultPort);
        // More than one colon is an unbracketed IPv6 literal; keep the default
        // port rather than misreading its final group as a port.
        if (firstColon != value.LastIndexOf(':'))
            return new AmqpTcpEndpoint(value, defaultPort);

        var hostname = value[..firstColon];
        if (string.IsNullOrWhiteSpace(hostname))
            throw new ArgumentException($"RabbitMQ endpoint '{value}' has an empty host.", nameof(value));
        return new AmqpTcpEndpoint(hostname, ParsePort(value[(firstColon + 1)..], value));
    }

    private static int ParsePort(string text, string endpoint)
    {
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            || parsed is < 1 or > 65535)
        {
            throw new ArgumentException($"RabbitMQ endpoint '{endpoint}' has an invalid port.", nameof(endpoint));
        }

        return parsed;
    }
}
