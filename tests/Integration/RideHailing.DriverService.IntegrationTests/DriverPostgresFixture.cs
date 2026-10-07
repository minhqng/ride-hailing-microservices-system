// Driver PostgreSQL fixture: REAL infrastructure, never a vacuous pass.
// Resolution order:
//   1. DRIVER_TEST_POSTGRES explicitly set => verify now; any failure throws loud.
//   2. Otherwise disposable Testcontainers PostgreSQL (postgres:18.6, same seed
//      as compose); any start/apply failure throws loud ( Ryuk disabled; cleanup
//      is explicit via AutoRemove + DisposeAsync).
// Applies ALL migration scripts in filename order (001, 002, ...).
using Npgsql;
using Testcontainers.PostgreSql;

namespace RideHailing.DriverService.IntegrationTests;

public sealed class DriverPostgresDatabase : IAsyncLifetime
{
    static DriverPostgresDatabase()
    {
        // Ryuk (Testcontainers janitor) cannot start reliably from inside the
        // SDK container on Docker Desktop. Cleanup is explicit (AutoRemove +
        // DisposeAsync), so disabling it is safe and keeps runs deterministic.
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
            await ApplyScriptsAsync(env);
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
        await ApplyScriptsAsync(ConnectionString);
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    public Task ReapplyScriptsAsync() => ApplyScriptsAsync(ConnectionString);

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
