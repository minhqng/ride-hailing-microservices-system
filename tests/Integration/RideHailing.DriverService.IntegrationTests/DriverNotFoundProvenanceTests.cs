// L2 contract proof: a bare HTTP 404 has no domain provenance (it may come
// from routing/proxy/transport), so Driver must emit a structured
// DRIVER_NOT_FOUND body that an authoritative consumer can distinguish from a
// transport 404. Real Kestrel + real PostgreSQL; no handler stubs.
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RideHailing.DriverService.Api;
using RideHailing.DriverService.Application.Location;
using RideHailing.DriverService.Application.Messaging;
using RideHailing.DriverService.Domain.Drivers;
using RideHailing.DriverService.Infrastructure.Faults;
using RideHailing.DriverService.Infrastructure.Persistence;
using RideHailing.Messaging.Outbox;

namespace RideHailing.DriverService.IntegrationTests;

[Collection(DriverPostgresCollection.Name)]
public sealed class DriverNotFoundProvenanceTests
{
    private readonly DriverPostgresDatabase _db;

    public DriverNotFoundProvenanceTests(DriverPostgresDatabase db)
    {
        _db = db;
    }

    private static WebApplication BuildHost(DriverPostgresDatabase database)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var outbox = new NpgsqlOutboxStore(database.DataSource);
        builder.Services.AddSingleton(database.DataSource);
        builder.Services.AddSingleton(outbox);
        builder.Services.AddSingleton<IOutboxStore>(outbox);
        builder.Services.AddSingleton(new DriverRepository(database.DataSource, outbox));
        builder.Services.AddSingleton(DriverFaultPoint.Disabled());
        builder.Services.AddOptions<LocationIngressOptions>();
        builder.Services.AddOptions<DriverBrokerOptions>()
            .Configure(options => options.PublishingEnabled = false);
        builder.Services.AddSingleton<LocationAcceptor>();
        builder.Services.AddSingleton<DriverEventPublisher>();

        var app = builder.Build();
        DriverEndpoints.Map(app);
        return app;
    }

    private static string AddressOf(WebApplication app) =>
        app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses.Single()
            .TrimEnd('/');

    private static async Task<string?> ReadCodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.TryGetProperty("code", out var code)
            ? code.GetString()
            : null;
    }

    [Fact]
    public async Task MissingDriver_HttpEndpoints_CarryStructuredNotFoundProvenance()
    {
        await using var app = BuildHost(_db);
        await app.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri(AddressOf(app) + "/") };
        var missing = Guid.NewGuid();

        using var read = await http.GetAsync($"drivers/{missing:N}");
        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        Assert.Equal(DriverErrors.DriverNotFound, await ReadCodeAsync(read));

        using var reserve = await http.PostAsJsonAsync($"drivers/{missing:N}/reserve", new
        {
            tripId = Guid.NewGuid(),
            reservationId = Guid.NewGuid(),
            expectedVersion = 1,
            matchingRound = 1,
        });
        Assert.Equal(HttpStatusCode.NotFound, reserve.StatusCode);
        Assert.Equal(DriverErrors.DriverNotFound, await ReadCodeAsync(reserve));

        await app.StopAsync();
    }

    [Fact]
    public async Task ExistingDriver_Read_StillReturnsCanonicalState()
    {
        var driver = Driver.Create(Guid.NewGuid(), Guid.NewGuid());
        driver.GoOnline(driver.Version);
        Assert.True(await _db.CreateRepository().TryInsertAsync(driver));

        await using var app = BuildHost(_db);
        await app.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri(AddressOf(app) + "/") };

        using var response = await http.GetAsync($"drivers/{driver.DriverId:N}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("AVAILABLE", document.RootElement.GetProperty("status").GetString());
        Assert.Equal(driver.Version, document.RootElement.GetProperty("version").GetInt32());

        await app.StopAsync();
    }
}
