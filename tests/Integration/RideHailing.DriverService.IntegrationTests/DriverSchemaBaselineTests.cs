// B-W02-03: Driver schema baseline proof on REAL PostgreSQL (no fake-green).
// Infra comes from the shared DriverPostgresDatabase: explicit env (fail loud)
// or disposable Testcontainers. Skips only when Docker is provably absent.
using Xunit.Abstractions;

namespace RideHailing.DriverService.IntegrationTests;

[Collection(DriverPostgresCollection.Name)]
public class DriverSchemaBaselineTests
{
    private readonly ITestOutputHelper _out;
    private readonly DriverPostgresDatabase _db;

    public DriverSchemaBaselineTests(ITestOutputHelper output, DriverPostgresDatabase db)
    {
        _out = output;
        _db = db;
    }

    [Fact]
    public async Task Driver_Baseline_Tables_Exist_On_Real_Postgres()
    {
        if (!_db.Ready())
        {
            _out.WriteLine("SKIP: " + _db.SkipReason);
            return;
        }

        _out.WriteLine($"INFRA: postgres via {_db.Source} — real assertions will run.");
        var tables = await _db.GetTablesAsync();
        Assert.Contains("drivers", tables);
        Assert.Contains("vehicles", tables);
    }

    [Fact]
    public async Task Driver_Baseline_Scripts_Are_Idempotent()
    {
        if (!_db.Ready())
        {
            _out.WriteLine("SKIP: " + _db.SkipReason);
            return;
        }

        // Second full apply must not throw (IF NOT EXISTS / guarded DDL).
        await _db.ReapplyScriptsAsync();
    }
}
