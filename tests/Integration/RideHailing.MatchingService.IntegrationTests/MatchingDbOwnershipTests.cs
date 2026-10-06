// B-W02-09 (config-shape half): asserts Matching tests are POINTED at matching_db
// and never at driver_db (05B §11.3). PostgreSQL-level denial is proven by
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
