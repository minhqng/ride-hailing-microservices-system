// B-W02-06 (P1 review fix): Driver PostgreSQL fixture with no fake-green.
// Semantics (frozen):
//   - DRIVER_TEST_POSTGRES (or ConnectionStrings__Postgres) explicitly set
//     => infra is EXPECTED => any failure THROWS (test fails loud, never skips).
//   - No env => disposable Testcontainers PostgreSQL (postgres:18.6, same seed
//     as compose). Start failure throws; only a provably missing Docker daemon
//     produces a loud SKIP (DockerUnavailableException).
//   - Baseline scripts are applied in filename order (001, 002, ...).
using System.Net.Sockets;
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
    public string Source { get; private set; } = "";
    public bool Skipped { get; private set; }
    public string SkipReason { get; private set; } = "";

    public bool Ready() => !Skipped && !string.IsNullOrEmpty(ConnectionString);

    public async Task InitializeAsync()
    {
        var env = Environment.GetEnvironmentVariable("DRIVER_TEST_POSTGRES")
            ?? Environment.GetEnvironmentVariable("ConnectionStrings__Postgres");

        if (!string.IsNullOrWhiteSpace(env))
        {
            // Explicit infra expectation: verify now, fail loud on any error.
            await using var probe = new NpgsqlConnection(env);
            await probe.OpenAsync();
            ConnectionString = env;
            Source = "env";
            await ApplyScriptsAsync(env);
            return;
        }

        if (!DockerMightExist())
        {
            Skipped = true;
            SkipReason = "No DOCKER_HOST and no /var/run/docker.sock: cannot start disposable PostgreSQL.";
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
        Source = "testcontainers";
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

    // Configured (not yet connected) connection string for ownership assertions.
    // Testcontainers default is used when no env is set.
    public static string ConfiguredConnectionString() =>
        Environment.GetEnvironmentVariable("DRIVER_TEST_POSTGRES")
        ?? Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")
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

    private static bool DockerMightExist() =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOCKER_HOST"))
        || File.Exists("/var/run/docker.sock");

    // Testcontainers returns localhost- or gateway-mapped endpoints, which are
    // unreachable from inside a sibling container (SDK-container runs on Docker
    // Desktop). Rewrite to host.docker.internal only in that topology; native
    // runs (CI ubuntu, real localhost) keep the original value.
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
