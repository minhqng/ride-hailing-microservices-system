// B-W02-09: Matching must never target driver_db (05B §11.3).
namespace RideHailing.MatchingService.IntegrationTests;

public class MatchingDbOwnershipTests
{
    [Fact]
    public void Matching_Fixture_Targets_Owned_Database_Only()
    {
        var cs = MatchingPostgresFixture.ResolveConnectionString();
        Assert.Contains("matching_db", cs, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("driver_db", cs, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("trip_db", cs, StringComparison.OrdinalIgnoreCase);
    }
}
