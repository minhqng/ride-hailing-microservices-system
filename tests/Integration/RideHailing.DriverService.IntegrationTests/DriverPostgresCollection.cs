// Shared disposable PostgreSQL for all Driver integration tests in this assembly:
// one container per test run (not per test). Tests must be order-independent
// (GUID-isolated rows + per-test cleanup where needed).
namespace RideHailing.DriverService.IntegrationTests;

[CollectionDefinition(DriverPostgresCollection.Name)]
public sealed class DriverPostgresCollection : ICollectionFixture<DriverPostgresDatabase>
{
    public const string Name = "driver-postgres";
}
