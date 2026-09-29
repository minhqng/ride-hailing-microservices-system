// B-W02-04: Matching schema baseline proof. Real PG only, skip when unavailable.
namespace RideHailing.MatchingService.IntegrationTests;

public class MatchingSchemaBaselineTests
{
    [Fact]
    public async Task Matching_Baseline_Tables_Exist_On_Real_Postgres()
    {
        var applied = await MatchingPostgresFixture.TryApplyBaselineAsync();
        if (!applied)
        {
            // PostgreSQL unavailable (MATCHING_TEST_POSTGRES). CI without infra stays green.
            return;
        }

        var tables = await MatchingPostgresFixture.GetTablesAsync();
        Assert.Contains("matching_rounds", tables);
        Assert.Contains("matching_attempts", tables);
    }
}
