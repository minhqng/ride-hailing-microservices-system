// B-W02-03: Driver schema baseline proof on REAL PostgreSQL.
// Infra is guaranteed by the shared collection fixture: any setup failure
// fails every test loudly. No skip path, no vacuous pass.
namespace RideHailing.DriverService.IntegrationTests;

[Collection(DriverPostgresCollection.Name)]
public class DriverSchemaBaselineTests
{
    private readonly DriverPostgresDatabase _db;

    public DriverSchemaBaselineTests(DriverPostgresDatabase db)
    {
        _db = db;
    }

    [Fact]
    public async Task Driver_Baseline_Tables_Exist_On_Real_Postgres()
    {
        var tables = await _db.GetTablesAsync();
        Assert.Contains("drivers", tables);
        Assert.Contains("vehicles", tables);
    }

    [Fact]
    public async Task Driver_Baseline_Scripts_Are_Idempotent()
    {
        // Second full apply must not throw (IF NOT EXISTS / guarded DDL).
        await _db.ReapplyScriptsAsync();
    }
}
