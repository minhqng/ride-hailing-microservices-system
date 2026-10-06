// Driver accept/reject + cancellation-path domain proof (A-W05-03).
//
// Mandatory W5 properties: repeated DriverAccepted for the same legal current
// assignment is replay-safe; DriverAccepted for an old reservation is
// stale/rejected with no mutation. Rejection is validated against the
// current assignment and exposes the guarded cleanup requirement while
// leaving Trip state, assignment, round and Version untouched: guarded
// release and rematch belong to future workflow work.

using RideHailing.TripService.Domain.Trips;

namespace RideHailing.TripService.UnitTests.Domain.Trips;

public sealed class TripDriverDecisionTests
{
    [Fact]
    public void RecordDriverAccepted_SameCurrentAssignment_IsReplaySafe()
    {
        var trip = TripTestBuilders.AtState(TripState.DriverReserved);
        var first = trip.RecordDriverAccepted(
            TripTestBuilders.DriverId, TripTestBuilders.ReservationId, TripTestBuilders.ActionMoment);

        Assert.True(first.IsSuccess);
        Assert.False(first.IsReplay);
        Assert.Equal(TripState.DriverAccepted, trip.State);

        var version = trip.Version;
        var updatedAt = trip.UpdatedAt;

        var replay = trip.RecordDriverAccepted(
            TripTestBuilders.DriverId, TripTestBuilders.ReservationId, TripTestBuilders.ActionMoment);

        Assert.True(replay.IsSuccess);
        Assert.True(replay.IsReplay);
        Assert.Equal(TripState.DriverAccepted, trip.State);
        Assert.Equal(TripTestBuilders.DriverId, trip.Assignment!.DriverId);
        Assert.Equal(TripTestBuilders.ReservationId, trip.Assignment.ReservationId);
        Assert.Equal(1, trip.CurrentMatchingRound);
        Assert.Equal(version, trip.Version);
        Assert.Equal(updatedAt, trip.UpdatedAt);
    }

    [Fact]
    public void RecordDriverAccepted_OldReservation_IsRejectedWithoutMutation()
    {
        var trip = TripTestBuilders.AtState(TripState.DriverReserved);
        var version = trip.Version;
        var assignment = trip.Assignment;
        var updatedAt = trip.UpdatedAt;

        var outcome = trip.RecordDriverAccepted(
            TripTestBuilders.DriverId, TripTestBuilders.OtherReservationId, TripTestBuilders.ActionMoment);

        Assert.False(outcome.IsSuccess);
        Assert.Equal(TripErrorCode.AssignmentIdentityMismatch, outcome.ErrorCode);
        Assert.Equal(TripState.DriverReserved, trip.State);
        Assert.Same(assignment, trip.Assignment);
        Assert.Equal(1, trip.CurrentMatchingRound);
        Assert.Equal(updatedAt, trip.UpdatedAt);
        Assert.Equal(version, trip.Version);
    }

    [Fact]
    public void RecordDriverAccepted_WrongDriver_IsRejectedWithoutMutation()
    {
        var trip = TripTestBuilders.AtState(TripState.DriverReserved);
        var version = trip.Version;

        // Acceptance must not succeed merely because the Trip is
        // DRIVER_RESERVED: the identity must match the current assignment.
        var outcome = trip.RecordDriverAccepted(
            TripTestBuilders.OtherDriverId, TripTestBuilders.ReservationId, TripTestBuilders.ActionMoment);

        Assert.False(outcome.IsSuccess);
        Assert.Equal(TripErrorCode.AssignmentIdentityMismatch, outcome.ErrorCode);
        Assert.Equal(TripState.DriverReserved, trip.State);
        Assert.Equal(TripTestBuilders.DriverId, trip.Assignment!.DriverId);
        Assert.Equal(version, trip.Version);
    }

    [Fact]
    public void RecordDriverAccepted_BeforeReservation_IsRejectedWithoutMutation()
    {
        var trip = TripTestBuilders.MatchingTrip();
        var version = trip.Version;

        var outcome = trip.RecordDriverAccepted(
            TripTestBuilders.DriverId, TripTestBuilders.ReservationId, TripTestBuilders.ActionMoment);

        Assert.False(outcome.IsSuccess);
        Assert.Equal(TripErrorCode.IllegalTransition, outcome.ErrorCode);
        Assert.Equal(TripState.Matching, trip.State);
        Assert.Null(trip.Assignment);
        Assert.Equal(version, trip.Version);
    }

