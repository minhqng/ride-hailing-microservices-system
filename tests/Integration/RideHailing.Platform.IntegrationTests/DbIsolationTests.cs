// B-W02-09 (P1/P2 review fix): cross-service DB access is DENIED at the
// PostgreSQL authorization layer — not just absent from connection strings.
// Replicates deploy/postgres/init/01-roles-dbs.sh isolation (per-DB roles +
// REVOKE CONNECT FROM PUBLIC) inside a disposable container and asserts both
// directions fail with 42501, while owned access works.
// Feeds SEC-08 (Doc 03; owner C): this is B-side executable evidence C can cite.
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit.Abstractions;

namespace RideHailing.Platform.IntegrationTests;

public class DbIsolationTests : IAsyncLifetime
{
    static DbIsolationTests()
    {
        // Same as service fixtures: Ryuk cannot start inside the SDK container;
        // DisposeAsync cleans up explicitly.
        Environment.SetEnvironmentVariable("TESTCONTAINERS_RYUK_DISABLED", "true");
    }

    private readonly ITestOutputHelper _out;
    private PostgreSqlContainer? _container;
    private bool _skipped;
    private string _skipReason = "";

    public DbIsolationTests(ITestOutputHelper output)
    {
        _out = output;
    }

    public async Task InitializeAsync()
    {
        static bool DockerMightExist() =>
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOCKER_HOST"))
            || File.Exists("/var/run/docker.sock");

        if (!DockerMightExist())
        {
            _skipped = true;
            _skipReason = "No DOCKER_HOST and no /var/run/docker.sock.";
            return;
        }

        _container = new PostgreSqlBuilder("postgres:18.6").Build();
        await _container.StartAsync();
        await ApplyIsolationTopologyAsync(SuperuserConnectionString());
        _out.WriteLine("INFRA: disposable postgres with isolated roles — real assertions will run.");
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    private string SuperuserConnectionString() =>
        _container?.GetConnectionString() ?? throw new InvalidOperationException("Container not started.");

    private string RoleConnectionString(string database, string user, string password)
    {
        var super = new NpgsqlConnectionStringBuilder(FixHost(SuperuserConnectionString()));
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = super.Host,
            Port = super.Port,
            Database = database,
            Username = user,
            Password = password,
        };
        return builder.ToString();
    }

    private static string FixHost(string cs) =>
        File.Exists("/.dockerenv")
            ? cs.Replace("Host=localhost", "Host=host.docker.internal")
                .Replace("Host=172.17.0.1", "Host=host.docker.internal")
            : cs;

    private async Task ApplyIsolationTopologyAsync(string superuserCs)
    {
        // Mirror of deploy/postgres/init/01-roles-dbs.sh for the two B-owned roles.
        var statements = new[]
        {
            "CREATE ROLE driver_app WITH LOGIN PASSWORD 'driver_dev_pw'",
            "CREATE ROLE matching_app WITH LOGIN PASSWORD 'matching_dev_pw'",
            "CREATE DATABASE driver_db OWNER driver_app",
            "CREATE DATABASE matching_db OWNER matching_app",
            "REVOKE CONNECT ON DATABASE driver_db FROM PUBLIC",
            "GRANT CONNECT ON DATABASE driver_db TO driver_app",
            "REVOKE CONNECT ON DATABASE matching_db FROM PUBLIC",
            "GRANT CONNECT ON DATABASE matching_db TO matching_app",
        };
        await using var conn = new NpgsqlConnection(FixHost(superuserCs));
        await conn.OpenAsync();
        foreach (var sql in statements)
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private static async Task<PostgresException> ConnectDeniedAsync(string connectionString)
    {
        await using var conn = new NpgsqlConnection(FixHost(connectionString));
        var ex = await Assert.ThrowsAsync<PostgresException>(() => conn.OpenAsync());
        return ex;
    }

    [Fact]
    public async Task Driver_Role_Cannot_Connect_Matching_Db()
    {
        if (_skipped)
        {
            _out.WriteLine("SKIP: " + _skipReason);
            return;
        }

        var ex = await ConnectDeniedAsync(RoleConnectionString("matching_db", "driver_app", "driver_dev_pw"));
        Assert.Equal("42501", ex.SqlState);
    }

    [Fact]
    public async Task Matching_Role_Cannot_Connect_Driver_Db()
    {
        if (_skipped)
        {
            _out.WriteLine("SKIP: " + _skipReason);
            return;
        }

        var ex = await ConnectDeniedAsync(RoleConnectionString("driver_db", "matching_app", "matching_dev_pw"));
        Assert.Equal("42501", ex.SqlState);
    }

    [Fact]
    public async Task Each_Role_Connects_Own_Db()
    {
        if (_skipped)
        {
            _out.WriteLine("SKIP: " + _skipReason);
            return;
        }

        await using var driver = new NpgsqlConnection(
            FixHost(RoleConnectionString("driver_db", "driver_app", "driver_dev_pw")));
        await driver.OpenAsync();
        await using var matching = new NpgsqlConnection(
            FixHost(RoleConnectionString("matching_db", "matching_app", "matching_dev_pw")));
        await matching.OpenAsync();
    }
}
