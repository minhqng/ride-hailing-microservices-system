// B-W08: real RabbitMQ for broker proofs (no fake-green).
// RABBITMQ_TEST_CONNECTION explicitly set => verified now, failure throws
// loud. Otherwise disposable Testcontainers RabbitMQ
// (rabbitmq:4.3.6-management, pinned like compose); any start failure throws
// loud (Ryuk disabled; cleanup explicit via AutoRemove + DisposeAsync).
// One container per test run (not per test); topology declared per test run.
using Testcontainers.RabbitMq;

namespace RideHailing.MatchingService.IntegrationTests;

public sealed class RabbitMqDatabase : IAsyncLifetime
{
    static RabbitMqDatabase()
    {
        Environment.SetEnvironmentVariable("TESTCONTAINERS_RYUK_DISABLED", "true");
    }

    private RabbitMqContainer? _container;

    public string ConnectionString { get; private set; } = "";

    public async Task InitializeAsync()
    {
        var env = Environment.GetEnvironmentVariable("RABBITMQ_TEST_CONNECTION");
        if (!string.IsNullOrWhiteSpace(env))
        {
            ConnectionString = env;
            return;
        }

        _container = new RabbitMqBuilder("rabbitmq:4.3.6-management")
            .WithImage("rabbitmq:4.3.6-management")
            .WithAutoRemove(true)
            .Build();
        await _container.StartAsync();
        ConnectionString = FixContainerHost(_container.GetConnectionString());
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    internal static string FixContainerHost(string connectionString)
    {
        if (!File.Exists("/.dockerenv"))
        {
            return connectionString;
        }

        foreach (var host in new[] { "localhost", "127.0.0.1", "0.0.0.0", "172.17.0.1" })
        {
            connectionString = connectionString.Replace(
                host, "host.docker.internal", StringComparison.OrdinalIgnoreCase);
        }

        return connectionString;
    }
}
