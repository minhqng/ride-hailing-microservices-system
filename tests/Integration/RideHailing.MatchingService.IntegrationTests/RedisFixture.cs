// B-W02-05: Redis connectivity fixture for Matching-owned GEO projection (W4 groundwork).
// W2 only proves reachability + PING. No GEO logic here. Skips when Redis unavailable.
using System.Net.Sockets;
using StackExchange.Redis;

namespace RideHailing.MatchingService.IntegrationTests;

public static class RedisFixture
{
    public static string ResolveConnectionString()
    {
        return Environment.GetEnvironmentVariable("REDIS_TEST_CONNECTION")
            ?? Environment.GetEnvironmentVariable("Redis__ConnectionString")
            ?? "localhost:6379";
    }

    public static async Task<bool> TryPingAsync()
    {
        try
        {
            var mux = await ConnectionMultiplexer.ConnectAsync(new ConfigurationOptions
            {
                EndPoints = { ResolveConnectionString() },
                ConnectTimeout = 3000,
                SyncTimeout = 3000,
                AbortOnConnectFail = true,
            });
            try
            {
                return (await mux.GetDatabase().PingAsync()).TotalMilliseconds >= 0;
            }
            finally
            {
                await mux.DisposeAsync();
            }
        }
        catch (Exception ex) when (ex is RedisConnectionException or TimeoutException or IOException or SocketException)
        {
            return false;
        }
    }
}
