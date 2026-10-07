// B-W07-09: real driver_db for cross-service component proof.
// Each repository touches only its own database (Matching tests own
// matching_db via MatchingPostgresDatabase; this fixture owns driver_db).
// No cross-service SQL: the orchestrator under test talks to Driver
// exclusively through IDriverClient (stubbed transport, real Driver repo).
// Fail-loud fixture (same contract as the service fixtures on this base):
// explicit env is verified now, otherwise a disposable container starts;
// any failure throws during collection init, never skips.
using Npgsql;
using RideHailing.DriverService.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace RideHailing.MatchingService.IntegrationTests;

public sealed class DriverTestDatabase : IAsyncLifetime
{
    static DriverTestDatabase()
    {
        Environment.SetEnvironmentVariable("TESTCONTAINERS_RYUK_DISABLED", "true");
    }

    private PostgreSqlContainer? _container;

    public string ConnectionString { get; private set; } = "";

    public async Task InitializeAsync()
    {
        var env = Environment.GetEnvironmentVariable("DRIVER_TEST_POSTGRES");
        if (!string.IsNullOrWhiteSpace(env))
        {
            await using var probe = new NpgsqlConnection(env);
            await probe.OpenAsync();
            ConnectionString = env;
            await ApplyAsync(env);
            return;
        }

        _container = new PostgreSqlBuilder("postgres:18.6")
            .WithDatabase("driver_db")
            .WithUsername("driver_app")
            .WithPassword("driver_dev_pw")
            .WithAutoRemove(true)
            .Build();
        await _container.StartAsync();
        ConnectionString = FixContainerHost(_container.GetConnectionString());
        await ApplyAsync(ConnectionString);
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    private static async Task ApplyAsync(string connectionString)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(DriverSqlBaseline.Load(), conn);
        await cmd.ExecuteNonQueryAsync();
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
                $"Host={host}", "Host=host.docker.internal", StringComparison.OrdinalIgnoreCase);
        }

        return connectionString;
    }
}
