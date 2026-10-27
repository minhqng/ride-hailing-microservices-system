// B-W12-08: disposable real-PostgreSQL fixtures. Every scenario gets its own
// database on a service-specific container, so seeded corruption can never
// touch an ordinary scenario database.
using Microsoft.EntityFrameworkCore;
using Npgsql;
using RideHailing.DriverService.Infrastructure.Outbox;
using RideHailing.MatchingService.Infrastructure.Outbox;
using RideHailing.TripService.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace RideHailing.B12Audit.Tests;

public sealed class AuditDatabaseSet(
    string driverConnectionString,
    string matchingConnectionString,
    string? tripConnectionString,
    string driverAdminConnectionString,
    string matchingAdminConnectionString,
    string? tripAdminConnectionString)
{
    public string DriverConnectionString { get; } = driverConnectionString;
    public string MatchingConnectionString { get; } = matchingConnectionString;
    public string? TripConnectionString { get; } = tripConnectionString;
    public string DriverAdminConnectionString { get; } = driverAdminConnectionString;
    public string MatchingAdminConnectionString { get; } = matchingAdminConnectionString;
    public string? TripAdminConnectionString { get; } = tripAdminConnectionString;

    public TripDbContext CreateTripDbContext() =>
        new(new DbContextOptionsBuilder<TripDbContext>()
            .UseNpgsql(TripConnectionString ?? throw new InvalidOperationException("Trip DB is not configured."))
            .Options);
}

[CollectionDefinition(Name)]
public sealed class B12AuditCollection : ICollectionFixture<B12AuditFixture>
{
    public const string Name = "b12-audit";
}

public sealed class B12AuditFixture : IAsyncLifetime
{
    static B12AuditFixture()
    {
        Environment.SetEnvironmentVariable("TESTCONTAINERS_RYUK_DISABLED", "true");
    }

    private PostgreSqlContainer? _driver;
    private PostgreSqlContainer? _matching;
    private PostgreSqlContainer? _trip;
    private int _sequence;

    public AuditReportCollector Report { get; } = new();

    public async Task InitializeAsync()
    {
        _driver = new PostgreSqlBuilder("postgres:18.6")
            .WithDatabase("driver_db").WithUsername("driver_app").WithPassword("driver_dev_pw")
            .WithAutoRemove(true).Build();
        _matching = new PostgreSqlBuilder("postgres:18.6")
            .WithDatabase("matching_db").WithUsername("matching_app").WithPassword("matching_dev_pw")
            .WithAutoRemove(true).Build();
        _trip = new PostgreSqlBuilder("postgres:18.6")
            .WithDatabase("trip_db").WithUsername("trip_app").WithPassword("trip_dev_pw")
            .WithAutoRemove(true).Build();

        await Task.WhenAll(_driver.StartAsync(), _matching.StartAsync(), _trip.StartAsync());
    }

    public async Task DisposeAsync()
    {
        await Task.WhenAll(
            _driver?.DisposeAsync().AsTask() ?? Task.CompletedTask,
            _matching?.DisposeAsync().AsTask() ?? Task.CompletedTask,
            _trip?.DisposeAsync().AsTask() ?? Task.CompletedTask);
    }

    public async Task<AuditDatabaseSet> CreateDatabasesAsync(string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        var slug = new string(label.ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
        var suffix = $"{Interlocked.Increment(ref _sequence):D3}";
        var driverAdmin = _driver!.GetConnectionString();
        var matchingAdmin = _matching!.GetConnectionString();
        var tripAdmin = _trip!.GetConnectionString();
        var driverCs = await CreateDatabaseAsync(driverAdmin, $"b12_driver_{slug}_{suffix}");
        var matchingCs = await CreateDatabaseAsync(matchingAdmin, $"b12_matching_{slug}_{suffix}");
        var tripCs = await CreateDatabaseAsync(tripAdmin, $"b12_trip_{slug}_{suffix}");

        await ApplyDriverSchemaAsync(driverCs);
        await ApplyMatchingSchemaAsync(matchingCs);
        await ApplyTripSchemaAsync(tripCs);
        return new AuditDatabaseSet(driverCs, matchingCs, tripCs, driverAdmin, matchingAdmin, tripAdmin);
    }

    public async Task<AuditDatabaseSet> CreateDriverOnlyAsync(string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        var slug = new string(label.ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
        var suffix = $"{Interlocked.Increment(ref _sequence):D3}";
        var driverAdmin = _driver!.GetConnectionString();
        var driverCs = await CreateDatabaseAsync(driverAdmin, $"b12_driver_{slug}_{suffix}");
        await ApplyDriverSchemaAsync(driverCs);
        return new AuditDatabaseSet(
            driverCs,
            matchingConnectionString: "",
            tripConnectionString: null,
            driverAdmin,
            matchingAdminConnectionString: "",
            tripAdminConnectionString: null);
    }

    private static async Task<string> CreateDatabaseAsync(string adminConnectionString, string database)
    {
        await using (var admin = new NpgsqlConnection(adminConnectionString))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\";", admin);
            await create.ExecuteNonQueryAsync();
        }

        var builder = new NpgsqlConnectionStringBuilder(adminConnectionString)
        {
            Database = database,
        };
        return builder.ConnectionString;
    }

    private static async Task ApplyDriverSchemaAsync(string connectionString)
    {
        var sql = string.Join("\n", Directory
            .GetFiles(Path.Combine(AuditEnvironment.RepoRoot,
                "src", "Services", "Driver", "RideHailing.DriverService",
                "Infrastructure", "Persistence", "Migrations"), "*.sql")
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .Select(File.ReadAllText));
        await using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }

        await DriverOutboxSchema.EnsureAsync(connectionString);
    }

    private static async Task ApplyMatchingSchemaAsync(string connectionString)
    {
        var migrationDir = Path.Combine(AuditEnvironment.RepoRoot,
            "src", "Services", "Matching", "RideHailing.MatchingService",
            "Infrastructure", "Persistence", "Migrations");
        await using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync();
            foreach (var file in Directory.GetFiles(migrationDir, "*.sql")
                         .OrderBy(Path.GetFileName, StringComparer.Ordinal))
            {
                await using var command = new NpgsqlCommand(File.ReadAllText(file), connection);
                await command.ExecuteNonQueryAsync();
            }
        }

        await MatchingOutboxSchema.EnsureAsync(connectionString);
    }

    private static async Task ApplyTripSchemaAsync(string connectionString)
    {
        var options = new DbContextOptionsBuilder<TripDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        await using var db = new TripDbContext(options);
        await db.Database.MigrateAsync();
    }
}
