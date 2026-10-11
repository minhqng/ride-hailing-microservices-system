// Canonical cancellation matrix (A-W05 cancellation + compensation
// requirement). Every allowed pre-start state reaches CANCELLED with the
// exact cleanup requirement REPRESENTED on the outcome (never executed:
// no Saga, no broker dispatch, no release orchestration in W5). Rejected
// branches prove no partial mutation.

using RideHailing.TripService.Domain.Trips;

namespace RideHailing.TripService.UnitTests.Domain.Trips;

public sealed class TripCancellationMatrixTests
{
    [Fact]
    public void Cancel_FromMatching_ExposesMatchingCleanupOnly()
    {
        var trip = TripTestBuilders.MatchingTrip();
        var version = trip.Version;

        var outcome = trip.Cancel(TripTestBuilders.ActionMoment);

        Assert.True(outcome.IsSuccess);
        Assert.False(outcome.IsReplay);
        Assert.Equal(TripCompensation.CancelMatching, outcome.Compensation);
        Assert.Equal(TripState.Cancelled, trip.State);
        Assert.Null(trip.Assignment);
        Assert.Equal(1, trip.CurrentMatchingRound);
        Assert.Equal(version + 1, trip.Version);
        Assert.Equal(TripTestBuilders.ActionMoment, trip.UpdatedAt);
    }

    [Theory]
    [InlineData(TripState.DriverReserved)]
    [InlineData(TripState.DriverAccepted)]
    public void Cancel_FromAssignedPreStartState_PreservesAssignment_AndExposesExactCleanup(TripState state)
    {
        var trip = TripTestBuilders.AtState(state);
        var version = trip.Version;
        var assignment = trip.Assignment;

        var outcome = trip.Cancel(TripTestBuilders.ActionMoment);

        Assert.True(outcome.IsSuccess);
        Assert.Equal(
            TripCompensation.ReleaseReservation | TripCompensation.CancelMatching,
            outcome.Compensation);
        Assert.Equal(TripState.Cancelled, trip.State);
        Assert.Same(assignment, trip.Assignment);
        Assert.Equal(TripTestBuilders.DriverId, trip.Assignment!.DriverId);
        Assert.Equal(TripTestBuilders.ReservationId, trip.Assignment.ReservationId);
        Assert.Equal(1, trip.CurrentMatchingRound);
        Assert.Equal(version + 1, trip.Version);
        Assert.Equal(TripTestBuilders.ActionMoment, trip.UpdatedAt);
    }

    [Theory]
    [InlineData(TripState.Created)]
    [InlineData(TripState.ReadyToStart)]
    public void Cancel_FromPolicyUnresolvedState_LeavesTripUnapplied(TripState state)
    {
        // Product policy is not frozen for these branches: no allow/reject
        // semantic is locked in, and nothing mutates.
        var trip = TripTestBuilders.AtState(state);
        var version = trip.Version;
        var assignment = trip.Assignment;
        var round = trip.CurrentMatchingRound;
        var updatedAt = trip.UpdatedAt;

        var outcome = trip.Cancel(TripTestBuilders.ActionMoment);

        Assert.False(outcome.IsSuccess);
        Assert.Equal(TripErrorCode.CancellationPolicyUnresolved, outcome.ErrorCode);
        Assert.Equal(TripCompensation.None, outcome.Compensation);
        Assert.Equal(state, trip.State);
        Assert.Same(assignment, trip.Assignment);
        Assert.Equal(round, trip.CurrentMatchingRound);
        Assert.Equal(updatedAt, trip.UpdatedAt);
        Assert.Equal(version, trip.Version);
    }

    [Fact]
    public void MarkPaymentFailed_ExposesExactCleanup_AndPreservesAssignment()
    {
        var trip = TripTestBuilders.AtState(TripState.DriverAccepted);
        var version = trip.Version;
        var assignment = trip.Assignment;

        var outcome = trip.MarkPaymentFailed(TripTestBuilders.ActionMoment);

        Assert.True(outcome.IsSuccess);
        Assert.Equal(
            TripCompensation.ReleaseReservation | TripCompensation.CancelMatching,
            outcome.Compensation);
        Assert.Equal(TripState.Cancelled, trip.State);
        Assert.Same(assignment, trip.Assignment);
        Assert.Equal(version + 1, trip.Version);
        Assert.Equal(TripTestBuilders.ActionMoment, trip.UpdatedAt);
    }

