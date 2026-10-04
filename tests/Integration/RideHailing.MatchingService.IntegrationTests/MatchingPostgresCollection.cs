// Shared disposable PostgreSQL for Matching integration tests in this assembly:
// one container per test run (not per test).
namespace RideHailing.MatchingService.IntegrationTests;

[CollectionDefinition(MatchingPostgresCollection.Name)]
public sealed class MatchingPostgresCollection : ICollectionFixture<MatchingPostgresDatabase>
{
    public const string Name = "matching-postgres";
}
