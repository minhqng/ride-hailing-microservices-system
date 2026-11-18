// Asserts Matching tests are POINTED at matching_db
// and never at driver_db. PostgreSQL-level denial is proven by
// Platform DbIsolationTests, not here.
namespace RideHailing.MatchingService.IntegrationTests;

public class MatchingDbOwnershipTests
{
    [Fact]
    public void Matching_Fixture_Targets_Owned_Database_Only()
    {
        var cs = MatchingPostgresDatabase.ConfiguredConnectionString();
        Assert.Contains("matching_db", cs, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("driver_db", cs, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("trip_db", cs, StringComparison.OrdinalIgnoreCase);
    }
}
