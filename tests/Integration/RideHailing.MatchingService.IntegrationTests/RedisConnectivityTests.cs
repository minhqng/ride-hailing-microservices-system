// B-W02-05: Redis reachability proof on REAL Redis (no fake-green).
namespace RideHailing.MatchingService.IntegrationTests;

[Collection(RedisCollection.Name)]
public class RedisConnectivityTests
{
    private readonly RedisDatabase _db;

    public RedisConnectivityTests(RedisDatabase db)
    {
        _db = db;
    }

    [Fact]
    public async Task Redis_Ping_Succeeds_On_Real_Redis()
    {
        var pong = await _db.Multiplexer().GetDatabase().PingAsync();
        Assert.True(pong.TotalMilliseconds >= 0);
    }
}
