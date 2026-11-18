// Real Driver HTTP ingress needs driver_db (state, Outbox),
// Redis (projection non-corruption) and RabbitMQ (publication absence).
namespace RideHailing.MatchingService.IntegrationTests;

[CollectionDefinition(DriverIngressCollection.Name)]
public sealed class DriverIngressCollection
    : ICollectionFixture<DriverTestDatabase>,
        ICollectionFixture<RedisDatabase>,
        ICollectionFixture<RabbitMqDatabase>
{
    public const string Name = "driver-ingress";
}
