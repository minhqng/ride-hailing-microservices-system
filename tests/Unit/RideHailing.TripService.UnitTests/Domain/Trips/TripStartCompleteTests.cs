// StartTrip / CompleteTrip guards (DSI-011, DSI-012, DSI-026).
//
// Mandatory W5 properties: StartTrip before the required authorization/state
// is rejected with no mutation; CompleteTrip from the wrong state is
// rejected with no mutation. Completion never touches Payment authority.

using RideHailing.TripService.Domain.Trips;

namespace RideHailing.TripService.UnitTests.Domain.Trips;

public sealed class TripStartCompleteTests
{
    [Theory]
    [InlineData(TripState.Created)]
    [InlineData(TripState.Matching)]
    [InlineData(TripState.DriverReserved)]
    [InlineData(TripState.DriverAccepted)]
    public void Start_BeforeAuthorization_IsRejectedWithoutMutation(TripState state)
    {
        var trip = TripTestBuilders.AtState(state, withAssignment: state is not TripState.Created and not TripState.Matching);
        var version = trip.Version;
        var assignment = trip.Assignment;
        var round = trip.CurrentMatchingRound;
        var updatedAt = trip.UpdatedAt;

        var outcome = trip.Start(
            TripTestBuilders.DriverId, TripTestBuilders.ReservationId, TripTestBuilders.ActionMoment);

        Assert.False(outcome.IsSuccess);
        Assert.Equal(TripErrorCode.IllegalTransition, outcome.ErrorCode);
        Assert.Equal(state, trip.State);
        Assert.Same(assignment, trip.Assignment);
        Assert.Equal(round, trip.CurrentMatchingRound);
        Assert.Equal(updatedAt, trip.UpdatedAt);
        Assert.Equal(version, trip.Version);
    }

    [Fact]
    public void Start_WithStaleAssignment_IsRejectedWithoutMutation()
    {
        var trip = TripTestBuilders.AtState(TripState.ReadyToStart);
        var version = trip.Version;

        var outcome = trip.Start(
            TripTestBuilders.DriverId, TripTestBuilders.OtherReservationId, TripTestBuilders.ActionMoment);

        Assert.False(outcome.IsSuccess);
        Assert.Equal(TripErrorCode.AssignmentIdentityMismatch, outcome.ErrorCode);
        Assert.Equal(TripState.ReadyToStart, trip.State);
        Assert.Equal(version, trip.Version);
    }

    [Fact]
    public void Start_ExactCurrentStartOnInProgress_IsIdempotentReplay()
    {
        var trip = TripTestBuilders.AtState(TripState.InProgress);
        var version = trip.Version;
        var updatedAt = trip.UpdatedAt;

        var outcome = trip.Start(
            TripTestBuilders.DriverId, TripTestBuilders.ReservationId, TripTestBuilders.ActionMoment);

        Assert.True(outcome.IsSuccess);
        Assert.True(outcome.IsReplay);
        Assert.Equal(TripState.InProgress, trip.State);
        Assert.Equal(version, trip.Version);
        Assert.Equal(updatedAt, trip.UpdatedAt);
    }

    [Theory]
    [InlineData(TripState.Created)]
    [InlineData(TripState.Matching)]
    [InlineData(TripState.DriverReserved)]
    [InlineData(TripState.DriverAccepted)]
    [InlineData(TripState.ReadyToStart)]
    public void Complete_FromWrongState_IsRejectedWithoutMutation(TripState state)
    {
        var trip = TripTestBuilders.AtState(state, withAssignment: state is not TripState.Created and not TripState.Matching);
        var version = trip.Version;
        var assignment = trip.Assignment;
        var updatedAt = trip.UpdatedAt;

        var outcome = trip.Complete(
            TripTestBuilders.DriverId, TripTestBuilders.ReservationId, TripTestBuilders.ActionMoment);

        Assert.False(outcome.IsSuccess);
        Assert.Equal(state, trip.State);
        Assert.Same(assignment, trip.Assignment);
        Assert.Equal(updatedAt, trip.UpdatedAt);
        Assert.Equal(version, trip.Version);
    }

    [Fact]
    public void Complete_WithMismatchedDriver_IsRejectedWithoutMutation()
    {
        var trip = TripTestBuilders.AtState(TripState.InProgress);
        var version = trip.Version;

        var outcome = trip.Complete(
            TripTestBuilders.OtherDriverId, TripTestBuilders.ReservationId, TripTestBuilders.ActionMoment);

        Assert.False(outcome.IsSuccess);
        Assert.Equal(TripErrorCode.AssignmentIdentityMismatch, outcome.ErrorCode);
        Assert.Equal(TripState.InProgress, trip.State);
        Assert.Equal(TripTestBuilders.DriverId, trip.Assignment!.DriverId);
        Assert.Equal(version, trip.Version);
    }

    [Fact]
    public void Complete_CommitsTerminalCompleted_WithTimestamp()
    {
        var trip = TripTestBuilders.AtState(TripState.InProgress);
        var version = trip.Version;

        var outcome = trip.Complete(
            TripTestBuilders.DriverId, TripTestBuilders.ReservationId, TripTestBuilders.ActionMoment);

        Assert.True(outcome.IsSuccess);
        Assert.False(outcome.IsReplay);
        Assert.Equal(TripCompensation.None, outcome.Compensation);
        Assert.Equal(TripState.Completed, trip.State);
        Assert.NotNull(trip.Assignment);
        Assert.Equal(version + 1, trip.Version);
        Assert.Equal(TripTestBuilders.ActionMoment, trip.UpdatedAt);
    }
}
