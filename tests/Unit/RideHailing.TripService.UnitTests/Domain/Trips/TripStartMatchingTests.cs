// A-W05-01/02 proofs: StartMatching opens the first matching round and the
// current-round model rejects anything that is not the single W5 round.

using RideHailing.TripService.Domain.Trips;

namespace RideHailing.TripService.UnitTests.Domain.Trips;

public sealed class TripStartMatchingTests
{
    [Fact]
    public void StartMatching_FromCreated_OpensRoundOne()
    {
        var trip = TripTestBuilders.NewTrip();
        var version = trip.Version;

        var outcome = trip.StartMatching(TripTestBuilders.ActionMoment);

        Assert.True(outcome.IsSuccess);
        Assert.False(outcome.IsReplay);
        Assert.Equal(TripCompensation.None, outcome.Compensation);
        Assert.Equal(TripState.Matching, trip.State);
        Assert.Equal(1, trip.CurrentMatchingRound);
        Assert.Null(trip.Assignment);
        Assert.Equal(version + 1, trip.Version);
        Assert.Equal(TripTestBuilders.ActionMoment, trip.UpdatedAt);
    }

    [Fact]
    public void StartMatching_OnRoundOneMatching_IsIdempotentReplay()
    {
        var trip = TripTestBuilders.MatchingTrip();
        var version = trip.Version;
        var updatedAt = trip.UpdatedAt;

        var outcome = trip.StartMatching(TripTestBuilders.ActionMoment);

        Assert.True(outcome.IsSuccess);
        Assert.True(outcome.IsReplay);
        Assert.Equal(TripState.Matching, trip.State);
        Assert.Equal(1, trip.CurrentMatchingRound);
        Assert.Null(trip.Assignment);
        Assert.Equal(version, trip.Version);
        Assert.Equal(updatedAt, trip.UpdatedAt);
    }

    [Fact]
    public void StartMatching_OnLaterRoundMatching_IsRejected()
    {
        // W5 owns no rematch operation: a round beyond 1 cannot be opened
        // from here. Future workflow will own the guarded rematch edge.
        var trip = TripTestBuilders.AtState(TripState.Matching, withAssignment: false, currentRound: 2);
        var version = trip.Version;

        var outcome = trip.StartMatching(TripTestBuilders.ActionMoment);

        Assert.False(outcome.IsSuccess);
        Assert.Equal(TripErrorCode.IllegalTransition, outcome.ErrorCode);
        Assert.Equal(TripState.Matching, trip.State);
        Assert.Equal(2, trip.CurrentMatchingRound);
        Assert.Equal(version, trip.Version);
    }

    [Theory]
    [InlineData(TripState.DriverReserved)]
    [InlineData(TripState.DriverAccepted)]
    [InlineData(TripState.ReadyToStart)]
    [InlineData(TripState.InProgress)]
    public void StartMatching_FromAssignedState_IsRejected(TripState state)
    {
        var trip = TripTestBuilders.AtState(state);
        var version = trip.Version;

        var outcome = trip.StartMatching(TripTestBuilders.ActionMoment);

        Assert.False(outcome.IsSuccess);
        Assert.Equal(TripErrorCode.IllegalTransition, outcome.ErrorCode);
        Assert.Equal(state, trip.State);
        Assert.Equal(version, trip.Version);
    }

    [Fact]
    public void StartMatching_WithDefaultTimestamp_Throws()
    {
        var trip = TripTestBuilders.NewTrip();

        Assert.Throws<ArgumentException>(() => trip.StartMatching(default));
        Assert.Equal(TripState.Created, trip.State);
        Assert.Equal(0, trip.CurrentMatchingRound);
    }
}
