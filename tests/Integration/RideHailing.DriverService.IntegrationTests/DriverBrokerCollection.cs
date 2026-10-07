// B-W08: broker component proof collection (real PG + real RabbitMQ).
namespace RideHailing.DriverService.IntegrationTests;

[CollectionDefinition(DriverBrokerCollection.Name)]
public sealed class DriverBrokerCollection
    : ICollectionFixture<DriverPostgresDatabase>,
        ICollectionFixture<RabbitMqDatabase>
{
    public const string Name = "driver-broker";
}
