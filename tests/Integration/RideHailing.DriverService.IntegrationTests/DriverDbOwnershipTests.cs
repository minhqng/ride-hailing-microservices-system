// B-W02-09: DB ownership guard — Driver tests must only target driver_db.
namespace RideHailing.DriverService.IntegrationTests;

public class DriverDbOwnershipTests
{
    [Fact]
    public void Driver_Fixture_Targets_Owned_Database_Only()
    {
        var cs = DriverPostgresFixture.ResolveConnectionString();
        Assert.Contains("driver_db", cs, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("matching_db", cs, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("trip_db", cs, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("payment_db", cs, StringComparison.OrdinalIgnoreCase);
    }
}
