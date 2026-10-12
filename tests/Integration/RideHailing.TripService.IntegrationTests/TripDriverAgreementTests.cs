// A-W06-07 Driver–Trip agreement fixture (cross-member, test-only).
//
// Production services never query each other's databases; this fixture is the
// explicitly sanctioned test seam that inspects both owned databases to prove
// DSI-030 agreement: after the Driver authority commits reservation R, the
// Trip may record reference R — and only R — as its current assignment.
//
// Stable identities (tripId, driverId, reservationId, matchingRound) flow
// from the real Driver reserve path into the real Trip intake; no transport
// behavior is invented (W08 owns the broker wiring).
extern alias DriverSvc;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using DriverDomain = DriverSvc::RideHailing.DriverService.Domain.Drivers;
using DriverPersistence = DriverSvc::RideHailing.DriverService.Infrastructure.Persistence;
using RideHailing.TripService.Application.Trips.CreateRide;
using RideHailing.TripService.Application.Trips.Lifecycle;
using RideHailing.TripService.Domain.Trips;
using RideHailing.TripService.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace RideHailing.TripService.IntegrationTests;

[Collection(TripPostgresCollection.Name)]
public sealed class TripDriverAgreementTests : IAsyncLifetime
{
    private readonly TripPostgresFixture _tripFixture;
    private PostgreSqlContainer? _driverContainer;
    private string _driverConnectionString = "";

    public TripDriverAgreementTests(TripPostgresFixture tripFixture)
    {
        _tripFixture = tripFixture;
    }

    public async Task InitializeAsync()
    {
        _driverContainer = new PostgreSqlBuilder("postgres:18.6")
            .WithDatabase("driver_db")
            .WithUsername("driver_app")
            .WithPassword("driver_dev_pw")
            .WithAutoRemove(true)
            .Build();
        await _driverContainer.StartAsync();
        _driverConnectionString = _driverContainer.GetConnectionString();
        await ApplyDriverMigrationsAsync(_driverConnectionString);
    }

    public async Task DisposeAsync()
    {
        if (_driverContainer is not null)
        {
            await _driverContainer.DisposeAsync();
        }
    }

