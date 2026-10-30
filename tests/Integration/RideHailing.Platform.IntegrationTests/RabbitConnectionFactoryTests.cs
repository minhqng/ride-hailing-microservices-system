using RabbitMQ.Client;
using RideHailing.Messaging.Topology;

namespace RideHailing.Platform.IntegrationTests;

public sealed class RabbitConnectionFactoryTests
{
    [Fact]
    public void ResolverUsesEveryDistinctConfiguredClusterEndpoint()
    {
        var factory = RabbitConnectionFactory.Create(
            "rabbitmq,rabbitmq-2,rabbitmq-3,rabbitmq-2",
            5672, "ride_app", "local-test-password", "test-client");

        var resolver = factory.EndpointResolverFactory(
            [new AmqpTcpEndpoint("ignored", 5672)]);
        var endpoints = resolver.All().Select(endpoint => endpoint.HostName).Order().ToArray();

        Assert.Equal(new[] { "rabbitmq", "rabbitmq-2", "rabbitmq-3" }, endpoints);
        Assert.False(factory.AutomaticRecoveryEnabled);
        Assert.False(factory.TopologyRecoveryEnabled);
    }

    [Fact]
    public void ResolverUsesPerEndpointPortsAndDeduplicatesParsedEndpoints()
    {
        var factory = RabbitConnectionFactory.Create(
            "rabbitmq,rabbitmq-2:5673,rabbitmq-2:5673,rabbitmq-3:5674",
            5672, "ride_app", "local-test-password", "test-client");

        var resolver = factory.EndpointResolverFactory(
            [new AmqpTcpEndpoint("ignored", 5672)]);
        var endpoints = resolver.All()
            .Select(endpoint => (endpoint.HostName, endpoint.Port))
            .OrderBy(endpoint => endpoint.HostName)
            .ToArray();

        Assert.Equal(
            new[] { ("rabbitmq", 5672), ("rabbitmq-2", 5673), ("rabbitmq-3", 5674) },
            endpoints);
        Assert.Equal("rabbitmq", factory.HostName);
        Assert.Equal(5672, factory.Port);
    }

    [Fact]
    public void SameHostCanBeListedWithDifferentPorts()
    {
        var factory = RabbitConnectionFactory.Create(
            "localhost:5672,localhost:5673", 5672, "ride_app", "local-test-password", "test-client");

        var resolver = factory.EndpointResolverFactory(
            [new AmqpTcpEndpoint("ignored", 5672)]);
        var endpoints = resolver.All()
            .Select(endpoint => (endpoint.HostName, endpoint.Port))
            .OrderBy(endpoint => endpoint.Port)
            .ToArray();

        Assert.Equal(new[] { ("localhost", 5672), ("localhost", 5673) }, endpoints);
    }

    [Fact]
    public void BracketedIpv6EndpointsUseTheirExplicitOrDefaultPorts()
    {
        var factory = RabbitConnectionFactory.Create(
            "[::1]:5673,[2001:db8::1]", 5672, "ride_app", "local-test-password", "test-client");

        var resolver = factory.EndpointResolverFactory(
            [new AmqpTcpEndpoint("ignored", 5672)]);
        var endpoints = resolver.All()
            .Select(endpoint => (endpoint.HostName, endpoint.Port))
            .OrderBy(endpoint => endpoint.HostName)
            .ToArray();

        Assert.Equal(new[] { ("::1", 5673), ("2001:db8::1", 5672) }, endpoints);
    }

    [Theory]
    [InlineData("rabbitmq:0")]
    [InlineData("rabbitmq:65536")]
    [InlineData("rabbitmq:not-a-port")]
    [InlineData("rabbitmq:")]
    [InlineData(":5672")]
    [InlineData("[::1")]
    [InlineData("[::1]x")]
    [InlineData("[]:5672")]
    public void InvalidEndpointPortIsRejected(string hosts)
    {
        Assert.Throws<ArgumentException>(() => RabbitConnectionFactory.Create(
            hosts, 5672, "ride_app", "local-test-password", "test-client"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" , ")]
    public void EmptyEndpointListIsRejected(string hosts)
    {
        Assert.Throws<ArgumentException>(() => RabbitConnectionFactory.Create(
            hosts, 5672, "ride_app", "local-test-password", "test-client"));
    }
}
