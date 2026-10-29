// Shared disposable infrastructure for production-path Matching proofs:
// matching_db + driver_db + Redis + RabbitMQ. One set per test run.
namespace RideHailing.MatchingService.IntegrationTests;

[CollectionDefinition(ProductionWorkflowCollection.Name)]
public sealed class ProductionWorkflowCollection
    : ICollectionFixture<MatchingPostgresDatabase>,
        ICollectionFixture<DriverTestDatabase>,
        ICollectionFixture<RedisDatabase>,
        ICollectionFixture<RabbitMqDatabase>
{
    public const string Name = "production-workflow";
}
