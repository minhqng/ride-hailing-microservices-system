// Scenario harness: real Driver HTTP authority, real Matching ledger
// orchestration and real Trip handlers against disposable databases.
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using RideHailing.DriverService.Api;
using RideHailing.DriverService.Application.Location;
using RideHailing.DriverService.Application.Messaging;
using RideHailing.DriverService.Domain.Drivers;
using RideHailing.DriverService.Infrastructure.Faults;
using RideHailing.DriverService.Infrastructure.Persistence;
using RideHailing.MatchingService.Application.Reserve;
using RideHailing.MatchingService.Infrastructure.DriverClient;
using RideHailing.MatchingService.Infrastructure.Persistence;
using RideHailing.Messaging.Outbox;
using RideHailing.TripService.Application.Trips.CreateRide;
using RideHailing.TripService.Application.Trips.Lifecycle;
using RideHailing.TripService.Domain.Trips;
using RideHailing.TripService.Infrastructure.Persistence;

namespace RideHailing.InvariantAudit.Tests;

public sealed class ScenarioClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public DateTimeOffset Advance(TimeSpan delta)
    {
        _now += delta;
        return _now;
    }
}

public sealed class AuditScenario : IAsyncDisposable
{
    private readonly AuditDatabaseSet _databases;
    private readonly WebApplication _driverHost;
    private readonly HttpClient _http;
    private readonly NpgsqlDataSource _driverDataSource;
    private readonly NpgsqlDataSource _matchingDataSource;
    private readonly DriverRepository _drivers;
    private readonly MatchingAttemptRepository _attempts;
    private readonly MatchingTerminalizer _terminalizer;
    private readonly MatchingReserveOrchestrator _orchestrator;

    public ScenarioClock Clock { get; } = new(DateTimeOffset.UtcNow.AddMinutes(-5));
    public AuditDatabaseSet Databases => _databases;
    public DriverRepository Drivers => _drivers;

    private AuditScenario(AuditDatabaseSet databases)
    {
        _databases = databases;
        _driverDataSource = NpgsqlDataSource.Create(databases.DriverConnectionString);
        var driverOutbox = new NpgsqlOutboxStore(_driverDataSource);
        _drivers = new DriverRepository(_driverDataSource, driverOutbox);

        _matchingDataSource = NpgsqlDataSource.Create(databases.MatchingConnectionString);
        var matchingOutbox = new NpgsqlOutboxStore(_matchingDataSource);
        _attempts = new MatchingAttemptRepository(databases.MatchingConnectionString);
        _terminalizer = new MatchingTerminalizer(_matchingDataSource, matchingOutbox);

        _driverHost = BuildDriverHost(_driverDataSource, _drivers);
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _orchestrator = new MatchingReserveOrchestrator(
            _attempts, new HttpDriverClient(_http), _terminalizer);
    }

    public static async Task<AuditScenario> CreateAsync(
        AuditDatabaseSet databases, CancellationToken ct = default)
    {
        var scenario = new AuditScenario(databases);
        await scenario._driverHost.StartAsync(ct);
        var address = scenario._driverHost.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        scenario._http.BaseAddress = new Uri(address + "/");
        return scenario;
    }

