// W5 lifecycle component proofs against disposable real PostgreSQL 18.x.
//
// Exercises the A-W05 application coordination (load → identity → single
// aggregate operation → persist → map) for every local lifecycle intake:
// StartMatching, Driver reservation/accept/reject, payment authorization,
// matching failure, start/complete and cancel. Each test asserts the
// authoritative reloaded row (never the handler result alone), the exact
// Version delta, replay/stale/conflict behavior and the compensation
// requirement representation. Driver/Payment/Matching identities below are
// authoritative-SHAPED test fixtures, not live B/C authority.

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RideHailing.TripService.Application.Trips.CreateRide;
using RideHailing.TripService.Application.Trips.Lifecycle;
using RideHailing.TripService.Domain.Trips;
using RideHailing.TripService.Infrastructure.Persistence;

namespace RideHailing.TripService.IntegrationTests;

[Collection(TripPostgresCollection.Name)]
public sealed class TripLifecycleComponentTests
{
    private readonly TripPostgresFixture _fixture;

    public TripLifecycleComponentTests(TripPostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static readonly DateTimeOffset CreatedAt = new(2026, 9, 26, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ActionAt = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

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
    public async Task StartMatching_PersistsMatchingRoundOne_WithVersionAdvance()
    {
        await MigrateAsync();
        var rider = Guid.NewGuid();
        var tripId = await CreateOwnedTripAsync(rider, "w5-start-matching");

        TripLifecycleResult result;
        await using (var db = Db())
        {
            result = await new StartMatchingHandler(db, Clock(), NullLogger<StartMatchingHandler>.Instance)
                .HandleAsync(tripId, rider);
        }

        Assert.True(result.IsSuccess);
        Assert.False(result.IsReplay);
        Assert.Equal(TripState.Matching, result.State);
        Assert.Equal(1, result.CurrentMatchingRound);

        await using var verify = Db();
        var trip = await verify.Trips.AsNoTracking().SingleAsync(t => t.TripId == tripId);
        Assert.Equal(TripState.Matching, trip.State);
        Assert.Equal(1, trip.CurrentMatchingRound);
        Assert.Null(trip.Assignment);
        Assert.Equal(1, trip.Version);
        Assert.True(Math.Abs((trip.UpdatedAt.ToUniversalTime() - ActionAt).TotalMilliseconds) < 1);
    }

    [Fact]
    public async Task StartMatching_Replay_PreservesRow()
    {
        await MigrateAsync();
        var rider = Guid.NewGuid();
        var tripId = await CreateOwnedTripAsync(rider, "w5-start-replay");

        await using (var db = Db())
        {
            var handler = new StartMatchingHandler(db, Clock(), NullLogger<StartMatchingHandler>.Instance);
            Assert.True((await handler.HandleAsync(tripId, rider)).IsSuccess);
        }

        TripLifecycleResult replay;
        await using (var db = Db())
        {
            replay = await new StartMatchingHandler(db, Clock(), NullLogger<StartMatchingHandler>.Instance)
                .HandleAsync(tripId, rider);
        }

        Assert.True(replay.IsSuccess);
        Assert.True(replay.IsReplay);

        await using var verify = Db();
        var trip = await verify.Trips.AsNoTracking().SingleAsync(t => t.TripId == tripId);
        Assert.Equal(TripState.Matching, trip.State);
        Assert.Equal(1, trip.Version);
    }

    [Fact]
    public async Task StartMatching_NotOwnedTrip_IsNotFound_WithoutSideEffects()
    {
        await MigrateAsync();
        var rider = Guid.NewGuid();
        var tripId = await CreateOwnedTripAsync(rider, "w5-start-scoped");

        TripLifecycleResult result;
        await using (var db = Db())
        {
            result = await new StartMatchingHandler(db, Clock(), NullLogger<StartMatchingHandler>.Instance)
                .HandleAsync(tripId, Guid.NewGuid());
        }

        Assert.True(result.IsNotFound);

        await using var verify = Db();
        var trip = await verify.Trips.AsNoTracking().SingleAsync(t => t.TripId == tripId);
        Assert.Equal(TripState.Created, trip.State);
        Assert.Equal(0, trip.CurrentMatchingRound);
        Assert.Equal(0, trip.Version);
    }

    [Fact]
    public async Task ReservationIntake_ThenStaleRound_ThenConflict_BehaveAsFenced()
    {
        await MigrateAsync();
        var rider = Guid.NewGuid();
        var tripId = await CreateOwnedTripAsync(rider, "w5-reserve-fence");
        var driverId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();

        await using (var db = Db())
        {
            Assert.True((await new StartMatchingHandler(db, Clock(), NullLogger<StartMatchingHandler>.Instance)
                .HandleAsync(tripId, rider)).IsSuccess);
        }

        TripLifecycleResult applied;
        await using (var db = Db())
        {
            applied = await new RecordDriverReservationHandler(db, Clock(), NullLogger<RecordDriverReservationHandler>.Instance)
                .HandleAsync(tripId, driverId, reservationId, 1);
        }

        Assert.True(applied.IsSuccess);
        Assert.False(applied.IsReplay);
        Assert.Equal(TripState.DriverReserved, applied.State);
        Assert.Equal(driverId, applied.AssignedDriverId);
        Assert.Equal(reservationId, applied.ReservationId);

        TripLifecycleResult stale;
        await using (var db = Db())
        {
            stale = await new RecordDriverReservationHandler(db, Clock(), NullLogger<RecordDriverReservationHandler>.Instance)
                .HandleAsync(tripId, Guid.NewGuid(), Guid.NewGuid(), 7);
        }

        Assert.False(stale.IsSuccess);
        Assert.Equal(TripErrorCode.StaleIdentity, stale.ErrorCode);

        TripLifecycleResult conflict;
        await using (var db = Db())
        {
            conflict = await new RecordDriverReservationHandler(db, Clock(), NullLogger<RecordDriverReservationHandler>.Instance)
                .HandleAsync(tripId, Guid.NewGuid(), Guid.NewGuid(), 1);
        }

        Assert.False(conflict.IsSuccess);
        Assert.Equal(TripErrorCode.AssignmentIdentityMismatch, conflict.ErrorCode);

        TripLifecycleResult replay;
        await using (var db = Db())
        {
            replay = await new RecordDriverReservationHandler(db, Clock(), NullLogger<RecordDriverReservationHandler>.Instance)
                .HandleAsync(tripId, driverId, reservationId, 1);
        }

        Assert.True(replay.IsSuccess);
        Assert.True(replay.IsReplay);

        await using var verify = Db();
        var trip = await verify.Trips.AsNoTracking().SingleAsync(t => t.TripId == tripId);
        Assert.Equal(TripState.DriverReserved, trip.State);
        Assert.Equal(driverId, trip.Assignment!.DriverId);
        Assert.Equal(reservationId, trip.Assignment.ReservationId);
        Assert.Equal(1, trip.Assignment.MatchingRound);
        Assert.Equal(2, trip.Version);
    }

    [Fact]
    public async Task AcceptRejectIntake_FencesIdentity_AndExposesCleanupRequirement()
    {
        await MigrateAsync();
        var rider = Guid.NewGuid();
        var tripId = await CreateOwnedTripAsync(rider, "w5-decision");
        var driverId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();

        await using (var db = Db())
        {
            Assert.True((await new StartMatchingHandler(db, Clock(), NullLogger<StartMatchingHandler>.Instance)
                .HandleAsync(tripId, rider)).IsSuccess);
            Assert.True((await new RecordDriverReservationHandler(db, Clock(), NullLogger<RecordDriverReservationHandler>.Instance)
                .HandleAsync(tripId, driverId, reservationId, 1)).IsSuccess);
        }

        TripLifecycleResult wrongDriver;
        await using (var db = Db())
        {
            wrongDriver = await new RecordDriverAcceptedHandler(db, Clock(), NullLogger<RecordDriverAcceptedHandler>.Instance)
                .HandleAsync(tripId, Guid.NewGuid(), reservationId);
        }

        Assert.False(wrongDriver.IsSuccess);
        Assert.Equal(TripErrorCode.AssignmentIdentityMismatch, wrongDriver.ErrorCode);

        TripLifecycleResult rejected;
        await using (var db = Db())
        {
            rejected = await new RecordDriverRejectedHandler(db, NullLogger<RecordDriverRejectedHandler>.Instance)
                .HandleAsync(tripId, driverId, reservationId);
        }

        Assert.True(rejected.IsSuccess);
        Assert.Equal(
            TripCompensation.ReleaseReservation | TripCompensation.CancelMatching,
            rejected.Compensation);
        Assert.Equal(TripState.DriverReserved, rejected.State);

        TripLifecycleResult accepted;
        await using (var db = Db())
        {
            accepted = await new RecordDriverAcceptedHandler(db, Clock(), NullLogger<RecordDriverAcceptedHandler>.Instance)
                .HandleAsync(tripId, driverId, reservationId);
        }

        Assert.True(accepted.IsSuccess);
        Assert.False(accepted.IsReplay);
        Assert.Equal(TripState.DriverAccepted, accepted.State);

        TripLifecycleResult acceptReplay;
        await using (var db = Db())
        {
            acceptReplay = await new RecordDriverAcceptedHandler(db, Clock(), NullLogger<RecordDriverAcceptedHandler>.Instance)
                .HandleAsync(tripId, driverId, reservationId);
        }

        Assert.True(acceptReplay.IsSuccess);
        Assert.True(acceptReplay.IsReplay);

        await using var verify = Db();
        var trip = await verify.Trips.AsNoTracking().SingleAsync(t => t.TripId == tripId);
        Assert.Equal(TripState.DriverAccepted, trip.State);
        Assert.Equal(driverId, trip.Assignment!.DriverId);
        Assert.Equal(3, trip.Version);
    }

    [Fact]
    public async Task PaymentFailedIntake_PersistsCancelled_WithExactCleanupRequirement()
    {
        await MigrateAsync();
        var rider = Guid.NewGuid();
        var tripId = await CreateOwnedTripAsync(rider, "w5-payment-failed");
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

        TripLifecycleResult failed;
        await using (var db = Db())
        {
            failed = await new MarkPaymentFailedHandler(db, Clock(), NullLogger<MarkPaymentFailedHandler>.Instance)
                .HandleAsync(tripId);
        }

        Assert.True(failed.IsSuccess);
        Assert.False(failed.IsReplay);
        Assert.Equal(TripState.Cancelled, failed.State);
        Assert.Equal(
            TripCompensation.ReleaseReservation | TripCompensation.CancelMatching,
            failed.Compensation);
        Assert.Equal(driverId, failed.AssignedDriverId);
        Assert.Equal(reservationId, failed.ReservationId);

        await using var verify = Db();
        var trip = await verify.Trips.AsNoTracking().SingleAsync(t => t.TripId == tripId);
        Assert.Equal(TripState.Cancelled, trip.State);
        Assert.Equal(driverId, trip.Assignment!.DriverId);
        Assert.Equal(reservationId, trip.Assignment.ReservationId);
        Assert.Equal(1, trip.CurrentMatchingRound);
        Assert.Equal(4, trip.Version);
        Assert.True(Math.Abs((trip.UpdatedAt.ToUniversalTime() - ActionAt).TotalMilliseconds) < 1);
    }

    [Fact]
    public async Task PaymentFailedIntake_WrongState_IsRejectedWithoutMutation()
    {
        await MigrateAsync();
        var rider = Guid.NewGuid();
        var tripId = await CreateOwnedTripAsync(rider, "w5-payment-failed-early");
        await using (var db = Db())
        {
            Assert.True((await new StartMatchingHandler(db, Clock(), NullLogger<StartMatchingHandler>.Instance)
                .HandleAsync(tripId, rider)).IsSuccess);
        }

        TripLifecycleResult early;
        await using (var db = Db())
        {
            early = await new MarkPaymentFailedHandler(db, Clock(), NullLogger<MarkPaymentFailedHandler>.Instance)
                .HandleAsync(tripId);
        }

        Assert.False(early.IsSuccess);
        Assert.Equal(TripErrorCode.IllegalTransition, early.ErrorCode);

        await using var verify = Db();
        var trip = await verify.Trips.AsNoTracking().SingleAsync(t => t.TripId == tripId);
        Assert.Equal(TripState.Matching, trip.State);
        Assert.Null(trip.Assignment);
        Assert.Equal(1, trip.Version);
    }

    [Fact]
    public async Task PaymentFailedIntake_TerminalTrip_IsRejectedWithoutMutation()
    {
        await MigrateAsync();
        var rider = Guid.NewGuid();
        var tripId = await CreateOwnedTripAsync(rider, "w5-payment-failed-terminal");
        await using (var db = Db())
        {
            Assert.True((await new StartMatchingHandler(db, Clock(), NullLogger<StartMatchingHandler>.Instance)
                .HandleAsync(tripId, rider)).IsSuccess);
            Assert.True((await new CancelRideHandler(db, Clock(), NullLogger<CancelRideHandler>.Instance)
                .HandleAsync(tripId, rider)).IsSuccess);
        }

        TripLifecycleResult late;
        await using (var db = Db())
        {
            late = await new MarkPaymentFailedHandler(db, Clock(), NullLogger<MarkPaymentFailedHandler>.Instance)
                .HandleAsync(tripId);
        }

        Assert.False(late.IsSuccess);
        Assert.Equal(TripErrorCode.TerminalState, late.ErrorCode);

        await using var verify = Db();
        var trip = await verify.Trips.AsNoTracking().SingleAsync(t => t.TripId == tripId);
        Assert.Equal(TripState.Cancelled, trip.State);
        Assert.Equal(2, trip.Version);
    }

    [Fact]
    public async Task CancelMatrix_PersistsTerminalState_WithExactCleanupRequirement()
    {
        await MigrateAsync();

        // MATCHING without assignment: matching cleanup only.
        var matchingRider = Guid.NewGuid();
        var matchingTrip = await CreateOwnedTripAsync(matchingRider, "w5-cancel-matching");
        await using (var db = Db())
        {
            Assert.True((await new StartMatchingHandler(db, Clock(), NullLogger<StartMatchingHandler>.Instance)
                .HandleAsync(matchingTrip, matchingRider)).IsSuccess);
        }

        TripLifecycleResult matchingCancel;
        await using (var db = Db())
        {
            matchingCancel = await new CancelRideHandler(db, Clock(), NullLogger<CancelRideHandler>.Instance)
                .HandleAsync(matchingTrip, matchingRider);
        }

        Assert.True(matchingCancel.IsSuccess);
        Assert.Equal(TripCompensation.CancelMatching, matchingCancel.Compensation);
        Assert.Equal(TripState.Cancelled, matchingCancel.State);

        // DRIVER_RESERVED with assignment: exact release + matching cleanup.
        var assignedRider = Guid.NewGuid();
        var assignedTrip = await CreateOwnedTripAsync(assignedRider, "w5-cancel-assigned");
        var driverId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        await using (var db = Db())
        {
            Assert.True((await new StartMatchingHandler(db, Clock(), NullLogger<StartMatchingHandler>.Instance)
                .HandleAsync(assignedTrip, assignedRider)).IsSuccess);
            Assert.True((await new RecordDriverReservationHandler(db, Clock(), NullLogger<RecordDriverReservationHandler>.Instance)
                .HandleAsync(assignedTrip, driverId, reservationId, 1)).IsSuccess);
        }

        TripLifecycleResult assignedCancel;
        await using (var db = Db())
        {
            assignedCancel = await new CancelRideHandler(db, Clock(), NullLogger<CancelRideHandler>.Instance)
                .HandleAsync(assignedTrip, assignedRider);
        }

        Assert.True(assignedCancel.IsSuccess);
        Assert.Equal(
            TripCompensation.ReleaseReservation | TripCompensation.CancelMatching,
            assignedCancel.Compensation);

        await using var verify = Db();
        var reloaded = await verify.Trips.AsNoTracking().SingleAsync(t => t.TripId == assignedTrip);
        Assert.Equal(TripState.Cancelled, reloaded.State);
        Assert.Equal(driverId, reloaded.Assignment!.DriverId);
        Assert.Equal(reservationId, reloaded.Assignment.ReservationId);

        // CREATED cancellation stays policy-unresolved with no mutation.
        var createdRider = Guid.NewGuid();
        var createdTrip = await CreateOwnedTripAsync(createdRider, "w5-cancel-created");

        TripLifecycleResult createdCancel;
        await using (var db = Db())
        {
            createdCancel = await new CancelRideHandler(db, Clock(), NullLogger<CancelRideHandler>.Instance)
                .HandleAsync(createdTrip, createdRider);
        }

        Assert.False(createdCancel.IsSuccess);
        Assert.Equal(TripErrorCode.CancellationPolicyUnresolved, createdCancel.ErrorCode);

        await using var verifyCreated = Db();
        var stillCreated = await verifyCreated.Trips.AsNoTracking().SingleAsync(t => t.TripId == createdTrip);
        Assert.Equal(TripState.Created, stillCreated.State);
        Assert.Equal(0, stillCreated.Version);
    }

    [Fact]
    public async Task MatchingFailed_StaleRound_DoesNotRegressReservedTrip()
    {
        await MigrateAsync();
        var rider = Guid.NewGuid();
        var tripId = await CreateOwnedTripAsync(rider, "w5-matching-failed");
        var driverId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();

        await using (var db = Db())
        {
            Assert.True((await new StartMatchingHandler(db, Clock(), NullLogger<StartMatchingHandler>.Instance)
                .HandleAsync(tripId, rider)).IsSuccess);
            Assert.True((await new RecordDriverReservationHandler(db, Clock(), NullLogger<RecordDriverReservationHandler>.Instance)
                .HandleAsync(tripId, driverId, reservationId, 1)).IsSuccess);
        }

        TripLifecycleResult stale;
        await using (var db = Db())
        {
            stale = await new MarkMatchingFailedHandler(db, Clock(), NullLogger<MarkMatchingFailedHandler>.Instance)
                .HandleAsync(tripId, 9);
        }

        Assert.False(stale.IsSuccess);

        await using var verify = Db();
        var trip = await verify.Trips.AsNoTracking().SingleAsync(t => t.TripId == tripId);
        Assert.Equal(TripState.DriverReserved, trip.State);
        Assert.Equal(driverId, trip.Assignment!.DriverId);
    }

    [Fact]
    public async Task Lifecycle_OptimisticConcurrency_LosesSecondWriter_WithTypedConflict()
    {
        await MigrateAsync();
        var rider = Guid.NewGuid();
        var tripId = await CreateOwnedTripAsync(rider, "w5-lifecycle-occ");

        await using (var start = Db())
        {
            Assert.True((await new StartMatchingHandler(start, Clock(), NullLogger<StartMatchingHandler>.Instance)
                .HandleAsync(tripId, rider)).IsSuccess);
        }

        await using var dbA = Db();
        await using var dbB = Db();
        var tripA = await dbA.Trips.SingleAsync(t => t.TripId == tripId);
        var tripB = await dbB.Trips.SingleAsync(t => t.TripId == tripId);

        Assert.True(tripA.ApplyDriverReserved(Guid.NewGuid(), Guid.NewGuid(), 1, ActionAt).IsSuccess);
        Assert.True(tripB.MarkMatchingFailed(1, ActionAt).IsSuccess);
        await dbA.SaveChangesAsync();

        await Assert.ThrowsAsync<TripConcurrencyConflictException>(() => dbB.SaveChangesAsync());

        await using var verify = Db();
        var authoritative = await verify.Trips.AsNoTracking().SingleAsync(t => t.TripId == tripId);
        Assert.Equal(TripState.DriverReserved, authoritative.State);
        Assert.NotNull(authoritative.Assignment);
    }
}
