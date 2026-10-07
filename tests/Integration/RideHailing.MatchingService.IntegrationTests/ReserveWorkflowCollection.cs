// B-W07-05..10: shared disposable databases for reserve-workflow component
// proof: matching_db (attempt/round ledger) + driver_db (reservation
// authority). One container pair per test run.
namespace RideHailing.MatchingService.IntegrationTests;

[CollectionDefinition(ReserveWorkflowCollection.Name)]
public sealed class ReserveWorkflowCollection
    : ICollectionFixture<MatchingPostgresDatabase>,
        ICollectionFixture<DriverTestDatabase>
{
    public const string Name = "reserve-workflow";
}
