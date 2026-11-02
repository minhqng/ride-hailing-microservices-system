using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using RideHailing.DriverService.Api;
using RideHailing.DriverService.Application.Location;
using RideHailing.DriverService.Application.Messaging;
using RideHailing.DriverService.Domain.Drivers;
using RideHailing.DriverService.Infrastructure.Faults;
using RideHailing.DriverService.Infrastructure.Persistence;
using RideHailing.Messaging.Outbox;
using Xunit.Abstractions;

namespace RideHailing.MatchingService.IntegrationTests;

[Collection(W12IngressCollection.Name)]

public sealed class DriverAuthorizationExposureTests
{
    private readonly DriverTestDatabase _drivers;
    private readonly ITestOutputHelper _output;

    public DriverAuthorizationExposureTests(DriverTestDatabase drivers, ITestOutputHelper output)
    {
        _drivers = drivers;
        _output = output;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnauthenticatedAndUnsignedRequestsCannotMutateDriver(bool spoof)
    {
        var driver = await SeedAsync();
        await using var host = BuildHost();
        await host.StartAsync();
        using var http = Client(host);
        if (spoof)
        {
            http.DefaultRequestHeaders.TryAddWithoutValidation("X-Driver-Id", driver.DriverId.ToString());
            http.DefaultRequestHeaders.TryAddWithoutValidation("X-Role", "Driver");
            http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "unsigned-spoofed-token");
        }
        var before = (await Repository().LoadAsync(driver.DriverId))!;
        using var online = await http.PostAsJsonAsync($"drivers/{driver.DriverId}/online",
            new { expectedVersion = driver.Version });
        Assert.Equal(HttpStatusCode.Unauthorized, online.StatusCode);
        using var create = await http.PostAsJsonAsync("drivers", new { userId = driver.UserId });
        Assert.Equal(HttpStatusCode.Unauthorized, create.StatusCode);
        using var reserve = await http.PostAsJsonAsync($"drivers/{driver.DriverId}/reserve",
            new { tripId = Guid.NewGuid(), reservationId = Guid.NewGuid(), expectedVersion = driver.Version, matchingRound = 1 });
        Assert.Equal(HttpStatusCode.Unauthorized, reserve.StatusCode);
        using var release = await http.PostAsJsonAsync($"drivers/{driver.DriverId}/release",
            new { tripId = Guid.NewGuid(), reservationId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.Unauthorized, release.StatusCode);
        using var read = await http.GetAsync($"drivers/{driver.DriverId}");
        Assert.Equal(HttpStatusCode.Unauthorized, read.StatusCode);
        var after = (await Repository().LoadAsync(driver.DriverId))!;
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(0, await OutboxCountAsync(driver.DriverId, "DriverAvailableV1"));
        Assert.Equal(0, await OutboxCountAsync(driver.DriverId, "DriverReservedV1"));
        Assert.Equal(0, await OutboxCountAsync(driver.DriverId, "DriverReservationReleasedV1"));
        await host.StopAsync();
    }
    private DriverRepository Repository() => _drivers.CreateRepository();

    private async Task<Driver> SeedAsync()
    {
        var driver = Driver.Create(Guid.NewGuid(), Guid.NewGuid());
        Assert.True(await Repository().TryInsertAsync(driver));
        return driver;
    }

    private async Task<long> OutboxCountAsync(Guid driverId, string messageType)
    {
        await using var connection = await _drivers.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            SELECT count(*) FROM outbox_messages
            WHERE aggregate_id = @id AND message_type = @type;
            """, connection);
        command.Parameters.AddWithValue("id", driverId.ToString("N"));
        command.Parameters.AddWithValue("type", messageType);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static HttpClient Client(WebApplication host)
    {
        var address = host.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        return new HttpClient { BaseAddress = new Uri(address + "/") };
    }

    private WebApplication BuildHost()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        DriverIngressLimits.Apply(builder);
        using var key = System.Security.Cryptography.RSA.Create(2048);
        builder.Configuration["Auth:PublicKeyBase64"] = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(key.ExportSubjectPublicKeyInfoPem()));
        RideHailing.Security.RideAuthentication.AddRideAuthentication(builder.Services, builder.Configuration);

        var outbox = new NpgsqlOutboxStore(_drivers.DataSource);
        builder.Services.AddSingleton(_drivers.DataSource);
        builder.Services.AddSingleton(outbox);
        builder.Services.AddSingleton<IOutboxStore>(outbox);
        builder.Services.AddSingleton(new DriverRepository(_drivers.DataSource, outbox));
        builder.Services.AddSingleton(DriverFaultPoint.Disabled());
        builder.Services.AddOptions<LocationIngressOptions>();
        builder.Services.AddOptions<DriverBrokerOptions>()
            .Configure(options =>
            {
                options.PublishingEnabled = false;
                options.CommandsEnabled = false;
            });
        builder.Services.AddSingleton<LocationAcceptor>();
        builder.Services.AddSingleton<DriverEventPublisher>();

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseDriverAccessGuard();
        DriverEndpoints.Map(app);
        return app;
    }
}