    private static async Task ApplyDriverMigrationsAsync(string connectionString)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Infrastructure", "Persistence", "Migrations");
        var files = Directory.Exists(dir)
            ? Directory.GetFiles(dir, "*.sql").OrderBy(Path.GetFileName, StringComparer.Ordinal).ToList()
            : new List<string>();
        if (!files.Any(f => Path.GetFileName(f).StartsWith("001_", StringComparison.Ordinal)))
        {
            throw new DirectoryNotFoundException(
                "Driver migration scripts not found in test output. Tried: " + dir);
        }

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        foreach (var file in files)
        {
            await using var cmd = new NpgsqlCommand(await File.ReadAllTextAsync(file), conn);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private readonly DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private static readonly DateTimeOffset CreatedAt = new(2026, 9, 26, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ActionAt = new(2026, 10, 12, 0, 0, 0, TimeSpan.Zero);

    private DbContextOptions<TripDbContext> TripOptions() =>
        new DbContextOptionsBuilder<TripDbContext>()
            .UseNpgsql(_tripFixture.ConnectionString).Options;

    private static async Task<Guid> SeedAvailableDriverAsync(DriverPersistence.DriverRepository repo)
    {
        var driver = DriverDomain.Driver.Create(Guid.NewGuid(), Guid.NewGuid());
        driver.GoOnline();
        Assert.True(await repo.TryInsertAsync(driver));
        return driver.DriverId;
    }

    private async Task<Guid> CreateMatchingTripAsync(Guid rider, string key)
    {
        await using var setup = new TripDbContext(TripOptions());
        await setup.Database.MigrateAsync();
        await using var db = new TripDbContext(TripOptions());
        var created = await new CreateRideHandler(db, new FixedTimeProvider(CreatedAt)).HandleAsync(
            new CreateRideCommand(
                rider,
                new TripLocation(10.762622, 106.660172),
                new TripLocation(10.776889, 106.700806),
                key));
        Assert.True(created.IsSuccess);
        await using var matching = new TripDbContext(TripOptions());
        Assert.True((await new StartMatchingHandler(matching, new FixedTimeProvider(CreatedAt), NullLogger<StartMatchingHandler>.Instance)
            .HandleAsync(created.TripId, rider)).IsSuccess);
        return created.TripId;
    }

    [Fact]
    public async Task Winner_Reference_Agrees_Across_Services()
    {
        var driverRepo = new DriverPersistence.DriverRepository(_driverConnectionString);
        var driverId = await SeedAvailableDriverAsync(driverRepo);
        var start = await driverRepo.LoadAsync(driverId);
        Assert.NotNull(start);
        var reservationId = Guid.NewGuid();
        var rider = Guid.NewGuid();
        var tripId = await CreateMatchingTripAsync(rider, "agree-winner-" + Guid.NewGuid().ToString("N"));

        var reserve = await driverRepo.TryReserveAsync(driverId, tripId, reservationId, start.Version);
        Assert.Equal(DriverPersistence.ReserveOutcome.Won, reserve.Outcome);

        TripLifecycleResult recorded;
        await using (var db = new TripDbContext(TripOptions()))
        {
            recorded = await new RecordDriverReservationHandler(db, new FixedTimeProvider(ActionAt), NullLogger<RecordDriverReservationHandler>.Instance)
                .HandleAsync(tripId, driverId, reservationId, 1);
        }

        Assert.True(recorded.IsSuccess);

        await using var tripVerify = new TripDbContext(TripOptions());
        var trip = await tripVerify.Trips.AsNoTracking().SingleAsync(t => t.TripId == tripId);
        var driver = await driverRepo.LoadAsync(driverId);
        Assert.NotNull(driver);
        Assert.Equal(TripState.DriverReserved, trip.State);
        Assert.NotNull(trip.Assignment);
        Assert.Equal(driverId, trip.Assignment.DriverId);
        Assert.Equal(reservationId, trip.Assignment.ReservationId);
        Assert.Equal(1, trip.Assignment.MatchingRound);
        Assert.Equal(tripId, driver.ReservedTripId);
        Assert.Equal(reservationId, driver.ReservationId);
    }

    [Fact]
    public async Task Loser_Reference_Cannot_Overwrite_Agreement()
    {
        var driverRepo = new DriverPersistence.DriverRepository(_driverConnectionString);
        var driverId = await SeedAvailableDriverAsync(driverRepo);
        var start = await driverRepo.LoadAsync(driverId);
        Assert.NotNull(start);
        var winnerReservation = Guid.NewGuid();
        var loserReservation = Guid.NewGuid();
        var rider = Guid.NewGuid();
        var tripId = await CreateMatchingTripAsync(rider, "agree-loser-" + Guid.NewGuid().ToString("N"));

        Assert.Equal(DriverPersistence.ReserveOutcome.Won,
            (await driverRepo.TryReserveAsync(driverId, tripId, winnerReservation, start.Version)).Outcome);
        Assert.Equal(DriverPersistence.ReserveOutcome.Conflict,
            (await driverRepo.TryReserveAsync(driverId, Guid.NewGuid(), loserReservation, start.Version + 1)).Outcome);

        await using (var db = new TripDbContext(TripOptions()))
        {
            Assert.True((await new RecordDriverReservationHandler(db, new FixedTimeProvider(ActionAt), NullLogger<RecordDriverReservationHandler>.Instance)
                .HandleAsync(tripId, driverId, winnerReservation, 1)).IsSuccess);
        }

        TripLifecycleResult loser;
        await using (var db = new TripDbContext(TripOptions()))
        {
            loser = await new RecordDriverReservationHandler(db, new FixedTimeProvider(ActionAt), NullLogger<RecordDriverReservationHandler>.Instance)
                .HandleAsync(tripId, driverId, loserReservation, 1);
        }

        Assert.False(loser.IsSuccess);
        Assert.Equal(TripErrorCode.AssignmentIdentityMismatch, loser.ErrorCode);

        await using var tripVerify = new TripDbContext(TripOptions());
        var trip = await tripVerify.Trips.AsNoTracking().SingleAsync(t => t.TripId == tripId);
        var driver = await driverRepo.LoadAsync(driverId);
        Assert.NotNull(driver);
        Assert.Equal(winnerReservation, trip.Assignment!.ReservationId);
        Assert.Equal(winnerReservation, driver.ReservationId);
        Assert.Equal(tripId, driver.ReservedTripId);
    }

    [Fact]
    public async Task Replay_Keeps_Agreement_Without_Second_Effect()
    {
        var driverRepo = new DriverPersistence.DriverRepository(_driverConnectionString);
        var driverId = await SeedAvailableDriverAsync(driverRepo);
        var start = await driverRepo.LoadAsync(driverId);
        Assert.NotNull(start);
        var reservationId = Guid.NewGuid();
        var rider = Guid.NewGuid();
        var tripId = await CreateMatchingTripAsync(rider, "agree-replay-" + Guid.NewGuid().ToString("N"));

        Assert.Equal(DriverPersistence.ReserveOutcome.Won,
            (await driverRepo.TryReserveAsync(driverId, tripId, reservationId, start.Version)).Outcome);
        var replay = await driverRepo.TryReserveAsync(driverId, tripId, reservationId, start.Version);
        Assert.Equal(DriverPersistence.ReserveOutcome.Replayed, replay.Outcome);

        await using (var db = new TripDbContext(TripOptions()))
        {
            Assert.True((await new RecordDriverReservationHandler(db, new FixedTimeProvider(ActionAt), NullLogger<RecordDriverReservationHandler>.Instance)
                .HandleAsync(tripId, driverId, reservationId, 1)).IsSuccess);
        }

        int versionAfterFirst;
        await using (var db = new TripDbContext(TripOptions()))
        {
            var recorded = await new RecordDriverReservationHandler(db, new FixedTimeProvider(ActionAt), NullLogger<RecordDriverReservationHandler>.Instance)
                .HandleAsync(tripId, driverId, reservationId, 1);
            Assert.True(recorded.IsSuccess);
            versionAfterFirst = (await db.Trips.AsNoTracking().SingleAsync(t => t.TripId == tripId)).Version;
        }

        await using var tripVerify = new TripDbContext(TripOptions());
        var trip = await tripVerify.Trips.AsNoTracking().SingleAsync(t => t.TripId == tripId);
        var driver = await driverRepo.LoadAsync(driverId);
        Assert.NotNull(driver);
        Assert.Equal(versionAfterFirst, trip.Version);
        Assert.Equal(reservationId, trip.Assignment!.ReservationId);
        Assert.Equal(reservationId, driver.ReservationId);
        Assert.Equal(tripId, driver.ReservedTripId);
        Assert.Equal(start.Version + 1, driver.Version);
    }
}
