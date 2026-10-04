// B-W02-05: Redis connectivity fixture for Matching-owned GEO projection (W4 groundwork).
// W2 only proves reachability + PING. No GEO logic here.
// Resolution order: explicit env connection string, else disposable
// Testcontainers Redis (fail loud on start failure). No silent green: when
// neither is available the caller must fail, never return a fake pass.
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

    public static Task<bool> TryPingAsync() => TryPingAsync(ResolveConnectionString());

    public static async Task<bool> TryPingAsync(string connectionString)
    {
        try
        {
            var mux = await ConnectionMultiplexer.ConnectAsync(new ConfigurationOptions
            {
                EndPoints = { FixContainerHost(connectionString) },
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

    // Same sibling-container host rewrite as the PostgreSQL fixtures: a
    // Testcontainers-mapped localhost endpoint is unreachable from inside a
    // sibling SDK container, so rewrite to host.docker.internal only there.
    internal static string FixContainerHost(string connectionString)
    {
        if (!File.Exists("/.dockerenv"))
        {
            return connectionString;
        }

        foreach (var host in new[] { "localhost", "127.0.0.1", "0.0.0.0" })
        {
            if (connectionString.StartsWith(host, StringComparison.OrdinalIgnoreCase))
            {
                return "host.docker.internal" + connectionString.Substring(host.Length);
            }
        }

        return connectionString;
    }
}
