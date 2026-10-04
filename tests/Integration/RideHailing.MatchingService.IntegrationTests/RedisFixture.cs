// B-W02-05 (P1 review fix): Redis fixture with no fake-green (Testcontainers.Redis).
// Same semantics as the PG fixtures: explicit REDIS_TEST_CONNECTION (or
// Redis__ConnectionString) => infra EXPECTED => failure throws loud;
// otherwise disposable Testcontainers Redis (redis:8.10.2, pinned like compose).
// Tests FAIL LOUD when Redis is unavailable (Assert.Fail with SkipReason):
// an authoritative projection proof must never pass without real Redis.
// Shared per collection: one container per test run (not per test).
using StackExchange.Redis;
using Testcontainers.Redis;

namespace RideHailing.MatchingService.IntegrationTests;

public sealed class RedisDatabase : IAsyncLifetime
{
    static RedisDatabase()
    {
        // Ryuk cannot start inside the SDK container; cleanup is explicit.
        Environment.SetEnvironmentVariable("TESTCONTAINERS_RYUK_DISABLED", "true");
    }

    private RedisContainer? _container;
    private ConnectionMultiplexer? _mux;

    public string ConnectionString { get; private set; } = "";
    public string Source { get; private set; } = "";
    public bool Skipped { get; private set; }
    public string SkipReason { get; private set; } = "";

    public bool Ready() => !Skipped && _mux is not null;

    public async Task InitializeAsync()
    {
        var env = Environment.GetEnvironmentVariable("REDIS_TEST_CONNECTION")
            ?? Environment.GetEnvironmentVariable("Redis__ConnectionString");

        if (!string.IsNullOrWhiteSpace(env))
        {
            _mux = await ConnectAsync(env);
            try
            {
                await _mux.GetDatabase().PingAsync();
            }
            catch (Exception ex)
            {
                await _mux.DisposeAsync();
                _mux = null;
                throw new InvalidOperationException(
                    $"REDIS_TEST_CONNECTION is set but Redis is unreachable at '{env}'.", ex);
            }

            ConnectionString = env;
            Source = "env";
            return;
        }

        if (!DockerMightExist())
        {
            Skipped = true;
            SkipReason = "No DOCKER_HOST and no /var/run/docker.sock: cannot start disposable Redis.";
            return;
        }

        _container = new RedisBuilder("redis:8.10.2")
            .WithAutoRemove(true)
            .Build();
        await _container.StartAsync();
        ConnectionString = FixContainerHost(_container.GetConnectionString());
        Source = "testcontainers";
        _mux = await ConnectAsync(ConnectionString);
    }

    public async Task DisposeAsync()
    {
        if (_mux is not null)
        {
            await _mux.DisposeAsync();
        }

        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    public ConnectionMultiplexer Multiplexer() =>
        _mux ?? throw new InvalidOperationException("RedisDatabase not ready.");

    // Configured endpoint for ownership/shape assertions (no connection).
    public static string ConfiguredEndpoint() =>
        Environment.GetEnvironmentVariable("REDIS_TEST_CONNECTION")
        ?? Environment.GetEnvironmentVariable("Redis__ConnectionString")
        ?? "localhost:6379";

    private static Task<ConnectionMultiplexer> ConnectAsync(string configuration) =>
        ConnectionMultiplexer.ConnectAsync(new ConfigurationOptions
        {
            EndPoints = { configuration },
            ConnectTimeout = 5000,
            SyncTimeout = 5000,
            AbortOnConnectFail = true,
        });

    private static bool DockerMightExist() =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOCKER_HOST"))
        || File.Exists("/var/run/docker.sock");

    // Testcontainers.Redis returns "localhost:port" (or gateway IP); unreachable
    // from inside a sibling container — same rewrite as the PG fixtures.
    internal static string FixContainerHost(string connectionString)
    {
        if (!File.Exists("/.dockerenv"))
        {
            return connectionString;
        }

        foreach (var host in new[] { "localhost", "127.0.0.1", "0.0.0.0", "172.17.0.1" })
        {
            connectionString = connectionString.Replace(
                host + ":", "host.docker.internal:", StringComparison.OrdinalIgnoreCase);
        }

        return connectionString;
    }
}
