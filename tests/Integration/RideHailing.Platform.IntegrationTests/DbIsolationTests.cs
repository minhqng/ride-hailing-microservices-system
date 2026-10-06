// B-W02-09: cross-service DB access is DENIED at the PostgreSQL authorization
// layer — not just absent from connection strings.
// Scope: CONNECT denial exactly as SEC-08 requires (Doc 03: Trip→Driver and
// Matching→Driver attempts must fail authorization). Schema-level privileges
// (TEMPORARY, CREATE) and the other four service roles belong to the platform
// init script review (C-owned), not to this CONNECT-scoped proof.
// Replicates the CONNECT half of deploy/postgres/init/01-roles-dbs.sh
// (per-DB roles + REVOKE CONNECT FROM PUBLIC) inside a disposable container
// and asserts both directions fail with 42501, while owned access works.
// Feeds SEC-08 (Doc 03; owner C): this is B-side executable evidence C can cite.
using Npgsql;
using Testcontainers.PostgreSql;

namespace RideHailing.Platform.IntegrationTests;

public class DbIsolationTests : IAsyncLifetime
{
    static DbIsolationTests()
    {
        // Same as service fixtures: Ryuk cannot start inside the SDK container;
        // DisposeAsync + AutoRemove clean up explicitly.
        Environment.SetEnvironmentVariable("TESTCONTAINERS_RYUK_DISABLED", "true");
    }

    private PostgreSqlContainer? _container;

    public async Task InitializeAsync()
    {
        _container = new PostgreSqlBuilder("postgres:18.6")
            .WithAutoRemove(true)
            .Build();
        await _container.StartAsync();
        await ApplyIsolationTopologyAsync(SuperuserConnectionString());
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
        var ex = await ConnectDeniedAsync(RoleConnectionString("matching_db", "driver_app", "driver_dev_pw"));
        Assert.Equal("42501", ex.SqlState);
    }

    [Fact]
    public async Task Matching_Role_Cannot_Connect_Driver_Db()
    {
        var ex = await ConnectDeniedAsync(RoleConnectionString("driver_db", "matching_app", "matching_dev_pw"));
        Assert.Equal("42501", ex.SqlState);
    }

    [Fact]
    public async Task Each_Role_Connects_Own_Db()
    {
        await using var driver = new NpgsqlConnection(
            FixHost(RoleConnectionString("driver_db", "driver_app", "driver_dev_pw")));
        await driver.OpenAsync();
        await using var matching = new NpgsqlConnection(
            FixHost(RoleConnectionString("matching_db", "matching_app", "matching_dev_pw")));
        await matching.OpenAsync();
    }
}
