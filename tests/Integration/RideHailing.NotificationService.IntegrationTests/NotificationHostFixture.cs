// Shared real-PostgreSQL + real-host fixture for the DS03 notification inbox
// suite. The host runs the production Program with messaging disabled (no
// broker consumer in this unit of proof) and a generated RSA key so tests use
// REAL signed JWTs, not a test-principal bypass.

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using RideHailing.NotificationWorker;
using Testcontainers.PostgreSql;

namespace RideHailing.NotificationService.IntegrationTests;

[CollectionDefinition(Name)]
public sealed class NotificationCollection : ICollectionFixture<NotificationHostFixture>
{
    public const string Name = "Notification real PostgreSQL + host";
}

public sealed class NotificationApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _connectionString;
    private readonly RSA _rsa = RSA.Create(2048);

    public NotificationApplicationFactory(string connectionString)
    {
        _connectionString = connectionString;
    }

    public string CreateToken(Guid userId, string role = "Rider")
    {
        var key = new RsaSecurityKey(_rsa);
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, userId.ToString("D")),
            new Claim("role", role),
        };
        var token = new JwtSecurityToken(
            issuer: "ride-hailing-identity",
            audience: "ride-hailing-api",
            claims: claims,
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(10),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.RsaSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Postgres", _connectionString);
        builder.UseSetting("NotificationMessaging:Enabled", "false");
        builder.UseSetting("Auth:PublicKeyBase64",
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(_rsa.ExportSubjectPublicKeyInfoPem())));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _rsa.Dispose();
        base.Dispose(disposing);
    }
}

public sealed class NotificationHostFixture : IAsyncLifetime
{
    public PostgreSqlContainer Container { get; } = new PostgreSqlBuilder(
            "postgres:18.6@sha256:3a4264520e121176619f54ff867d12774bc81e95e7b84db58d63bccdb6144b81")
        .WithDatabase("notification_db")
        .WithUsername("notification_app")
        .Build();

    public NotificationApplicationFactory Factory { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;

    public NotificationProjectionStore Store =>
        Factory.Services.GetRequiredService<NotificationProjectionStore>();

    public NotificationEventProcessor Processor =>
        Factory.Services.GetRequiredService<NotificationEventProcessor>();

    public string ConnectionString => Container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await Container.StartAsync();
        Factory = new NotificationApplicationFactory(ConnectionString);
        Client = Factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        Client.Dispose();
        Factory.Dispose();
        await Container.DisposeAsync();
    }

    public async Task<NpgsqlDataSource> NewDataSourceAsync() =>
        NpgsqlDataSource.Create(ConnectionString);
}