    [Fact]
    public void RecordDriverRejected_CurrentAssignment_ExposesCleanupWithoutMutation()
    {
        var trip = TripTestBuilders.AtState(TripState.DriverReserved);
        var version = trip.Version;
        var assignment = trip.Assignment;
        var updatedAt = trip.UpdatedAt;

        var outcome = trip.RecordDriverRejected(
            TripTestBuilders.DriverId, TripTestBuilders.ReservationId);

        Assert.True(outcome.IsSuccess);
        Assert.False(outcome.IsReplay);
        Assert.Equal(
            TripCompensation.ReleaseReservation | TripCompensation.CancelMatching,
            outcome.Compensation);

        // Guarded release + rematch are future workflow: nothing mutates.
        Assert.Equal(TripState.DriverReserved, trip.State);
        Assert.Same(assignment, trip.Assignment);
        Assert.Equal(1, trip.CurrentMatchingRound);
        Assert.Equal(updatedAt, trip.UpdatedAt);
        Assert.Equal(version, trip.Version);
    }

    [Fact]
    public void RecordDriverRejected_RepeatedValidation_ReaffirmsSameRequirementWithoutMutation()
    {
        // Rejection is a stateless validation of the current cleanup
        // requirement, not a stateful transition: Trip persists no rejection
        // memory, so an identical second validation succeeds alike. Success
        // means "the requirement still holds", never "a new compensation
        // was emitted"; nothing mutates and Version never advances.
        var trip = TripTestBuilders.AtState(TripState.DriverReserved);

        var first = trip.RecordDriverRejected(
            TripTestBuilders.DriverId, TripTestBuilders.ReservationId);
        var version = trip.Version;
        var updatedAt = trip.UpdatedAt;

        var second = trip.RecordDriverRejected(
            TripTestBuilders.DriverId, TripTestBuilders.ReservationId);

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.False(first.IsReplay);
        Assert.False(second.IsReplay);
        Assert.Equal(
            TripCompensation.ReleaseReservation | TripCompensation.CancelMatching,
            first.Compensation);
        Assert.Equal(first.Compensation, second.Compensation);
        Assert.Equal(TripState.DriverReserved, trip.State);
        Assert.Equal(TripTestBuilders.DriverId, trip.Assignment!.DriverId);
        Assert.Equal(TripTestBuilders.ReservationId, trip.Assignment.ReservationId);
        Assert.Equal(1, trip.CurrentMatchingRound);
        Assert.Equal(updatedAt, trip.UpdatedAt);
        Assert.Equal(version, trip.Version);
    }

    [Fact]
    public void RecordDriverRejected_StaleReservation_IsRejectedWithoutMutation()
    {
        var trip = TripTestBuilders.AtState(TripState.DriverReserved);
        var version = trip.Version;
        var assignment = trip.Assignment;

        var outcome = trip.RecordDriverRejected(
            TripTestBuilders.DriverId, TripTestBuilders.OtherReservationId);

        Assert.False(outcome.IsSuccess);
        Assert.Equal(TripErrorCode.AssignmentIdentityMismatch, outcome.ErrorCode);
        Assert.Equal(TripState.DriverReserved, trip.State);
        Assert.Same(assignment, trip.Assignment);
        Assert.Equal(TripCompensation.None, outcome.Compensation);
        Assert.Equal(version, trip.Version);
    }

    [Theory]
    [InlineData(TripState.Created)]
    [InlineData(TripState.Matching)]
    [InlineData(TripState.DriverAccepted)]
    [InlineData(TripState.ReadyToStart)]
    [InlineData(TripState.InProgress)]
    public void RecordDriverRejected_OutsideReservedWindow_IsRejected(TripState state)
    {
        var trip = TripTestBuilders.AtState(state);
        var version = trip.Version;

        var outcome = trip.RecordDriverRejected(
            TripTestBuilders.DriverId, TripTestBuilders.ReservationId);

        Assert.False(outcome.IsSuccess);
        Assert.Equal(TripErrorCode.IllegalTransition, outcome.ErrorCode);
        Assert.Equal(state, trip.State);
        Assert.Equal(TripCompensation.None, outcome.Compensation);
        Assert.Equal(version, trip.Version);
    }
}
