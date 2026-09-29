// Trip integration-test host (P3, A-W02-04).
//
// Uses the ACTUAL Trip ASP.NET Core application via the standard
// WebApplicationFactory<Program> pattern. No fake application entry path.
// The only test override is the PostgreSQL connection string, which points at
// the disposable Testcontainers instance; production appsettings are never
// modified with disposable credentials and the production DI path is
// otherwise preserved.

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace RideHailing.TripService.IntegrationTests;

/// <summary>
/// Boots the real TripService against a supplied (disposable test) PostgreSQL.
/// </summary>
public sealed class TripApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _postgresConnectionString;

    public TripApplicationFactory(string postgresConnectionString)
    {
        _postgresConnectionString = postgresConnectionString;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // UseSetting flows into the SUT's WebApplicationBuilder configuration
        // with precedence over appsettings, so Program.cs reads the disposable
        // test connection through the unchanged production config path.
        // (An IWebHostBuilder.ConfigureAppConfiguration in-memory override was
        // tried first and does NOT reach minimal-host CreateBuilder config at
        // Program startup; UseSetting does.)
        builder.UseSetting("ConnectionStrings:Postgres", _postgresConnectionString);
    }
}
