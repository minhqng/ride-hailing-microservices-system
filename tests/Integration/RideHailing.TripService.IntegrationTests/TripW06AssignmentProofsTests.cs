// A-W06 assignment uniqueness and stale-result proofs on real PostgreSQL.
//
// Covers the Trip-side DOM-I3 defense that W5 left without an executable
// competing-assignment race: two different authoritative-shaped reservations
// for the same unassigned Trip generation cannot both persist, and
// MatchingFailed/terminal inputs cannot regress an assigned or terminal Trip.
// Every test asserts the reloaded authoritative row, never handler results
// alone, with the full no-mutation footprint where relevant.
//
// Driver identities below are authoritative-SHAPED fixtures, not live Driver
// authority (B-W06 unavailable); the property proven here is Trip fencing
// and OCC, not Driver exclusivity.

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RideHailing.TripService.Application.Trips.CreateRide;
using RideHailing.TripService.Application.Trips.Lifecycle;
using RideHailing.TripService.Domain.Trips;
using RideHailing.TripService.Infrastructure.Persistence;

namespace RideHailing.TripService.IntegrationTests;

[Collection(TripPostgresCollection.Name)]
public sealed class TripW06AssignmentProofsTests
{
    private readonly TripPostgresFixture _fixture;

    public TripW06AssignmentProofsTests(TripPostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static readonly DateTimeOffset CreatedAt = new(2026, 9, 26, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ActionAt = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    private DbContextOptions<TripDbContext> Options() =>
        new DbContextOptionsBuilder<TripDbContext>()
            .UseNpgsql(_fixture.ConnectionString).Options;

    private async Task MigrateAsync()
    {
        var options = Options();
        await using var setup = new TripDbContext(options);
        await setup.Database.MigrateAsync();
    }

    private async Task<Guid> CreateOwnedTripAsync(Guid rider, string key)
    {
        await using var db = new TripDbContext(Options());
        var handler = new CreateRideHandler(db, new FixedTimeProvider(CreatedAt));
        var result = await handler.HandleAsync(new CreateRideCommand(
            rider,
            new TripLocation(10.762622, 106.660172),
            new TripLocation(10.776889, 106.700806),
            key));
        Assert.True(result.IsSuccess);
        return result.TripId;
    }

    private TripDbContext Db() => new(Options());

    private static FixedTimeProvider Clock() => new(ActionAt);

    [Fact]
    public async Task CompetingReservations_SameGeneration_OneWins_OneConflicts_SingleAssignmentPersists()
    {
        await MigrateAsync();
        var rider = Guid.NewGuid();
        var tripId = await CreateOwnedTripAsync(rider, "w6-competing-" + Guid.NewGuid().ToString("N"));
        var driverA = Guid.NewGuid();
        var reservationA = Guid.NewGuid();
        var driverB = Guid.NewGuid();
        var reservationB = Guid.NewGuid();

        await using (var db = Db())
        {
            Assert.True((await new StartMatchingHandler(db, Clock(), NullLogger<StartMatchingHandler>.Instance)
                .HandleAsync(tripId, rider)).IsSuccess);
        }

        // Both writers read the same pre-assignment version before either saves.
        // Deterministic staleness via independent tracking, no sleeps.
        await using var dbA = Db();
        await using var dbB = Db();
        var tripA = await dbA.Trips.SingleAsync(t => t.TripId == tripId);
        var tripB = await dbB.Trips.SingleAsync(t => t.TripId == tripId);
        Assert.Equal(tripA.Version, tripB.Version);
        Assert.Null(tripA.Assignment);
        Assert.Null(tripB.Assignment);

        Assert.True(tripA.ApplyDriverReserved(driverA, reservationA, 1, ActionAt).IsSuccess);
        Assert.True(tripB.ApplyDriverReserved(driverB, reservationB, 1, ActionAt).IsSuccess);

        await dbA.SaveChangesAsync();
        var conflict = await Assert.ThrowsAsync<TripConcurrencyConflictException>(() => dbB.SaveChangesAsync());
        Assert.Equal(tripId, conflict.TripId);

        // Authoritative final state: exactly one assignment, the winner's.
        await using var verify = Db();
        var authoritative = await verify.Trips.AsNoTracking().SingleAsync(t => t.TripId == tripId);
        Assert.Equal(TripState.DriverReserved, authoritative.State);
        Assert.NotNull(authoritative.Assignment);
        Assert.Equal(driverA, authoritative.Assignment.DriverId);
        Assert.Equal(reservationA, authoritative.Assignment.ReservationId);
        Assert.Equal(1, authoritative.Assignment.MatchingRound);
        Assert.Equal(1, authoritative.CurrentMatchingRound);
        Assert.Equal(2, authoritative.Version);

        // After reload the losing different reservation is a domain conflict,
        // not a second assignment, and mutates nothing.
        TripLifecycleResult loser;
        await using (var db = Db())
        {
            loser = await new RecordDriverReservationHandler(db, Clock(), NullLogger<RecordDriverReservationHandler>.Instance)
                .HandleAsync(tripId, driverB, reservationB, 1);
        }

        Assert.False(loser.IsSuccess);
        Assert.Equal(TripErrorCode.AssignmentIdentityMismatch, loser.ErrorCode);

        await using var verifyAgain = Db();
        var still = await verifyAgain.Trips.AsNoTracking().SingleAsync(t => t.TripId == tripId);
        Assert.Equal(TripState.DriverReserved, still.State);
        Assert.Equal(driverA, still.Assignment!.DriverId);
        Assert.Equal(reservationA, still.Assignment.ReservationId);
        Assert.Equal(2, still.Version);
    }

    [Fact]
    public async Task MatchingFailed_SameRound_AfterReserved_DoesNotRegress()
    {
        await MigrateAsync();
        var rider = Guid.NewGuid();
        var tripId = await CreateOwnedTripAsync(rider, "w6-mf-reserved-" + Guid.NewGuid().ToString("N"));
        var driverId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();

        await using (var db = Db())
        {
            Assert.True((await new StartMatchingHandler(db, Clock(), NullLogger<StartMatchingHandler>.Instance)
                .HandleAsync(tripId, rider)).IsSuccess);
            Assert.True((await new RecordDriverReservationHandler(db, Clock(), NullLogger<RecordDriverReservationHandler>.Instance)
                .HandleAsync(tripId, driverId, reservationId, 1)).IsSuccess);
        }

        await using var beforeDb = Db();
        var before = await beforeDb.Trips.AsNoTracking().SingleAsync(t => t.TripId == tripId);

        TripLifecycleResult failed;
        await using (var db = Db())
        {
            failed = await new MarkMatchingFailedHandler(db, Clock(), NullLogger<MarkMatchingFailedHandler>.Instance)
                .HandleAsync(tripId, 1);
        }

        Assert.False(failed.IsSuccess);

        await using var verify = Db();
        var after = await verify.Trips.AsNoTracking().SingleAsync(t => t.TripId == tripId);
        Assert.Equal(TripState.DriverReserved, after.State);
        Assert.Equal(driverId, after.Assignment!.DriverId);
        Assert.Equal(reservationId, after.Assignment.ReservationId);
        Assert.Equal(1, after.Assignment.MatchingRound);
        Assert.Equal(1, after.CurrentMatchingRound);
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
    }

    [Fact]
    public async Task MatchingFailed_SameRound_AfterAccepted_DoesNotRegress()
    {
        await MigrateAsync();
        var rider = Guid.NewGuid();
        var tripId = await CreateOwnedTripAsync(rider, "w6-mf-accepted-" + Guid.NewGuid().ToString("N"));
        var driverId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();

        await using (var db = Db())
        {
            Assert.True((await new StartMatchingHandler(db, Clock(), NullLogger<StartMatchingHandler>.Instance)
                .HandleAsync(tripId, rider)).IsSuccess);
            Assert.True((await new RecordDriverReservationHandler(db, Clock(), NullLogger<RecordDriverReservationHandler>.Instance)
                .HandleAsync(tripId, driverId, reservationId, 1)).IsSuccess);
            Assert.True((await new RecordDriverAcceptedHandler(db, Clock(), NullLogger<RecordDriverAcceptedHandler>.Instance)
                .HandleAsync(tripId, driverId, reservationId)).IsSuccess);
        }

        await using var beforeDb = Db();
        var before = await beforeDb.Trips.AsNoTracking().SingleAsync(t => t.TripId == tripId);
        Assert.Equal(TripState.DriverAccepted, before.State);

        TripLifecycleResult failed;
        await using (var db = Db())
        {
            failed = await new MarkMatchingFailedHandler(db, Clock(), NullLogger<MarkMatchingFailedHandler>.Instance)
                .HandleAsync(tripId, 1);
        }

        Assert.False(failed.IsSuccess);

        await using var verify = Db();
        var after = await verify.Trips.AsNoTracking().SingleAsync(t => t.TripId == tripId);
        Assert.Equal(TripState.DriverAccepted, after.State);
        Assert.Equal(driverId, after.Assignment!.DriverId);
        Assert.Equal(reservationId, after.Assignment.ReservationId);
        Assert.Equal(1, after.CurrentMatchingRound);
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
    }

    [Fact]
    public async Task ReservationReplay_ViaHandler_PreservesDbRow()
    {
        await MigrateAsync();
        var rider = Guid.NewGuid();
        var tripId = await CreateOwnedTripAsync(rider, "w6-replay-" + Guid.NewGuid().ToString("N"));
        var driverId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();

        await using (var db = Db())
        {
            Assert.True((await new StartMatchingHandler(db, Clock(), NullLogger<StartMatchingHandler>.Instance)
                .HandleAsync(tripId, rider)).IsSuccess);
            Assert.True((await new RecordDriverReservationHandler(db, Clock(), NullLogger<RecordDriverReservationHandler>.Instance)
                .HandleAsync(tripId, driverId, reservationId, 1)).IsSuccess);
        }

        await using var beforeDb = Db();
        var before = await beforeDb.Trips.AsNoTracking().SingleAsync(t => t.TripId == tripId);

        TripLifecycleResult replay;
        await using (var db = Db())
        {
            replay = await new RecordDriverReservationHandler(db, Clock(), NullLogger<RecordDriverReservationHandler>.Instance)
                .HandleAsync(tripId, driverId, reservationId, 1);
        }

        Assert.True(replay.IsSuccess);
        Assert.True(replay.IsReplay);

        await using var verify = Db();
        var after = await verify.Trips.AsNoTracking().SingleAsync(t => t.TripId == tripId);
        Assert.Equal(before.State, after.State);
        Assert.Equal(before.Assignment!.DriverId, after.Assignment!.DriverId);
        Assert.Equal(before.Assignment.ReservationId, after.Assignment.ReservationId);
        Assert.Equal(before.Assignment.MatchingRound, after.Assignment.MatchingRound);
        Assert.Equal(before.CurrentMatchingRound, after.CurrentMatchingRound);
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
        Assert.Equal(before.CreatedAt, after.CreatedAt);
    }

    [Fact]
    public async Task ReservationAfterMatchFailedTerminal_DoesNotReactivate()
    {
        await MigrateAsync();
        var rider = Guid.NewGuid();
        var tripId = await CreateOwnedTripAsync(rider, "w6-terminal-" + Guid.NewGuid().ToString("N"));

        await using (var db = Db())
        {
            Assert.True((await new StartMatchingHandler(db, Clock(), NullLogger<StartMatchingHandler>.Instance)
                .HandleAsync(tripId, rider)).IsSuccess);
            Assert.True((await new MarkMatchingFailedHandler(db, Clock(), NullLogger<MarkMatchingFailedHandler>.Instance)
                .HandleAsync(tripId, 1)).IsSuccess);
        }

        await using var beforeDb = Db();
        var before = await beforeDb.Trips.AsNoTracking().SingleAsync(t => t.TripId == tripId);
        Assert.Equal(TripState.MatchFailed, before.State);

        TripLifecycleResult late;
        var lateDriver = Guid.NewGuid();
        var lateReservation = Guid.NewGuid();
        await using (var db = Db())
        {
            late = await new RecordDriverReservationHandler(db, Clock(), NullLogger<RecordDriverReservationHandler>.Instance)
                .HandleAsync(tripId, lateDriver, lateReservation, 1);
        }

        // W7/W8 accepts only the cleanup obligation; terminal business state
        // and the absence of an assignment remain unchanged (DSL-002).
        Assert.True(late.IsSuccess);

        await using var verify = Db();
        var after = await verify.Trips.AsNoTracking().SingleAsync(t => t.TripId == tripId);
        Assert.Equal(TripState.MatchFailed, after.State);
        Assert.Null(after.Assignment);
        Assert.Equal(1, after.CurrentMatchingRound);
        Assert.Equal(before.Version + 1, after.Version);
        var cleanup = Assert.Single(after.CleanupIntents);
        Assert.Equal(lateDriver, cleanup.DriverId);
        Assert.Equal(lateReservation, cleanup.ReservationId);
        Assert.Null(cleanup.CompletedAt);
    }
}
