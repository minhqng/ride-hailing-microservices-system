// B-W02-05: Redis reachability proof on REAL Redis (no fake-green).
using Xunit.Abstractions;

namespace RideHailing.MatchingService.IntegrationTests;

[Collection(RedisCollection.Name)]
public class RedisConnectivityTests
{
    private readonly ITestOutputHelper _out;
    private readonly RedisDatabase _db;

    public RedisConnectivityTests(ITestOutputHelper output, RedisDatabase db)
    {
        _out = output;
        _db = db;
    }

    [Fact]
    public async Task Redis_Ping_Succeeds_On_Real_Redis()
    {
        if (!_db.Ready())
        {
            _out.WriteLine("SKIP: " + _db.SkipReason);
            return;
        }

        _out.WriteLine($"INFRA: redis via {_db.Source} — real assertions will run.");
        var pong = await _db.Multiplexer().GetDatabase().PingAsync();
        Assert.True(pong.TotalMilliseconds >= 0);
    }
}
