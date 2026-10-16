using RideHailing.TripService.Domain.Trips;

namespace RideHailing.TripService.UnitTests.Domain.Trips;

public sealed class TripCleanupTests
{
    [Fact]
    public void MatchFailed_LateReservation_CanBeReleasedWithoutReopening()
    {
        var trip = TripTestBuilders.AtState(TripState.MatchFailed);
        var driver = Guid.NewGuid();
        var reservation = Guid.NewGuid();
        Assert.True(trip.RecordPreStartTerminalReservation(driver, reservation, trip.CurrentMatchingRound,
            TripTestBuilders.ActionMoment).IsSuccess);
        Assert.Null(trip.Assignment);
        var cleanup = Assert.Single(trip.CleanupIntents);
        Assert.Equal(driver, cleanup.DriverId);
        Assert.Equal(reservation, cleanup.ReservationId);
        Assert.True(trip.ConfirmCleanup(cleanup.Key, TripTestBuilders.ActionMoment).IsSuccess);
        var version = trip.Version;
        Assert.True(trip.RecordPreStartTerminalReservation(driver, reservation, trip.CurrentMatchingRound,
            TripTestBuilders.ActionMoment).IsReplay);
        Assert.True(trip.ConfirmCleanup(cleanup.Key, TripTestBuilders.ActionMoment).IsReplay);
        Assert.Equal(version, trip.Version);
        Assert.Equal(TripState.MatchFailed, trip.State);
    }

    [Theory]
    [InlineData(TripState.Created)]
    [InlineData(TripState.Matching)]
    [InlineData(TripState.DriverReserved)]
    [InlineData(TripState.DriverAccepted)]
    [InlineData(TripState.ReadyToStart)]
    [InlineData(TripState.InProgress)]
    [InlineData(TripState.Completed)]
    public void PreStartTerminalCleanup_RejectsOtherStates(TripState state)
    {
        var trip = TripTestBuilders.AtState(state);
        var version = trip.Version;
        Assert.False(trip.RecordPreStartTerminalReservation(Guid.NewGuid(), Guid.NewGuid(), 1,
            TripTestBuilders.ActionMoment).IsSuccess);
        Assert.Equal(version, trip.Version);
        Assert.Equal(state, trip.State);
        Assert.Empty(trip.CleanupIntents);
    }

    [Theory]
    [InlineData(TripState.Matching, 1)]
    [InlineData(TripState.DriverReserved, 3)]
    [InlineData(TripState.DriverAccepted, 3)]
    public void Cancel_CreatesExactObligations(TripState state, int count)
    {
        var trip = TripTestBuilders.AtState(state);
        Assert.True(trip.Cancel(TripTestBuilders.ActionMoment).IsSuccess);
        Assert.Equal(count, trip.CleanupIntents.Count);
        Assert.All(trip.CleanupIntents, i => { Assert.Equal(trip.TripId, i.TripId); Assert.Null(i.CompletedAt); });
        if (trip.Assignment is not null)
        {
            var release = Assert.Single(trip.CleanupIntents, i => i.Kind == TripCleanupKind.ReleaseReservation);
            Assert.Equal(trip.Assignment.DriverId, release.DriverId);
            Assert.Equal(trip.Assignment.ReservationId, release.ReservationId);
        }
    }

    [Fact]
    public void LateReservation_RecordsCleanupWithoutAssignment_AndReplaysWithoutMutation()
    {
        var trip = TripTestBuilders.MatchingTrip();
        trip.Cancel(TripTestBuilders.ActionMoment);
        var driver = Guid.NewGuid();
        var reservation = Guid.NewGuid();
        Assert.True(trip.RecordCancelledReservation(driver, reservation, 1, TripTestBuilders.ActionMoment).IsSuccess);
        var version = trip.Version;
        Assert.True(trip.RecordCancelledReservation(driver, reservation, 1, TripTestBuilders.ActionMoment).IsReplay);
        Assert.Equal(version, trip.Version);
        Assert.Equal(TripState.Cancelled, trip.State);
        Assert.Null(trip.Assignment);
        Assert.False(trip.RecordCancelledReservation(Guid.NewGuid(), reservation, 1, TripTestBuilders.ActionMoment).IsSuccess);
        Assert.False(trip.RecordCancelledReservation(driver, Guid.NewGuid(), 2, TripTestBuilders.ActionMoment).IsSuccess);
        Assert.Equal(version, trip.Version);
    }

    [Theory]
    [InlineData(TripState.Completed)]
    [InlineData(TripState.InProgress)]
    public void CleanupIntakes_CannotChangeOtherStates(TripState state)
    {
        // MatchFailed is intentionally excluded: pre-start terminal cleanup
        // accepts exact late reservations via RecordPreStartTerminalReservation
        // (DSL-002). This theory pins the legacy Cancelled-only shim plus
        // non-terminal rejection; see MatchFailed_LateReservation above.
        var trip = TripTestBuilders.AtState(state);
        var version = trip.Version;
        Assert.False(trip.RecordCancelledReservation(Guid.NewGuid(), Guid.NewGuid(), 1, TripTestBuilders.ActionMoment).IsSuccess);
        Assert.False(trip.RecordCancelledPaymentAuthorization(TripTestBuilders.ActionMoment).IsSuccess);
        Assert.False(trip.ConfirmCleanup("payment", TripTestBuilders.ActionMoment).IsSuccess);
        Assert.Equal(state, trip.State);
        Assert.Equal(version, trip.Version);
        Assert.Empty(trip.CleanupIntents);
    }

    [Fact]
    public void LateAuthorization_RequiresReconciliation_NeverReactivates()
    {
        var trip = TripTestBuilders.MatchingTrip();
        trip.Cancel(TripTestBuilders.ActionMoment);
        Assert.True(trip.RecordCancelledPaymentAuthorization(TripTestBuilders.ActionMoment).IsSuccess);
        Assert.True(trip.RecordCancelledPaymentAuthorization(TripTestBuilders.ActionMoment).IsReplay);
        Assert.Single(trip.CleanupIntents, i => i.Kind == TripCleanupKind.ReconcilePayment);
        Assert.True(trip.ConfirmCleanup("payment", TripTestBuilders.ActionMoment).IsSuccess);
        var version = trip.Version;
        Assert.True(trip.ConfirmCleanup("payment", TripTestBuilders.ActionMoment).IsReplay);
        Assert.True(trip.RecordCancelledPaymentAuthorization(TripTestBuilders.ActionMoment).IsReplay);
        Assert.Equal(version, trip.Version);
        Assert.Equal(TripState.Cancelled, trip.State);
    }
}
