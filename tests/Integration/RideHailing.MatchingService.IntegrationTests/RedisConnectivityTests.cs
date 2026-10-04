// B-W02-05: Redis reachability proof on REAL Redis (no fake-green, no GEO logic in W2).
//
// Resolution order: an explicitly configured Redis endpoint (REDIS_TEST_CONNECTION
// or Redis__ConnectionString) is EXPECTED and a failed PING fails loud; otherwise
// a disposable Testcontainers Redis (redis:8.10.2, same as compose) is started and
// PINGed. Failure with neither available throws: absence of Redis must never
// produce a green proof.
using Testcontainers.Redis;

namespace RideHailing.MatchingService.IntegrationTests;

public sealed class RedisConnectivityTests : IAsyncLifetime
{
    static RedisConnectivityTests()
    {
        // Same determinism rationale as the PostgreSQL fixtures: explicit
        // dispose, no Ryuk janitor inside the SDK container.
        Environment.SetEnvironmentVariable("TESTCONTAINERS_RYUK_DISABLED", "true");
    }

    private RedisContainer? _container;

    public async Task InitializeAsync()
    {
        if (await RedisFixture.TryPingAsync())
        {
            return;
        }

        if (!DockerPresent())
        {
            throw new InvalidOperationException(
                "Real Redis proof required but unavailable: no configured endpoint answered PING " +
                "and no Docker daemon exists for disposable Redis.");
        }

        _container = new RedisBuilder("redis:8.10.2")
            .WithAutoRemove(true)
            .Build();
        await _container.StartAsync();
    }

    [Fact]
    public async Task Redis_Ping_Succeeds_On_Real_Redis()
    {
        var connectionString = _container?.GetConnectionString() ?? RedisFixture.ResolveConnectionString();
        Assert.True(
            await RedisFixture.TryPingAsync(connectionString),
            $"PING failed against real Redis at {connectionString}.");
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    private static bool DockerPresent() =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOCKER_HOST"))
        || File.Exists("/var/run/docker.sock");
}
