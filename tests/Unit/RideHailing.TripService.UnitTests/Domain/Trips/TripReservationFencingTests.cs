// A-W05-02/03 proofs: assignedDriverId/reservationId/matchingRound ownership.
// The Trip accepts only the current round as its assignment, replays the
// exact current assignment without effect, rejects stale generations, and
// rejects conflicting identities — all without partial mutation.

using RideHailing.TripService.Domain.Trips;

namespace RideHailing.TripService.UnitTests.Domain.Trips;

public sealed class TripReservationFencingTests
{
    [Fact]
    public void ApplyDriverReserved_CurrentRound_CommitsCoherentAssignment()
    {
        var trip = TripTestBuilders.MatchingTrip();

        var outcome = trip.ApplyDriverReserved(
            TripTestBuilders.DriverId, TripTestBuilders.ReservationId, 1, TripTestBuilders.ActionMoment);

        Assert.True(outcome.IsSuccess);
        Assert.False(outcome.IsReplay);
        Assert.Equal(TripCompensation.None, outcome.Compensation);
        Assert.Equal(TripState.DriverReserved, trip.State);
        Assert.Equal(TripTestBuilders.DriverId, trip.Assignment!.DriverId);
        Assert.Equal(TripTestBuilders.ReservationId, trip.Assignment.ReservationId);
        Assert.Equal(1, trip.Assignment.MatchingRound);
        Assert.Equal(1, trip.CurrentMatchingRound);
    }

    [Fact]
    public void ApplyDriverReserved_ExactCurrentAssignment_IsIdempotentReplay()
    {
        var trip = TripTestBuilders.AtState(TripState.DriverReserved);
        var version = trip.Version;
        var updatedAt = trip.UpdatedAt;
        var assignment = trip.Assignment;

        var outcome = trip.ApplyDriverReserved(
            TripTestBuilders.DriverId, TripTestBuilders.ReservationId, 1, TripTestBuilders.ActionMoment);

        Assert.True(outcome.IsSuccess);
        Assert.True(outcome.IsReplay);
        Assert.Equal(TripState.DriverReserved, trip.State);
        Assert.Same(assignment, trip.Assignment);
        Assert.Equal(1, trip.CurrentMatchingRound);
        Assert.Equal(version, trip.Version);
        Assert.Equal(updatedAt, trip.UpdatedAt);
    }

    [Theory]
    [InlineData(TripState.DriverAccepted)]
    [InlineData(TripState.ReadyToStart)]
    [InlineData(TripState.InProgress)]
    public void ApplyDriverReserved_ExactCurrentAssignmentAfterAdvance_IsHarmlessReplay(TripState state)
    {
        // A redelivered reservation fact for the still-current assignment is
        // already true: replay, never a second assignment (DSI-008).
        var trip = TripTestBuilders.AtState(state);
        var version = trip.Version;

        var outcome = trip.ApplyDriverReserved(
            TripTestBuilders.DriverId, TripTestBuilders.ReservationId, 1, TripTestBuilders.ActionMoment);

        Assert.True(outcome.IsSuccess);
        Assert.True(outcome.IsReplay);
        Assert.Equal(state, trip.State);
        Assert.Equal(version, trip.Version);
    }

    [Fact]
    public void ApplyDriverReserved_OldRound_IsRejectedAsStale()
    {
        var trip = TripTestBuilders.AtState(TripState.Matching, withAssignment: false, currentRound: 2);
        var version = trip.Version;

        var outcome = trip.ApplyDriverReserved(
            TripTestBuilders.DriverId, TripTestBuilders.ReservationId, 1, TripTestBuilders.ActionMoment);

        Assert.False(outcome.IsSuccess);
        Assert.Equal(TripErrorCode.StaleIdentity, outcome.ErrorCode);
        Assert.Equal(TripState.Matching, trip.State);
        Assert.Null(trip.Assignment);
        Assert.Equal(2, trip.CurrentMatchingRound);
        Assert.Equal(version, trip.Version);
    }

    [Fact]
    public void ApplyDriverReserved_FutureRound_IsRejectedAsStale()
    {
        var trip = TripTestBuilders.MatchingTrip();
        var version = trip.Version;

        var outcome = trip.ApplyDriverReserved(
            TripTestBuilders.DriverId, TripTestBuilders.ReservationId, 2, TripTestBuilders.ActionMoment);

        Assert.False(outcome.IsSuccess);
        Assert.Equal(TripErrorCode.StaleIdentity, outcome.ErrorCode);
        Assert.Equal(TripState.Matching, trip.State);
        Assert.Null(trip.Assignment);
        Assert.Equal(1, trip.CurrentMatchingRound);
        Assert.Equal(version, trip.Version);
    }

