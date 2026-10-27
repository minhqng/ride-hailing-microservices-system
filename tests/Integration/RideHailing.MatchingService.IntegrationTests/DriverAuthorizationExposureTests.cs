// B-W12-05 / SEC-01..04: REAL Kestrel negative HTTP attempts. The repository
// currently has no authenticated Driver principal: Identity and Gateway are
// skeletons (no token issuance/validation, no middleware). These tests
// therefore RECORD THE OPEN EXPOSURE (unauthorized mutation succeeds); they do
// NOT claim an authorization PASS. If service-side authentication lands, these
// tripwires fail and the evidence must be updated.
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
[Trait("SecurityFinding", "SYSTEM_AUTH_PENDING")]
public sealed class DriverAuthorizationExposureTests
{
    private readonly DriverTestDatabase _drivers;
    private readonly ITestOutputHelper _output;

    public DriverAuthorizationExposureTests(DriverTestDatabase drivers, ITestOutputHelper output)
    {
        _drivers = drivers;
        _output = output;
    }

    [Fact]
    public async Task Exposure_Unauthenticated_Caller_Mutates_Driver_And_Creates_Outbox_Intent()
    {
        var driver = await SeedAsync();
        await using var host = BuildHost();
        await host.StartAsync();
        using var http = Client(host);

        // No Authorization header, no client certificate, no trusted service
        // identity. SEC-01/02 require rejection here.
        using var response = await http.PostAsJsonAsync(
            $"drivers/{driver.DriverId:N}/online",
            new { expectedVersion = driver.Version });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var after = (await Repository().LoadAsync(driver.DriverId))!;
        Assert.Equal(DriverStatus.Available, after.Status);
        Assert.True(after.Version > driver.Version);
        Assert.Equal(1, await OutboxCountAsync(driver.DriverId, "DriverAvailableV1"));

        // Create/read/offline are equally unauthenticated.
        using var create = await http.PostAsJsonAsync("drivers", new { userId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        using var createdDoc = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        var createdId = createdDoc.RootElement.GetProperty("driverId").GetGuid();
        using var read = await http.GetAsync($"drivers/{createdId:N}");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);

        using var offline = await http.PostAsJsonAsync(
            $"drivers/{driver.DriverId:N}/offline",
            new { expectedVersion = after.Version });
        Assert.Equal(HttpStatusCode.OK, offline.StatusCode);
        var offlineState = (await Repository().LoadAsync(driver.DriverId))!;
        Assert.Equal(DriverStatus.Offline, offlineState.Status);
        Assert.Equal(1, await OutboxCountAsync(driver.DriverId, "DriverOfflineV1"));

        _output.WriteLine(
            $"EXPOSURE SEC-01/02: unauthenticated POST online accepted; " +
            $"driver={driver.DriverId} version {driver.Version}->{after.Version} " +
            "and DriverAvailableV1 Outbox intent committed. " +
            "Unauthenticated create/read/offline also accepted.");

        await host.StopAsync();
    }

    [Fact]
    public async Task Exposure_Spoofed_Actor_Headers_Mutate_Another_Driver_And_Internal_Reserve()
    {
        var driverA = await SeedAsync();
        var driverB = await SeedAsync();
        var riderTarget = await SeedAsync();
        var reservable = await SeedAsync();
        var onlineResult = await Repository().TryGoOnlineAsync(
            reservable.DriverId, reservable.Version);
        Assert.Equal(DriverMutationOutcome.Applied, onlineResult.Outcome);
        var online = onlineResult.Driver!;

        await using var host = BuildHost();
        await host.StartAsync();
        using var http = Client(host);
        http.DefaultRequestHeaders.TryAddWithoutValidation("X-Driver-Id", driverA.DriverId.ToString("D"));
        http.DefaultRequestHeaders.TryAddWithoutValidation("X-User-Id", Guid.NewGuid().ToString("D"));
        http.DefaultRequestHeaders.TryAddWithoutValidation("X-Role", "driver");
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "unsigned-spoofed-token");

        // Driver A (claimed by headers) mutates Driver B's route object.
        using var onlineB = await http.PostAsJsonAsync(
            $"drivers/{driverB.DriverId:N}/online",
            new { expectedVersion = driverB.Version });
        Assert.Equal(HttpStatusCode.OK, onlineB.StatusCode);
        var afterB = (await Repository().LoadAsync(driverB.DriverId))!;
        Assert.Equal(DriverStatus.Available, afterB.Status);
        Assert.Equal(1, await OutboxCountAsync(driverB.DriverId, "DriverAvailableV1"));

        // Rider role has no Driver function boundary: the same call succeeds.
        http.DefaultRequestHeaders.Remove("X-Role");
        http.DefaultRequestHeaders.TryAddWithoutValidation("X-Role", "rider");
        using var riderOnline = await http.PostAsJsonAsync(
            $"drivers/{riderTarget.DriverId:N}/online",
            new { expectedVersion = riderTarget.Version });
        Assert.Equal(HttpStatusCode.OK, riderOnline.StatusCode);
        Assert.Equal(DriverStatus.Available,
            (await Repository().LoadAsync(riderTarget.DriverId))!.Status);

        // Unauthorized internal reserve/release are reachable directly on the
        // Compose-exposed Driver port and carry no service identity.
        var trip = Guid.NewGuid();
        var reservation = Guid.NewGuid();
        using var reserve = await http.PostAsJsonAsync(
            $"drivers/{reservable.DriverId:N}/reserve",
            new
            {
                tripId = trip,
                reservationId = reservation,
                expectedVersion = online.Version,
                matchingRound = 1,
            });
        Assert.Equal(HttpStatusCode.OK, reserve.StatusCode);
        var reserved = (await Repository().LoadAsync(reservable.DriverId))!;
        Assert.Equal(DriverStatus.Reserved, reserved.Status);
        Assert.Equal(reservation, reserved.ReservationId);
        Assert.Equal(1, await OutboxCountAsync(reservable.DriverId, "DriverReservedV1"));

        using var readReservation = await http.GetAsync(
            $"internal/drivers/{reservable.DriverId:N}/reservations/{reservation:N}");
        Assert.Equal(HttpStatusCode.OK, readReservation.StatusCode);

        using var release = await http.PostAsJsonAsync(
            $"drivers/{reservable.DriverId:N}/release",
            new { tripId = trip, reservationId = reservation });
        Assert.Equal(HttpStatusCode.OK, release.StatusCode);
        Assert.Equal(DriverStatus.Available,
            (await Repository().LoadAsync(reservable.DriverId))!.Status);
        Assert.Equal(1, await OutboxCountAsync(reservable.DriverId, "DriverReservationReleasedV1"));

        _output.WriteLine(
            "EXPOSURE SEC-02/03/04: spoofed X-Driver-Id/X-Role and unsigned Bearer " +
            "are ignored, not rejected; Driver A's claimed identity can mutate Driver B; " +
            "rider role performs a Driver operation; internal reserve/release/reconcile " +
            "are unauthenticated. Every accepted call changed state and/or Outbox state.");
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
        DriverEndpoints.Map(app);
        return app;
    }
}
