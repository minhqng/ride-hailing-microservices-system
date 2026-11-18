// Matching PostgreSQL fixture: REAL infrastructure, never a vacuous pass.
// MATCHING_TEST_POSTGRES explicitly set => verified now, failure throws loud.
// Otherwise disposable Testcontainers PostgreSQL; any start/apply failure
// throws loud ( Ryuk disabled; cleanup explicit via AutoRemove + DisposeAsync).
// Applies ALL migration scripts in filename order (001 baseline, 002 status fix, ...).
using Npgsql;
using RideHailing.Messaging.Outbox;
using Testcontainers.PostgreSql;

namespace RideHailing.MatchingService.IntegrationTests;

public sealed class MatchingPostgresDatabase : IAsyncLifetime
{
    static MatchingPostgresDatabase()
    {
        // See DriverPostgresDatabase: explicit dispose; Ryuk disabled by
        // default, but an explicit operator value wins.
        Environment.SetEnvironmentVariable(
            "TESTCONTAINERS_RYUK_DISABLED",
            Environment.GetEnvironmentVariable("TESTCONTAINERS_RYUK_DISABLED") ?? "true");
    }

    private PostgreSqlContainer? _container;

    public string ConnectionString { get; private set; } = "";

    /// <summary>Connection pool used by Outbox/terminalization components.</summary>
    public NpgsqlDataSource DataSource { get; private set; } = null!;

    /// <summary>Canonical shared Outbox store over the Matching database.</summary>
    public NpgsqlOutboxStore OutboxStore { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var env = Environment.GetEnvironmentVariable("MATCHING_TEST_POSTGRES");
        if (!string.IsNullOrWhiteSpace(env))
        {
            EnsureDisposable(env, "MATCHING_TEST_POSTGRES");
            await using var probe = new NpgsqlConnection(env);
            await probe.OpenAsync();
            ConnectionString = env;
            await ApplyScriptsAsync(env);
            await EnsureSharedSchemaAsync();
            return;
        }

        _container = new PostgreSqlBuilder("postgres:18.6@sha256:3a4264520e121176619f54ff867d12774bc81e95e7b84db58d63bccdb6144b81")
            .WithDatabase("matching_db")
            .WithUsername("matching_app")
            .WithPassword("matching_dev_pw")
            .WithAutoRemove(true)
            .Build();
        await _container.StartAsync();
        ConnectionString = FixContainerHost(_container.GetConnectionString());
        await ApplyScriptsAsync(ConnectionString);
        await EnsureSharedSchemaAsync();
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

    public async Task ReapplyScriptsAsync()
    {
        await ApplyScriptsAsync(ConnectionString);
        await EnsureSharedSchemaAsync();
    }

    /// <summary>
    /// Apply the embedded Outbox DDL after the Matching-owned migrations.
    /// NpgsqlOutboxStore.EnsureSchemaAsync is the single schema source (no
    /// test-local copy), matching the production startup path.
    /// </summary>
    private async Task EnsureSharedSchemaAsync()
    {
        if (DataSource is null)
        {
            DataSource = NpgsqlDataSource.Create(ConnectionString);
            OutboxStore = new NpgsqlOutboxStore(DataSource);
        }

        await OutboxStore.EnsureSchemaAsync();
    }

    /// <summary>
    /// Loads one migration script by filename for tests that must apply
    /// migrations out of order (e.g. legacy-dataset repair proofs).
    /// </summary>
    public static string LoadMigrationScript(string fileName) =>
        File.ReadAllText(Path.Combine(FindMigrationsDir(), fileName));

    // Configured (not yet connected) connection string for ownership assertions.
    public static string ConfiguredConnectionString() =>
        Environment.GetEnvironmentVariable("MATCHING_TEST_POSTGRES")
        ?? "Host=localhost;Port=5432;Database=matching_db;Username=matching_app;Password=matching_dev_pw";

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
        // NOTE: source-tree candidates come FIRST. Test assemblies
        // that (transitively) reference another service inherit that
        // service's copied *.sql Content into their own output dir, so the
        // bin-dir candidate below can resolve to FOREIGN scripts (e.g. driver
        // migrations inside matching test output). Source tree is always
        // present where tests run and is unambiguous per service.
        var candidates = new[]
        {
            // Dev/test source-tree fallbacks (service + test working dirs).
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                "..", "..", "..", "..", "..", "..",
                "src", "Services", "Matching", "RideHailing.MatchingService",
                "Infrastructure", "Persistence", "Migrations")),
            Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(),
                "..", "..", "..", "..",
                "src", "Services", "Matching", "RideHailing.MatchingService",
                "Infrastructure", "Persistence", "Migrations")),
            // Published service output (csproj copies *.sql to output).
            Path.Combine(AppContext.BaseDirectory, "Infrastructure", "Persistence", "Migrations"),
        };
        foreach (var p in candidates)
        {
            if (Directory.Exists(p))
            {
                return p;
            }
        }

        throw new DirectoryNotFoundException(
            "Matching Migrations dir not found. Tried: " + string.Join("; ", candidates));
    }

    // Same sibling-container host rewrite as Driver (see DriverPostgresDatabase).
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
