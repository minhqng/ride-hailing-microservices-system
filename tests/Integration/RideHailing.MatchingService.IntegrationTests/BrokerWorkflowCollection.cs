// B-W08: shared disposable infrastructure for broker component proof:
// matching_db (round ledger) + Redis (derived projection) + RabbitMQ
// (transport). One set per test run.
namespace RideHailing.MatchingService.IntegrationTests;

[CollectionDefinition(BrokerWorkflowCollection.Name)]
public sealed class BrokerWorkflowCollection
    : ICollectionFixture<MatchingPostgresDatabase>,
        ICollectionFixture<RedisDatabase>,
        ICollectionFixture<RabbitMqDatabase>
{
    public const string Name = "broker-workflow";
}