    private static WebApplication BuildDriverHost(
        NpgsqlDataSource dataSource, DriverRepository repository)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var outbox = new NpgsqlOutboxStore(dataSource);
        builder.Services.AddSingleton(dataSource);
        builder.Services.AddSingleton(outbox);
        builder.Services.AddSingleton(repository);
        builder.Services.AddSingleton(DriverFaultPoint.Disabled());
        builder.Services.AddOptions<LocationIngressOptions>();
        builder.Services.AddOptions<DriverBrokerOptions>();
        builder.Services.AddSingleton<LocationAcceptor>();
        builder.Services.AddSingleton<DriverEventPublisher>();
        var app = builder.Build();
        DriverEndpoints.Map(app);
        return app;
    }

    public async Task<Driver> SeedAvailableDriverAsync(CancellationToken ct = default)
    {
        var driver = Driver.Create(Guid.NewGuid(), Guid.NewGuid());
        driver.GoOnline(driver.Version);
        Assert.True(await _drivers.TryInsertAsync(driver, ct));
        return driver;
    }

    public async Task<Driver> SeedOfflineDriverAsync(CancellationToken ct = default)
    {
        var driver = Driver.Create(Guid.NewGuid(), Guid.NewGuid());
        Assert.True(await _drivers.TryInsertAsync(driver, ct));
        return driver;
    }

    public Task<RoundReserveResult> ReserveAsync(
        Guid tripId, int round, Guid[] candidates, CancellationToken ct = default) =>
        _orchestrator.ExecuteRoundAsync(tripId, round, candidates, Guid.NewGuid().ToString("D"), ct);

    public async Task<Guid> CreateTripAsync(Guid rider, string key, CancellationToken ct = default)
    {
        await using var db = CreateTripDbContext();
        var result = await new CreateRideHandler(db, Clock).HandleAsync(new CreateRideCommand(
            rider,
            new TripLocation(10.762622, 106.660172),
            new TripLocation(10.776889, 106.700806),
            key), ct);
        Assert.True(result.IsSuccess, result.ErrorMessage);
        return result.TripId;
    }

    public async Task StartMatchingAsync(Guid tripId, Guid rider, CancellationToken ct = default)
    {
        await using var db = CreateTripDbContext();
        var result = await new StartMatchingHandler(db, Clock, NullLogger<StartMatchingHandler>.Instance)
            .HandleAsync(tripId, rider, ct);
        Assert.True(result.IsSuccess, result.Message);
    }

    public async Task RecordReservationAsync(
        Guid tripId, Guid driverId, Guid reservationId, int round, CancellationToken ct = default)
    {
        await using var db = CreateTripDbContext();
        var result = await new RecordDriverReservationHandler(
                db, Clock, NullLogger<RecordDriverReservationHandler>.Instance)
            .HandleAsync(tripId, driverId, reservationId, round, ct);
        Assert.True(result.IsSuccess, result.Message);
    }

    public async Task MarkMatchingFailedAsync(Guid tripId, int round, CancellationToken ct = default)
    {
        await using var db = CreateTripDbContext();
        var result = await new MarkMatchingFailedHandler(
                db, Clock, NullLogger<MarkMatchingFailedHandler>.Instance)
            .HandleAsync(tripId, round, ct);
        Assert.True(result.IsSuccess, result.Message);
    }

    public async Task CancelAsync(Guid tripId, Guid rider, CancellationToken ct = default)
    {
        await using var db = CreateTripDbContext();
        var result = await new CancelRideHandler(db, Clock, NullLogger<CancelRideHandler>.Instance)
            .HandleAsync(tripId, rider, ct);
        Assert.True(result.IsSuccess, result.Message);
    }

    public async Task<List<(string Key, string Kind)>> LoadOpenCleanupAsync(
        Guid tripId, CancellationToken ct = default)
    {
        await using var connection = new NpgsqlConnection(_databases.TripConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand("""
            SELECT key, kind FROM trip_cleanup_intents
            WHERE trip_id = @trip AND completed_at IS NULL ORDER BY key;
            """, connection);
        command.Parameters.AddWithValue("trip", tripId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<(string, string)>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add((reader.GetString(0), reader.GetString(1)));
        }

        return rows;
    }

    public async Task ConfirmCleanupAsync(
        Guid tripId, string key, CleanupAuthorityResult result, CancellationToken ct = default)
    {
        await using var db = CreateTripDbContext();
        var outcome = await new ConfirmTripCleanupHandler(db, Clock)
            .HandleAsync(tripId, key, result, ct);
        Assert.True(outcome.IsSuccess, outcome.Message);
    }

    public Task<DriverLifecycleResult> StartDriverTripAsync(
        Guid driverId, Guid tripId, Guid reservationId, CancellationToken ct = default) =>
        _drivers.TryMarkTripStartedAsync(driverId, tripId, reservationId, ct);

    public Task<DriverLifecycleResult> CompleteDriverTripAsync(
        Guid driverId, Guid tripId, Guid reservationId, CancellationToken ct = default) =>
        _drivers.TryMarkTripCompletedAsync(driverId, tripId, reservationId, ct);

    public Task<DriverMutationResult> ReleaseDriverAsync(
        Guid driverId, Guid tripId, Guid reservationId, CancellationToken ct = default) =>
        _drivers.TryReleaseAsync(driverId, tripId, reservationId, ct);

    private TripDbContext CreateTripDbContext() =>
        new(new DbContextOptionsBuilder<TripDbContext>()
            .UseNpgsql(_databases.TripConnectionString)
            .Options);

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        try
        {
            using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _driverHost.StopAsync(stopCts.Token);
        }
        finally
        {
            await _driverHost.DisposeAsync();
            await _matchingDataSource.DisposeAsync();
            await _driverDataSource.DisposeAsync();
        }
    }
}
