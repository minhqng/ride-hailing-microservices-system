// Both legal winning schedules for GoOffline vs Reserve
// on REAL PostgreSQL. A DB row lock cannot be told which contender to grant
// first, so each schedule is forced after a common barrier: one contender
// commits, then the other is released with the SAME starting version. This
// proves both legal finals and the required Outbox consequence without a
// timing sleep.
using System.Diagnostics;
using Npgsql;
using RideHailing.DriverService.Application.Messaging;
using RideHailing.DriverService.Domain.Drivers;
using RideHailing.DriverService.Infrastructure.Persistence;
using Xunit.Abstractions;

namespace RideHailing.DriverService.IntegrationTests;

[Collection(DriverPostgresCollection.Name)]
public sealed class DriverCon02DeterministicTests
{
    private readonly DriverPostgresDatabase _db;
    private readonly ITestOutputHelper _output;

    public DriverCon02DeterministicTests(DriverPostgresDatabase db, ITestOutputHelper output)
    {
        _db = db;
        _output = output;
    }

    private DriverRepository Repo() => _db.CreateRepository();

    private async Task<long> EventCountAsync(Guid driver, string messageType)
    {
        await using var conn = await _db.DataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("""
            SELECT count(*) FROM outbox_messages
            WHERE aggregate_id = @id AND message_type = @type;
            """, conn);
        cmd.Parameters.AddWithValue("id", driver.ToString("N"));
        cmd.Parameters.AddWithValue("type", messageType);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<(Guid Id, int Version)> SeedAvailableAsync(DriverRepository repo)
    {
        var driver = Driver.Create(Guid.NewGuid(), Guid.NewGuid());
        driver.GoOnline(driver.Version);
        Assert.True(await repo.TryInsertAsync(driver));
        return (driver.DriverId, driver.Version);
    }

    [Fact]
    public async Task OfflineWins_ThenReserveLoses_WithOnlyOfflineIntent()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = deadline.Token;
        var repo = Repo();
        var (driverId, version) = await SeedAvailableAsync(repo);
        var trip = Guid.NewGuid();
        var reservation = Guid.NewGuid();
        using var barrier = new Barrier(2);
        var reserveGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var elapsed = Stopwatch.StartNew();

        var offlineTask = Task.Run(async () =>
        {
            Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(30)));
            return await repo.TryGoOfflineAsync(driverId, version, ct);
        }, ct);
        var reserveTask = Task.Run(async () =>
        {
            Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(30)));
            await reserveGate.Task.WaitAsync(ct);
            return await repo.TryReserveAsync(driverId, trip, reservation, version, null, ct);
        }, ct);

        DriverMutationResult offline;
        ReserveResult reserve;
        try
        {
            offline = await offlineTask;
            reserveGate.TrySetResult();
            reserve = await reserveTask;
        }
        finally
        {
            reserveGate.TrySetResult();
        }

        Assert.Equal(DriverMutationOutcome.Applied, offline.Outcome);
        Assert.Equal(ReserveOutcome.Conflict, reserve.Outcome);
        Assert.Equal(DriverErrors.StaleVersion, reserve.ConflictCode);

        var final = (await repo.LoadAsync(driverId, ct))!;
        Assert.Equal(DriverStatus.Offline, final.Status);
        Assert.Null(final.ReservedTripId);
        Assert.Null(final.ReservationId);
        Assert.Equal(version + 1, final.Version);
        Assert.Equal(1, await EventCountAsync(driverId, DriverEventTypes.Offline));
        Assert.Equal(0, await EventCountAsync(driverId, DriverEventTypes.Reserved));
        _output.WriteLine(
            $"CON-02 schedule=offline-wins barrier=reached driver={driverId} " +
            $"lostReservation={reservation} finalVersion={final.Version} " +
            $"offlineIntents=1 reservedIntents=0 elapsedMs={elapsed.ElapsedMilliseconds}");
    }

    [Fact]
    public async Task ReserveWins_ThenOfflineLoses_WithOnlyReservedIntent()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = deadline.Token;
        var repo = Repo();
        var (driverId, version) = await SeedAvailableAsync(repo);
        var trip = Guid.NewGuid();
        var reservation = Guid.NewGuid();
        using var barrier = new Barrier(2);
        var offlineGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var elapsed = Stopwatch.StartNew();

        var offlineTask = Task.Run(async () =>
        {
            Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(30)));
            await offlineGate.Task.WaitAsync(ct);
            return await repo.TryGoOfflineAsync(driverId, version, ct);
        }, ct);
        var reserveTask = Task.Run(async () =>
        {
            Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(30)));
            return await repo.TryReserveAsync(driverId, trip, reservation, version, null, ct);
        }, ct);

        ReserveResult reserve;
        DriverMutationResult offline;
        try
        {
            reserve = await reserveTask;
            offlineGate.TrySetResult();
            offline = await offlineTask;
        }
        finally
        {
            offlineGate.TrySetResult();
        }

        Assert.Equal(ReserveOutcome.Won, reserve.Outcome);
        Assert.Equal(DriverMutationOutcome.Conflict, offline.Outcome);
        Assert.Equal(DriverErrors.StaleVersion, offline.ConflictCode);

        var final = (await repo.LoadAsync(driverId, ct))!;
        Assert.Equal(DriverStatus.Reserved, final.Status);
        Assert.Equal(trip, final.ReservedTripId);
        Assert.Equal(reservation, final.ReservationId);
        Assert.Equal(version + 1, final.Version);
        Assert.Equal(1, await EventCountAsync(driverId, DriverEventTypes.Reserved));
        Assert.Equal(0, await EventCountAsync(driverId, DriverEventTypes.Offline));
        _output.WriteLine(
            $"CON-02 schedule=reserve-wins barrier=reached driver={driverId} " +
            $"reservation={reservation} finalVersion={final.Version} " +
            $"reservedIntents=1 offlineIntents=0 elapsedMs={elapsed.ElapsedMilliseconds}");
    }
}
