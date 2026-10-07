// B-W02-09 (config-shape half): asserts Driver tests are POINTED at driver_db
// and never at another service DB. This proves connection-string scoping only;
// PostgreSQL-level denial is proven by Platform DbIsolationTests, not here.
namespace RideHailing.DriverService.IntegrationTests;

public class DriverDbOwnershipTests
{
    [Fact]
    public void Driver_Fixture_Targets_Owned_Database_Only()
    {
        var cs = DriverPostgresDatabase.ConfiguredConnectionString();
        Assert.Contains("driver_db", cs, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("matching_db", cs, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("trip_db", cs, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("payment_db", cs, StringComparison.OrdinalIgnoreCase);
    }
}
