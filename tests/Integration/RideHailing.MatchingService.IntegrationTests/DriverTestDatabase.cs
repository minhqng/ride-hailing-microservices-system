// B-W07-09: real driver_db for cross-service component proof.
// Each repository touches only its own database (Matching tests own
// matching_db via MatchingPostgresDatabase; this fixture owns driver_db).
// No cross-service SQL: the orchestrator under test talks to Driver
// exclusively through IDriverClient (stubbed transport, real Driver repo).
// Fail-loud fixture (same contract as the service fixtures on this base):
// explicit env is verified now, otherwise a disposable container starts;
// any failure throws during collection init, never skips.
using Npgsql;
using RideHailing.DriverService.Infrastructure.Outbox;
using RideHailing.DriverService.Infrastructure.Persistence;
using RideHailing.Messaging.Outbox;
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

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public NpgsqlOutboxStore OutboxStore { get; private set; } = null!;

    /// <summary>
    /// Production-shaped Driver repository over the shared pool. W9 requires a
    /// data source + shared Outbox store; there is no connection-string-only
    /// construction path anymore.
    /// </summary>
    public DriverRepository CreateRepository() => new(DataSource, OutboxStore);

    public async Task InitializeAsync()
    {
        var env = Environment.GetEnvironmentVariable("DRIVER_TEST_POSTGRES");
        if (!string.IsNullOrWhiteSpace(env))
        {
            await using var probe = new NpgsqlConnection(env);
            await probe.OpenAsync();
            ConnectionString = env;
            await ApplyAsync(env);
            CreateDataSource();
            return;
        }

        _container = new PostgreSqlBuilder("postgres:18.6@sha256:3a4264520e121176619f54ff867d12774bc81e95e7b84db58d63bccdb6144b81")
            .WithDatabase("driver_db")
            .WithUsername("driver_app")
            .WithPassword("driver_dev_pw")
            .WithAutoRemove(true)
            .Build();
        await _container.StartAsync();
        ConnectionString = FixContainerHost(_container.GetConnectionString());
        await ApplyAsync(ConnectionString);
        CreateDataSource();
    }

    private void CreateDataSource()
    {
        DataSource = NpgsqlDataSource.Create(ConnectionString);
        OutboxStore = new NpgsqlOutboxStore(DataSource);
    }

    public async Task DisposeAsync()
    {
        if (DataSource is not null)
        {
            await DataSource.DisposeAsync();
        }

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

        // W9: apply C's canonical shared Outbox schema to driver_db.
        await DriverOutboxSchema.EnsureAsync(connectionString);
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