    [Fact]
    public void ApplyDriverReserved_OldReservationAfterAdvance_IsRejectedAsStale()
    {
        // The current assignment is R/r1; a reservation result for an older
        // generation cannot overwrite it, even with a different driver.
        var trip = TripTestBuilders.AtState(TripState.DriverReserved);
        var version = trip.Version;
        var assignment = trip.Assignment;

        var outcome = trip.ApplyDriverReserved(
            TripTestBuilders.OtherDriverId, TripTestBuilders.OtherReservationId, 0, TripTestBuilders.ActionMoment);

        Assert.False(outcome.IsSuccess);
        Assert.Equal(TripErrorCode.StaleIdentity, outcome.ErrorCode);
        Assert.Equal(TripState.DriverReserved, trip.State);
        Assert.Same(assignment, trip.Assignment);
        Assert.Equal(version, trip.Version);
    }

    [Fact]
    public void ApplyDriverReserved_DifferentReservationForCurrentRound_IsRejectedAsConflict()
    {
        var trip = TripTestBuilders.AtState(TripState.DriverReserved);
        var version = trip.Version;
        var assignment = trip.Assignment;

        var outcome = trip.ApplyDriverReserved(
            TripTestBuilders.OtherDriverId, TripTestBuilders.OtherReservationId, 1, TripTestBuilders.ActionMoment);

        Assert.False(outcome.IsSuccess);
        Assert.Equal(TripErrorCode.AssignmentIdentityMismatch, outcome.ErrorCode);
        Assert.Equal(TripState.DriverReserved, trip.State);
        Assert.Same(assignment, trip.Assignment);
        Assert.Equal(TripTestBuilders.DriverId, trip.Assignment!.DriverId);
        Assert.Equal(TripTestBuilders.ReservationId, trip.Assignment.ReservationId);
        Assert.Equal(version, trip.Version);
    }

    [Fact]
    public void ApplyDriverReserved_SameDriverDifferentReservation_IsRejectedAsConflict()
    {
        var trip = TripTestBuilders.AtState(TripState.DriverReserved);
        var version = trip.Version;

        var outcome = trip.ApplyDriverReserved(
            TripTestBuilders.DriverId, TripTestBuilders.OtherReservationId, 1, TripTestBuilders.ActionMoment);

        Assert.False(outcome.IsSuccess);
        Assert.Equal(TripErrorCode.AssignmentIdentityMismatch, outcome.ErrorCode);
        Assert.Equal(TripState.DriverReserved, trip.State);
        Assert.Equal(TripTestBuilders.ReservationId, trip.Assignment!.ReservationId);
        Assert.Equal(version, trip.Version);
    }

    [Fact]
    public void ApplyDriverReserved_OnMatchingWithAssignmentContradiction_IsRejected()
    {
        // Incoherent shape (constructible in memory, never persistable):
        // the existing assignment wins over any new claim.
        var trip = Trip.Reconstitute(
            TripTestBuilders.TripId,
            TripTestBuilders.RiderId,
            TripTestBuilders.Location(),
            TripTestBuilders.Location(),
            TripTestBuilders.Fare(),
            TripState.Matching,
            1,
            TripTestBuilders.Assignment(),
            TripTestBuilders.CreatedMoment,
            TripTestBuilders.CreatedMoment,
            3);
        var version = trip.Version;

        var outcome = trip.ApplyDriverReserved(
            TripTestBuilders.OtherDriverId, TripTestBuilders.OtherReservationId, 1, TripTestBuilders.ActionMoment);

        Assert.False(outcome.IsSuccess);
        Assert.Equal(TripErrorCode.AssignmentAlreadyPresent, outcome.ErrorCode);
        Assert.Equal(TripState.Matching, trip.State);
        Assert.Equal(version, trip.Version);
    }

    [Fact]
    public void ApplyDriverReserved_StaleRoundWithAssignmentContradiction_ReportsStaleFirst()
    {
        // Dual condition: the Trip holds an assignment AND the incoming fact
        // is from a superseded generation. Generation validity (DSI-016)
        // precedes same-generation conflict (DSI-008): a stale generation
        // cannot overwrite current state no matter what else holds.
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

        var outcome = trip.ApplyDriverReserved(
            TripTestBuilders.OtherDriverId, TripTestBuilders.OtherReservationId, 1, TripTestBuilders.ActionMoment);

        Assert.False(outcome.IsSuccess);
        Assert.Equal(TripErrorCode.StaleIdentity, outcome.ErrorCode);
        Assert.Equal(TripState.Matching, trip.State);
        Assert.Same(assignment, trip.Assignment);
        Assert.Equal(2, trip.CurrentMatchingRound);
        Assert.Equal(updatedAt, trip.UpdatedAt);
        Assert.Equal(version, trip.Version);
    }
}
