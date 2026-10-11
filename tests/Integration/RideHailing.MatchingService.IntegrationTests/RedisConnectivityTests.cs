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
    public async Task Startup_Outage_Returns_Reconnecting_Multiplexer_Instead_Of_Removing_Consumer()
    {
        using var mux = await RideHailing.MatchingService.Application.Projection.RedisProjectionUpdater
            .ConnectAsync("127.0.0.1:1,connectRetry=0").WaitAsync(TimeSpan.FromSeconds(15));
        Assert.False(mux.IsConnected);
        var updater = new RideHailing.MatchingService.Application.Projection.RedisProjectionUpdater(mux);
        var result = await updater.ApplyStateAsync(new(Guid.NewGuid(), "AVAILABLE", 1));
        Assert.True(result.Degraded);
    }

    [Fact]
    public async Task Production_Connector_Accepts_Redis_Configuration_Options()
    {
        using var mux = await RideHailing.MatchingService.Application.Projection.RedisProjectionUpdater
            .ConnectAsync(_db.ConnectionString + ",connectRetry=0");
        await mux.GetDatabase().PingAsync();
        Assert.True(mux.IsConnected);
    }

    [Fact]
    public async Task Redis_Ping_Succeeds_On_Real_Redis()
    {
        var pong = await _db.Multiplexer().GetDatabase().PingAsync();
        Assert.True(pong.TotalMilliseconds >= 0);
    }
}
