// Shared disposable Redis for Matching integration tests: one container per run.
namespace RideHailing.MatchingService.IntegrationTests;

[CollectionDefinition(RedisCollection.Name)]
public sealed class RedisCollection : ICollectionFixture<RedisDatabase>
{
    public const string Name = "matching-redis";
}
