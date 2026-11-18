// Driver PostgreSQL fixture: REAL infrastructure, never a vacuous pass.
// Resolution order:
//   1. DRIVER_TEST_POSTGRES explicitly set => verify now; any failure throws loud.
//   2. Otherwise disposable Testcontainers PostgreSQL (postgres:18.6@sha256:3a4264520e121176619f54ff867d12774bc81e95e7b84db58d63bccdb6144b81, same seed
//      as compose); any start/apply failure throws loud ( Ryuk disabled; cleanup
//      is explicit via DisposeAsync; the owned container also supports restart proofs).
// Applies ALL migration scripts in filename order (001, 002, ...).
using Npgsql;
using RideHailing.DriverService.Infrastructure.Outbox;
using RideHailing.DriverService.Infrastructure.Persistence;
using RideHailing.Messaging.Outbox;
using Testcontainers.PostgreSql;

namespace RideHailing.DriverService.IntegrationTests;

public sealed class DriverPostgresDatabase : IAsyncLifetime
{
    static DriverPostgresDatabase()
    {
        // Ryuk (Testcontainers janitor) cannot start reliably from inside the
        // SDK container on Docker Desktop. Cleanup is explicit (AutoRemove +
        // DisposeAsync), so disabling it is safe and keeps runs deterministic.
        // An explicit TESTCONTAINERS_RYUK_DISABLED value always wins.
        Environment.SetEnvironmentVariable(
            "TESTCONTAINERS_RYUK_DISABLED",
            Environment.GetEnvironmentVariable("TESTCONTAINERS_RYUK_DISABLED") ?? "true");
    }

    private PostgreSqlContainer? _container;

    public string ConnectionString { get; private set; } = "";

    /// <summary>
    /// Shared pool for repository-backed tests. Capping the pool keeps the
    /// 100-contender serialization proof client-side, exactly like the old
    /// per-call connection strings did.
    /// </summary>
    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public NpgsqlOutboxStore OutboxStore { get; private set; } = null!;

    public DriverRepository CreateRepository() => new(DataSource, OutboxStore);

    public async Task InitializeAsync()
    {
        var env = Environment.GetEnvironmentVariable("DRIVER_TEST_POSTGRES");
        if (!string.IsNullOrWhiteSpace(env))
        {
            EnsureDisposable(env, "DRIVER_TEST_POSTGRES");
            await using var probe = new NpgsqlConnection(env);
            await probe.OpenAsync();
            ConnectionString = env;
            await ApplyScriptsAsync(env);
            CreateDataSource();
            return;
        }

        _container = new PostgreSqlBuilder("postgres:18.6@sha256:3a4264520e121176619f54ff867d12774bc81e95e7b84db58d63bccdb6144b81")
            .WithDatabase("driver_db")
            .WithUsername("driver_app")
            .WithPassword("driver_dev_pw")
            .WithAutoRemove(false)
            .Build();
        await _container.StartAsync();
        ConnectionString = FixContainerHost(_container.GetConnectionString());
        await ApplyScriptsAsync(ConnectionString);
        CreateDataSource();
    }

    private void CreateDataSource()
    {
        // Respect an operator-provided pool cap; otherwise stay below the
        // container's max_connections for the 100-contender lock proof.
        var cs = ConnectionString;
        if (!cs.Contains("Maximum Pool Size", StringComparison.OrdinalIgnoreCase))
        {
            cs += ";Maximum Pool Size=40";
        }

        var builder = new NpgsqlDataSourceBuilder(cs);
        DataSource = builder.Build();
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

    public Task ReapplyScriptsAsync() => ApplyScriptsAsync(ConnectionString);

    public async Task RestartOwnedAsync()
    {
        if (_container is null) throw new InvalidOperationException("Restart proof requires a disposable fixture-owned database.");
        await DataSource.DisposeAsync();
        await _container.StopAsync();
        await _container.StartAsync();
        ConnectionString = FixContainerHost(_container.GetConnectionString());
        CreateDataSource();
    }

    /// <summary>
    /// Loads one migration script by filename for tests that must apply
    /// migrations out of order (e.g. dirty-dataset pre-flight proofs).
    /// </summary>
    public static string LoadMigrationScript(string fileName) =>
        File.ReadAllText(Path.Combine(FindMigrationsDir(), fileName));

    // Configured (not yet connected) connection string for ownership assertions.
    public static string ConfiguredConnectionString() =>
        Environment.GetEnvironmentVariable("DRIVER_TEST_POSTGRES")
        ?? "Host=localhost;Port=5432;Database=driver_db;Username=driver_app;Password=driver_dev_pw";

    // An external database is only accepted when it is provably disposable:
    // the fixture applies migrations and tests truncate tables, so pointing it
    // at a shared database must be an explicit, acknowledged decision.
    private static void EnsureDisposable(string connectionString, string variable)
    {
        var database = new NpgsqlConnectionStringBuilder(connectionString).Database ?? "";
        var acknowledged = Environment.GetEnvironmentVariable(variable + "_ALLOW_DESTRUCTIVE");
        if (string.Equals(acknowledged, "1", StringComparison.Ordinal)
            || database.EndsWith("_test", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw new InvalidOperationException(
            $"{variable} targets database '{database}', which is not provably disposable. " +
            $"Use a database whose name ends with '_test', or set {variable}_ALLOW_DESTRUCTIVE=1 " +
            "to confirm the fixture may migrate and truncate it.");
    }

    public async Task<HashSet<string>> GetTablesAsync()
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT tablename FROM pg_tables WHERE schemaname = 'public'", conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync())
        {
            tables.Add(reader.GetString(0));
        }

        return tables;
    }

    private static async Task ApplyScriptsAsync(string connectionString)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        foreach (var sql in LoadBaselineScripts())
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync();
        }

        // The Outbox table is shared schema, applied from the embedded
        // canonical DDL rather than duplicated in Driver migrations.
        await DriverOutboxSchema.EnsureAsync(connectionString);
    }

    private static IEnumerable<string> LoadBaselineScripts()
    {
        var dir = FindMigrationsDir();
        return Directory.GetFiles(dir, "*.sql")
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .Select(File.ReadAllText);
    }

    private static string FindMigrationsDir()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Infrastructure", "Persistence", "Migrations"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                "..", "..", "..", "..", "..", "..",
                "src", "Services", "Driver", "RideHailing.DriverService",
                "Infrastructure", "Persistence", "Migrations")),
            Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(),
                "..", "..", "..", "..",
                "src", "Services", "Driver", "RideHailing.DriverService",
                "Infrastructure", "Persistence", "Migrations")),
        };
        foreach (var p in candidates)
        {
            if (Directory.Exists(p))
            {
                return p;
            }
        }

        throw new DirectoryNotFoundException(
            "Driver Migrations dir not found. Tried: " + string.Join("; ", candidates));
    }

    // Testcontainers returns localhost-/gateway-mapped endpoints, unreachable
    // from inside a sibling container: rewrite to host.docker.internal there.
    // Native runs (CI ubuntu, real localhost) keep the original value.
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
