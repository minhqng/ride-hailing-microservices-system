// B-REC-01 production proof authority: the REAL Driver endpoints on Kestrel
// over the shared driver_db. Matching talks HTTP only (no cross-service SQL);
// the fault seam is the production W11 implementation.
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RideHailing.DriverService.Api;
using RideHailing.DriverService.Application.Location;
using RideHailing.DriverService.Application.Messaging;
using RideHailing.DriverService.Infrastructure.Faults;
using RideHailing.DriverService.Infrastructure.Persistence;
using RideHailing.Messaging.Outbox;

namespace RideHailing.MatchingService.IntegrationTests;

internal sealed class DriverAuthorityHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    private DriverAuthorityHost(WebApplication app, string baseUrl)
    {
        _app = app;
        BaseUrl = baseUrl;
    }

    public string BaseUrl { get; }

    public static async Task<DriverAuthorityHost> StartAsync(
        DriverTestDatabase database, DriverFaultPoint faults, CancellationToken ct)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var outbox = new NpgsqlOutboxStore(database.DataSource);
        builder.Services.AddSingleton(database.DataSource);
        builder.Services.AddSingleton(outbox);
        builder.Services.AddSingleton(new DriverRepository(database.DataSource, outbox));
        builder.Services.AddSingleton(faults);
        builder.Services.AddOptions<LocationIngressOptions>();
        builder.Services.AddOptions<DriverBrokerOptions>();
        builder.Services.AddSingleton<LocationAcceptor>();
        builder.Services.AddSingleton<DriverEventPublisher>();

        var app = builder.Build();
        DriverEndpoints.Map(app);
        await app.StartAsync(ct);

        var address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses.Single();
        return new DriverAuthorityHost(app, address.TrimEnd('/'));
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _app.StopAsync(stop.Token);
        }
        catch
        {
            // Test cleanup.
        }

        await _app.DisposeAsync();
    }
}