    [Fact]
    public void MarkPaymentAuthorized_CommitsReadyToStart_WithoutCompensation()
    {
        var trip = TripTestBuilders.AtState(TripState.DriverAccepted);
        var version = trip.Version;

        var outcome = trip.MarkPaymentAuthorized(TripTestBuilders.ActionMoment);

        Assert.True(outcome.IsSuccess);
        Assert.False(outcome.IsReplay);
        Assert.Equal(TripCompensation.None, outcome.Compensation);
        Assert.Equal(TripState.ReadyToStart, trip.State);
        Assert.NotNull(trip.Assignment);
        Assert.Equal(version + 1, trip.Version);
        Assert.Equal(TripTestBuilders.ActionMoment, trip.UpdatedAt);
    }

    [Fact]
    public void MarkPaymentAuthorized_ReplayOnReadyToStart_IsHarmless()
    {
        var trip = TripTestBuilders.AtState(TripState.ReadyToStart);
        var version = trip.Version;
        var updatedAt = trip.UpdatedAt;

        var outcome = trip.MarkPaymentAuthorized(TripTestBuilders.ActionMoment);

        Assert.True(outcome.IsSuccess);
        Assert.True(outcome.IsReplay);
        Assert.Equal(TripState.ReadyToStart, trip.State);
        Assert.Equal(version, trip.Version);
        Assert.Equal(updatedAt, trip.UpdatedAt);
    }

    [Theory]
    [InlineData(TripState.Created)]
    [InlineData(TripState.Matching)]
    public void MarkPaymentAuthorized_BeforeAcceptance_IsRejectedWithoutMutation(TripState state)
    {
        var trip = TripTestBuilders.AtState(state, withAssignment: false);
        var version = trip.Version;
        var round = trip.CurrentMatchingRound;
        var updatedAt = trip.UpdatedAt;

        var outcome = trip.MarkPaymentAuthorized(TripTestBuilders.ActionMoment);

        Assert.False(outcome.IsSuccess);
        Assert.Equal(TripErrorCode.IllegalTransition, outcome.ErrorCode);
        Assert.Equal(state, trip.State);
        Assert.Equal(round, trip.CurrentMatchingRound);
        Assert.Null(trip.Assignment);
        Assert.Equal(updatedAt, trip.UpdatedAt);
        Assert.Equal(version, trip.Version);
    }

    [Fact]
    public void MarkPaymentAuthorized_OnReservedBeforeAcceptance_IsRejectedWithoutMutation()
    {
        var trip = TripTestBuilders.AtState(TripState.DriverReserved);
        var version = trip.Version;
        var assignment = trip.Assignment;

        var outcome = trip.MarkPaymentAuthorized(TripTestBuilders.ActionMoment);

        Assert.False(outcome.IsSuccess);
        Assert.Equal(TripErrorCode.IllegalTransition, outcome.ErrorCode);
        Assert.Equal(TripState.DriverReserved, trip.State);
        Assert.Same(assignment, trip.Assignment);
        Assert.Equal(version, trip.Version);
    }

    [Theory]
    [InlineData(TripState.Created)]
    [InlineData(TripState.Matching)]
    public void MarkPaymentFailed_OutsideAcceptedWindow_IsRejectedWithoutMutation(TripState state)
    {
        var trip = TripTestBuilders.AtState(state, withAssignment: false);
        var version = trip.Version;

        var outcome = trip.MarkPaymentFailed(TripTestBuilders.ActionMoment);

        Assert.False(outcome.IsSuccess);
        Assert.Equal(TripErrorCode.IllegalTransition, outcome.ErrorCode);
        Assert.Equal(state, trip.State);
        Assert.Equal(version, trip.Version);
    }

    [Fact]
    public void MarkPaymentFailed_OnReservedBeforeAcceptance_IsRejectedWithoutMutation()
    {
        var trip = TripTestBuilders.AtState(TripState.DriverReserved);
        var version = trip.Version;
        var assignment = trip.Assignment;

        var outcome = trip.MarkPaymentFailed(TripTestBuilders.ActionMoment);

        Assert.False(outcome.IsSuccess);
        Assert.Equal(TripErrorCode.IllegalTransition, outcome.ErrorCode);
        Assert.Equal(TripState.DriverReserved, trip.State);
        Assert.Same(assignment, trip.Assignment);
        Assert.Equal(version, trip.Version);
    }
}
