// B-W12-05/06: real Driver HTTP ingress needs driver_db (state, Outbox),
// Redis (projection non-corruption) and RabbitMQ (publication absence).
namespace RideHailing.MatchingService.IntegrationTests;

[CollectionDefinition(W12IngressCollection.Name)]
public sealed class W12IngressCollection
    : ICollectionFixture<DriverTestDatabase>,
        ICollectionFixture<RedisDatabase>,
        ICollectionFixture<RabbitMqDatabase>
{
    public const string Name = "w12-driver-ingress";
}
