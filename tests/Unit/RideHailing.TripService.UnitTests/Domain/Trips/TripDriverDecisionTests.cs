// Driver accept/reject + cancellation-path domain proof (A-W05-03, DS03).
//
// Mandatory W5 properties: repeated DriverAccepted for the same legal current
// assignment is replay-safe; DriverAccepted for an old reservation is
// stale/rejected with no mutation. DS03 rejection records a real terminal
// MATCH_FAILED/DRIVER_REJECTED result with exact cleanup intents; a duplicate
// exact rejection is an idempotent replay and every other rejection leaves
// the aggregate untouched.

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
    public void RecordDriverRejected_CurrentAssignment_ReachesTerminalWithExactCleanup()
    {
        var trip = TripTestBuilders.AtState(TripState.DriverReserved);
        var version = trip.Version;

        var outcome = trip.RecordDriverRejected(
            TripTestBuilders.DriverId, TripTestBuilders.ReservationId, TripTestBuilders.ActionMoment);

        Assert.True(outcome.IsSuccess);
        Assert.False(outcome.IsReplay);
        Assert.Equal(
            TripCompensation.ReleaseReservation | TripCompensation.CancelMatching,
            outcome.Compensation);

        Assert.Equal(TripState.MatchFailed, trip.State);
        Assert.Equal(TripFailureReason.DriverRejected, trip.FailureReason);
        Assert.Null(trip.Assignment);
        Assert.Equal(1, trip.CurrentMatchingRound);
        Assert.Equal(version + 1, trip.Version);
        Assert.Equal(TripTestBuilders.ActionMoment, trip.UpdatedAt);

        var release = Assert.Single(trip.CleanupIntents, i => i.Kind == TripCleanupKind.ReleaseReservation);
        Assert.Equal(TripTestBuilders.DriverId, release.DriverId);
        Assert.Equal(TripTestBuilders.ReservationId, release.ReservationId);
        Assert.Equal(1, release.MatchingRound);
        Assert.Null(release.CompletedAt);
        Assert.Single(trip.CleanupIntents, i => i.Kind == TripCleanupKind.CancelMatching);
    }

    [Fact]
    public void RecordDriverRejected_RepeatedExactRejection_IsIdempotentReplay()
    {
        var trip = TripTestBuilders.AtState(TripState.DriverReserved);

        var first = trip.RecordDriverRejected(
            TripTestBuilders.DriverId, TripTestBuilders.ReservationId, TripTestBuilders.ActionMoment);
        var version = trip.Version;
        var updatedAt = trip.UpdatedAt;

        var second = trip.RecordDriverRejected(
            TripTestBuilders.DriverId, TripTestBuilders.ReservationId, TripTestBuilders.ActionMoment);

        Assert.True(first.IsSuccess);
        Assert.False(first.IsReplay);
        Assert.True(second.IsSuccess);
        Assert.True(second.IsReplay);
        Assert.Equal(first.Compensation, second.Compensation);
        Assert.Equal(TripState.MatchFailed, trip.State);
        Assert.Equal(TripFailureReason.DriverRejected, trip.FailureReason);
        Assert.Equal(2, trip.CleanupIntents.Count);
        Assert.Equal(version, trip.Version);
        Assert.Equal(updatedAt, trip.UpdatedAt);
    }

    [Fact]
    public void RecordDriverRejected_StaleReservation_IsRejectedWithoutMutation()
    {
        var trip = TripTestBuilders.AtState(TripState.DriverReserved);
        var version = trip.Version;
        var assignment = trip.Assignment;

        var outcome = trip.RecordDriverRejected(
            TripTestBuilders.DriverId, TripTestBuilders.OtherReservationId, TripTestBuilders.ActionMoment);

        Assert.False(outcome.IsSuccess);
        Assert.Equal(TripErrorCode.AssignmentIdentityMismatch, outcome.ErrorCode);
        Assert.Equal(TripState.DriverReserved, trip.State);
        Assert.Same(assignment, trip.Assignment);
        Assert.Empty(trip.CleanupIntents);
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
            TripTestBuilders.DriverId, TripTestBuilders.ReservationId, TripTestBuilders.ActionMoment);

        Assert.False(outcome.IsSuccess);
        Assert.Equal(TripErrorCode.IllegalTransition, outcome.ErrorCode);
        Assert.Equal(state, trip.State);
        Assert.Equal(TripCompensation.None, outcome.Compensation);
        Assert.Equal(version, trip.Version);
    }

    [Fact]
    public void RecordDriverRejected_OnNoDriverMatchFailed_DoesNotReplayAnotherReason()
    {
        var trip = TripTestBuilders.AtState(TripState.MatchFailed, withAssignment: false);

        var outcome = trip.RecordDriverRejected(
            TripTestBuilders.DriverId, TripTestBuilders.ReservationId, TripTestBuilders.ActionMoment);

        Assert.False(outcome.IsSuccess);
        Assert.Equal(TripErrorCode.TerminalState, outcome.ErrorCode);
        Assert.Equal(TripState.MatchFailed, trip.State);
    }
}
