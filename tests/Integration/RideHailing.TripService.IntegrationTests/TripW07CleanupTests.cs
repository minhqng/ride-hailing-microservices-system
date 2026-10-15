using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using RideHailing.TripService.Application.Trips.Lifecycle;
using RideHailing.TripService.Domain.Trips;
using RideHailing.TripService.Infrastructure.Persistence;

namespace RideHailing.TripService.IntegrationTests;

[Collection(TripPostgresCollection.Name)]
public sealed class TripW07CleanupTests(TripPostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 0, 0, 0, TimeSpan.Zero);
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    private TripDbContext Db(params IInterceptor[] interceptors) => new(
        new DbContextOptionsBuilder<TripDbContext>().UseNpgsql(fixture.ConnectionString)
            .AddInterceptors(interceptors).Options);

    private async Task<Trip> Seed()
    {
        await using var db = Db();
        await db.Database.MigrateAsync();
        var trip = Trip.Create(Guid.NewGuid(), Guid.NewGuid(), new(10.7, 106.6), new(10.8, 106.7),
            new("VND", 50000m, "w7", Now), Now);
        trip.StartMatching(Now);
        db.Trips.Add(trip);
        await db.SaveChangesAsync();
        return trip;
    }

    private async Task<TripLifecycleResult> Cancel(Trip trip)
    {
        await using var db = Db();
        return await new CancelRideHandler(db, new Clock(), NullLogger<CancelRideHandler>.Instance)
            .HandleAsync(trip.TripId, trip.RiderId);
    }

    private async Task<TripLifecycleResult> Reserve(Trip trip, Guid driver, Guid reservation)
    {
        await using var db = Db();
        return await new RecordDriverReservationHandler(db, new Clock(), NullLogger<RecordDriverReservationHandler>.Instance)
            .HandleAsync(trip.TripId, driver, reservation, 1);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancelVsReservation_BothSchedules_PreserveTerminalAndExactCleanup(bool cancelFirst)
    {
        var trip = await Seed();
        var driver = Guid.NewGuid();
        var reservation = Guid.NewGuid();
        if (cancelFirst) Assert.True((await Cancel(trip)).IsSuccess);
        Assert.True((await Reserve(trip, driver, reservation)).IsSuccess);
        if (!cancelFirst) Assert.True((await Cancel(trip)).IsSuccess);
        Assert.True((await Cancel(trip)).IsReplay);
        Assert.True((await Reserve(trip, driver, reservation)).IsReplay);

        await using var verify = Db();
        var final = await verify.Trips.AsNoTracking().SingleAsync(t => t.TripId == trip.TripId);
        Assert.Equal(TripState.Cancelled, final.State);
        Assert.Equal(cancelFirst, final.Assignment is null);
        var cleanup = Assert.Single(final.CleanupIntents, i => i.Kind == TripCleanupKind.ReleaseReservation);
        Assert.Equal(trip.TripId, cleanup.TripId);
        Assert.Equal(driver, cleanup.DriverId);
        Assert.Equal(reservation, cleanup.ReservationId);
        Assert.Null(cleanup.CompletedAt);
    }

    [Fact]
    public async Task StaleCancellation_RollsBackCleanup_WhenReservationWins()
    {
        var trip = await Seed();
        await using var loser = Db();
        var cancelling = await loser.Trips.SingleAsync(t => t.TripId == trip.TripId);
        Assert.True(cancelling.Cancel(Now).IsSuccess);
        var driver = Guid.NewGuid();
        var reservation = Guid.NewGuid();
        await Reserve(trip, driver, reservation);
        await Assert.ThrowsAsync<TripConcurrencyConflictException>(() => loser.SaveChangesAsync());
        await using (var verify = Db())
        {
            var winner = await verify.Trips.AsNoTracking().SingleAsync(t => t.TripId == trip.TripId);
            Assert.Equal(TripState.DriverReserved, winner.State);
            Assert.Empty(winner.CleanupIntents);
        }
        Assert.True((await Cancel(trip)).IsSuccess);
        await using var reloaded = Db();
        var final = await reloaded.Trips.AsNoTracking().SingleAsync(t => t.TripId == trip.TripId);
        Assert.Equal(reservation, Assert.Single(final.CleanupIntents,
            i => i.Kind == TripCleanupKind.ReleaseReservation).ReservationId);
    }

    [Fact]
    public async Task StaleReservation_CannotReactivateCancellation_RetryRecordsCleanup()
    {
        var trip = await Seed();
        var driver = Guid.NewGuid();
        var reservation = Guid.NewGuid();
        await using var loser = Db();
        var reserving = await loser.Trips.SingleAsync(t => t.TripId == trip.TripId);
        reserving.ApplyDriverReserved(driver, reservation, 1, Now);
        await Cancel(trip);
        await Assert.ThrowsAsync<TripConcurrencyConflictException>(() => loser.SaveChangesAsync());
        await Reserve(trip, driver, reservation);
        await using var verify = Db();
        var final = await verify.Trips.AsNoTracking().SingleAsync(t => t.TripId == trip.TripId);
        Assert.Equal(TripState.Cancelled, final.State);
        Assert.Null(final.Assignment);
        Assert.Single(final.CleanupIntents, i => i.ReservationId == reservation);
    }

    [Fact]
    public async Task LostCommitResponse_RetryFindsDurableCancellationAndObligations()
    {
        var trip = await Seed();
        await Reserve(trip, Guid.NewGuid(), Guid.NewGuid());
        await using (var db = Db(new LostResponse()))
        {
            var handler = new CancelRideHandler(db, new Clock(), NullLogger<CancelRideHandler>.Instance);
            await Assert.ThrowsAsync<IOException>(() => handler.HandleAsync(trip.TripId, trip.RiderId));
        }
        Assert.True((await Cancel(trip)).IsReplay);
        await using var verify = Db();
        var final = await verify.Trips.AsNoTracking().SingleAsync(t => t.TripId == trip.TripId);
        Assert.Equal(TripState.Cancelled, final.State);
        Assert.Equal(3, final.CleanupIntents.Count);
    }

    [Fact]
    public async Task UnknownCleanupStaysPending_ConfirmedExactIntentCompletesAfterReload()
    {
        var trip = await Seed();
        var reservation = Guid.NewGuid();
        await Reserve(trip, Guid.NewGuid(), reservation);
        await Cancel(trip);
        var key = $"release:{reservation:D}";
        await using (var db = Db())
        {
            var handler = new ConfirmTripCleanupHandler(db, new Clock());
            Assert.False((await handler.HandleAsync(trip.TripId, key, CleanupAuthorityResult.Unknown)).IsSuccess);
            Assert.False((await handler.HandleAsync(trip.TripId, "release:" + Guid.NewGuid(), CleanupAuthorityResult.Released)).IsSuccess);
            Assert.True((await handler.HandleAsync(trip.TripId, key, CleanupAuthorityResult.Released)).IsSuccess);
        }
        await using var verify = Db();
        var final = await verify.Trips.AsNoTracking().SingleAsync(t => t.TripId == trip.TripId);
        Assert.Equal(TripState.Cancelled, final.State);
        Assert.Equal(Now, final.CleanupIntents.Single(i => i.Key == key).CompletedAt);
        Assert.All(final.CleanupIntents.Where(i => i.Key != key), i => Assert.Null(i.CompletedAt));
    }

    [Fact]
    public async Task TwoLateReservations_ConflictThenReload_PreserveBothCleanupObligations()
    {
        var trip = await Seed();
        await Cancel(trip);
        await using var first = Db();
        await using var second = Db();
        var a = await first.Trips.SingleAsync(t => t.TripId == trip.TripId);
        var b = await second.Trips.SingleAsync(t => t.TripId == trip.TripId);
        var driver = Guid.NewGuid();
        var resA = Guid.NewGuid();
        var resB = Guid.NewGuid();
        a.RecordCancelledReservation(driver, resA, 1, Now);
        b.RecordCancelledReservation(driver, resB, 1, Now);
        await first.SaveChangesAsync();
        await Assert.ThrowsAsync<TripConcurrencyConflictException>(() => second.SaveChangesAsync());
        await Reserve(trip, driver, resB);
        await using var verify = Db();
        var final = await verify.Trips.AsNoTracking().SingleAsync(t => t.TripId == trip.TripId);
        Assert.Equal(TripState.Cancelled, final.State);
        Assert.Null(final.Assignment);
        Assert.Equal(2, final.CleanupIntents.Count(i => i.Kind == TripCleanupKind.ReleaseReservation));
    }

    [Fact]
    public async Task UpgradeFromW6_BackfillsPendingCleanup_WithoutChangingTripVersion()
    {
        var name = "w7_probe_" + Guid.NewGuid().ToString("N");
        await using var admin = new NpgsqlConnection(fixture.ConnectionString);
        await admin.OpenAsync();
        await using (var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin))
            await command.ExecuteNonQueryAsync();
        var cs = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = name, Pooling = false }.ToString();
        try
        {
            await using var db = new TripDbContext(new DbContextOptionsBuilder<TripDbContext>().UseNpgsql(cs).Options);
            await db.Database.MigrateAsync();
            var trip = Trip.Create(Guid.NewGuid(), Guid.NewGuid(), new(10.7, 106.6), new(10.8, 106.7),
                new("VND", 50000m, "w7", Now), Now);
            trip.StartMatching(Now);
            var reservation = Guid.NewGuid();
            trip.ApplyDriverReserved(Guid.NewGuid(), reservation, 1, Now);
            trip.Cancel(Now);
            db.Trips.Add(trip);
            await db.SaveChangesAsync();
            var version = trip.Version;
            db.ChangeTracker.Clear();
            // Remove only the new W7 ledger in this disposable database. The
            // retained cancelled row is now a W6-shaped upgrade input.
            await db.GetService<IMigrator>().MigrateAsync("20261005155025_W05_TripLifecycle");
            await db.Database.MigrateAsync();
            var final = await db.Trips.AsNoTracking().SingleAsync(t => t.TripId == trip.TripId);
            Assert.Equal(version, final.Version);
            Assert.Equal(TripState.Cancelled, final.State);
            Assert.Equal(3, final.CleanupIntents.Count);
            Assert.Equal(reservation, final.CleanupIntents.Single(i => i.Kind == TripCleanupKind.ReleaseReservation).ReservationId);
            Assert.All(final.CleanupIntents, i => Assert.Null(i.CompletedAt));
            Assert.False(db.Database.HasPendingModelChanges());
        }
        finally
        {
            await using var command = new NpgsqlCommand($"DROP DATABASE \"{name}\" WITH (FORCE)", admin);
            await command.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task LatePaymentAuthorization_PersistsReconciliation_NoReadyState()
    {
        var trip = await Seed();
        await Cancel(trip);
        await using (var db = Db())
        {
            var handler = new MarkPaymentAuthorizedHandler(db, new Clock(), NullLogger<MarkPaymentAuthorizedHandler>.Instance);
            Assert.True((await handler.HandleAsync(trip.TripId)).IsSuccess);
            Assert.True((await handler.HandleAsync(trip.TripId)).IsReplay);
        }
        await using var verify = Db();
        var final = await verify.Trips.AsNoTracking().SingleAsync(t => t.TripId == trip.TripId);
        Assert.Equal(TripState.Cancelled, final.State);
        Assert.Null(Assert.Single(final.CleanupIntents, i => i.Kind == TripCleanupKind.ReconcilePayment).CompletedAt);
    }

    private sealed class LostResponse : SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData,
            int result, CancellationToken cancellationToken = default) => throw new IOException("Lost commit response (test).");
    }
}
