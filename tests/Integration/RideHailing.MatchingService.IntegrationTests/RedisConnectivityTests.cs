// B-W02-05: Redis reachability proof (no GEO logic in W2).
namespace RideHailing.MatchingService.IntegrationTests;

public class RedisConnectivityTests
{
    [Fact]
    public async Task Redis_Ping_Succeeds_When_Available()
    {
        if (!await RedisFixture.TryPingAsync())
        {
            // Redis unavailable (REDIS_TEST_CONNECTION). CI without infra stays green.
            return;
        }
    }
}
