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

    [Theory]
    [InlineData("")]
    [InlineData(" , ")]
    public void EmptyEndpointListIsRejected(string hosts)
    {
        Assert.Throws<ArgumentException>(() => RabbitConnectionFactory.Create(
            hosts, 5672, "ride_app", "local-test-password", "test-client"));
    }
}
