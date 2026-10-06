// MatchingFailed current-round fencing (A-W05-04, DSI-016).
//
// Only the current round's failure is terminal for the Trip. A stale round
// never regresses an advanced Trip, and a Trip holding a current assignment
// contradicts a clean matching failure.

using RideHailing.TripService.Domain.Trips;

namespace RideHailing.TripService.UnitTests.Domain.Trips;

public sealed class TripMatchingFailedTests
{
    [Fact]
    public void MarkMatchingFailed_CurrentRound_CommitsMatchFailed()
    {
        var trip = TripTestBuilders.MatchingTrip();
        var version = trip.Version;

        var outcome = trip.MarkMatchingFailed(1, TripTestBuilders.ActionMoment);

        Assert.True(outcome.IsSuccess);
        Assert.False(outcome.IsReplay);
        Assert.Equal(TripCompensation.None, outcome.Compensation);
        Assert.Equal(TripState.MatchFailed, trip.State);
        Assert.Null(trip.Assignment);
        Assert.Equal(1, trip.CurrentMatchingRound);
        Assert.Equal(version + 1, trip.Version);
        Assert.Equal(TripTestBuilders.ActionMoment, trip.UpdatedAt);
    }

    [Fact]
    public void MarkMatchingFailed_StaleRoundOnMatching_IsRejectedWithoutMutation()
    {
        var trip = TripTestBuilders.AtState(TripState.Matching, withAssignment: false, currentRound: 2);
        var version = trip.Version;
        var updatedAt = trip.UpdatedAt;

        var outcome = trip.MarkMatchingFailed(1, TripTestBuilders.ActionMoment);

        Assert.False(outcome.IsSuccess);
        Assert.Equal(TripErrorCode.StaleIdentity, outcome.ErrorCode);
        Assert.Equal(TripState.Matching, trip.State);
        Assert.Equal(2, trip.CurrentMatchingRound);
        Assert.Equal(updatedAt, trip.UpdatedAt);
        Assert.Equal(version, trip.Version);
    }

    [Fact]
    public void MarkMatchingFailed_AfterReservationWindowClosed_IsRejectedWithoutMutation()
    {
        // The Trip already advanced on round 1: the matching window is
        // closed, so the failure is an illegal edge (not a stale generation
        // decision) — either way it cannot regress the Trip and mutates
        // nothing. Stale-generation fencing for matching failure is proven
        // on MATCHING rows above; stale reservation generations are proven
        // in TripReservationFencingTests.
        var trip = TripTestBuilders.AtState(TripState.DriverReserved);
        var version = trip.Version;
        var assignment = trip.Assignment;

        var outcome = trip.MarkMatchingFailed(0, TripTestBuilders.ActionMoment);

        Assert.False(outcome.IsSuccess);
        Assert.Equal(TripErrorCode.IllegalTransition, outcome.ErrorCode);
        Assert.Equal(TripState.DriverReserved, trip.State);
        Assert.Same(assignment, trip.Assignment);
        Assert.Equal(version, trip.Version);
    }

    [Fact]
    public void MarkMatchingFailed_StaleRoundWithAssignmentContradiction_ReportsStaleFirst()
    {
        // Dual condition: the Trip holds an assignment AND the incoming
        // failure is from a superseded generation. Generation validity
        // (DSI-016) precedes same-generation conflict (DSI-008), mirroring
        // ApplyDriverReserved: a stale generation cannot fail the Trip.
        var trip = Trip.Reconstitute(
            TripTestBuilders.TripId,
            TripTestBuilders.RiderId,
            TripTestBuilders.Location(),
            TripTestBuilders.Location(),
            TripTestBuilders.Fare(),
            TripState.Matching,
            2,
            TripTestBuilders.Assignment(matchingRound: 2),
            TripTestBuilders.CreatedMoment,
            TripTestBuilders.CreatedMoment,
            3);
        var version = trip.Version;
        var assignment = trip.Assignment;
        var updatedAt = trip.UpdatedAt;

        var outcome = trip.MarkMatchingFailed(1, TripTestBuilders.ActionMoment);

        Assert.False(outcome.IsSuccess);
        Assert.Equal(TripErrorCode.StaleIdentity, outcome.ErrorCode);
        Assert.Equal(TripState.Matching, trip.State);
        Assert.Same(assignment, trip.Assignment);
        Assert.Equal(2, trip.CurrentMatchingRound);
        Assert.Equal(updatedAt, trip.UpdatedAt);
        Assert.Equal(version, trip.Version);
    }

    [Theory]
    [InlineData(TripState.Created)]
    [InlineData(TripState.DriverReserved)]
    [InlineData(TripState.DriverAccepted)]
    [InlineData(TripState.ReadyToStart)]
    [InlineData(TripState.InProgress)]
    public void MarkMatchingFailed_OutsideMatchingWindow_IsRejected(TripState state)
    {
        var trip = TripTestBuilders.AtState(state);
        var version = trip.Version;

        var outcome = trip.MarkMatchingFailed(1, TripTestBuilders.ActionMoment);

        Assert.False(outcome.IsSuccess);
        Assert.Equal(TripErrorCode.IllegalTransition, outcome.ErrorCode);
        Assert.Equal(state, trip.State);
        Assert.Equal(version, trip.Version);
    }
}
