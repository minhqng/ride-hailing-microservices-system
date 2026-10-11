// Table-driven legal-transition suite: every deterministic W5 domain edge
// that commits. Replay, stale, conflict and illegal representatives live in
// the dedicated fencing/matrix suites; this suite proves each applied edge
// commits the expected state, assignment, round, timestamp and Version delta.

using RideHailing.TripService.Domain.Trips;

namespace RideHailing.TripService.UnitTests.Domain.Trips;

public sealed class TripLegalTransitionTests
{
    public static TheoryData<string, TripState, bool, Func<Trip, TripOutcome>, TripState, Guid?, Guid?, int> LegalCases() => new()
    {
        {
            "CreatedStartsMatching", TripState.Created, false,
            t => t.StartMatching(TripTestBuilders.ActionMoment),
            TripState.Matching, null, null, 0
        },
        {
            "MatchingAppliesDriverReserved", TripState.Matching, false,
            t => t.ApplyDriverReserved(TripTestBuilders.DriverId, TripTestBuilders.ReservationId, 1, TripTestBuilders.ActionMoment),
            TripState.DriverReserved, TripTestBuilders.DriverId, TripTestBuilders.ReservationId, 1
        },
        {
            "DriverReservedRecordsDriverAccepted", TripState.DriverReserved, true,
            t => t.RecordDriverAccepted(TripTestBuilders.DriverId, TripTestBuilders.ReservationId, TripTestBuilders.ActionMoment),
            TripState.DriverAccepted, TripTestBuilders.DriverId, TripTestBuilders.ReservationId, 1
        },
        {
            "DriverAcceptedMarksPaymentAuthorized", TripState.DriverAccepted, true,
            t => t.MarkPaymentAuthorized(TripTestBuilders.ActionMoment),
            TripState.ReadyToStart, TripTestBuilders.DriverId, TripTestBuilders.ReservationId, 1
        },
        {
            "DriverAcceptedMarksPaymentFailed", TripState.DriverAccepted, true,
            t => t.MarkPaymentFailed(TripTestBuilders.ActionMoment),
            TripState.Cancelled, TripTestBuilders.DriverId, TripTestBuilders.ReservationId, 1
        },
        {
            "MatchingMarksMatchingFailed", TripState.Matching, false,
            t => t.MarkMatchingFailed(1, TripTestBuilders.ActionMoment),
            TripState.MatchFailed, null, null, 0
        },
        {
            "MatchingCancels", TripState.Matching, false,
            t => t.Cancel(TripTestBuilders.ActionMoment),
            TripState.Cancelled, null, null, 0
        },
        {
            "DriverReservedCancelsPreservingAssignment", TripState.DriverReserved, true,
            t => t.Cancel(TripTestBuilders.ActionMoment),
            TripState.Cancelled, TripTestBuilders.DriverId, TripTestBuilders.ReservationId, 1
        },
        {
            "DriverAcceptedCancelsPreservingAssignment", TripState.DriverAccepted, true,
            t => t.Cancel(TripTestBuilders.ActionMoment),
            TripState.Cancelled, TripTestBuilders.DriverId, TripTestBuilders.ReservationId, 1
        },
        {
            "ReadyToStartStarts", TripState.ReadyToStart, true,
            t => t.Start(TripTestBuilders.DriverId, TripTestBuilders.ReservationId, TripTestBuilders.ActionMoment),
            TripState.InProgress, TripTestBuilders.DriverId, TripTestBuilders.ReservationId, 1
        },
        {
            "InProgressCompletes", TripState.InProgress, true,
            t => t.Complete(TripTestBuilders.DriverId, TripTestBuilders.ReservationId, TripTestBuilders.ActionMoment),
            TripState.Completed, TripTestBuilders.DriverId, TripTestBuilders.ReservationId, 1
        },
    };

    [Theory]
    [MemberData(nameof(LegalCases))]
    public void LegalTransition_CommitsExpectedStateAndAssignment(
        string name,
        TripState initial,
        bool withAssignment,
        Func<Trip, TripOutcome> act,
        TripState expected,
        Guid? expectedDriverId,
        Guid? expectedReservationId,
        int expectedRound)
    {
        var trip = TripTestBuilders.AtState(initial, withAssignment);
        var version = trip.Version;

        var outcome = act(trip);

        Assert.True(outcome.IsSuccess, $"{name}: expected success but got {outcome.ErrorCode}: {outcome.Message}");
        Assert.False(outcome.IsReplay, $"{name}: applied transition must not report replay.");
        Assert.Equal(expected, trip.State);
        Assert.Equal(version + 1, trip.Version);
        Assert.Equal(TripTestBuilders.ActionMoment, trip.UpdatedAt);

        if (expectedDriverId is null)
        {
            Assert.Null(trip.Assignment);
        }
        else
        {
            Assert.NotNull(trip.Assignment);
            Assert.Equal(expectedDriverId.Value, trip.Assignment.DriverId);
            Assert.Equal(expectedReservationId!.Value, trip.Assignment.ReservationId);
            Assert.Equal(expectedRound, trip.Assignment.MatchingRound);
            Assert.Equal(expectedRound, trip.CurrentMatchingRound);
        }
    }

    [Fact]
    public void Trip_AssignmentRequiresReservationIdentity()
    {
        var trip = TripTestBuilders.MatchingTrip();
        var version = trip.Version;

        var outcome = trip.ApplyDriverReserved(TripTestBuilders.DriverId, Guid.Empty, 1, TripTestBuilders.ActionMoment);

        Assert.False(outcome.IsSuccess);
        Assert.Equal(TripErrorCode.AssignmentRequired, outcome.ErrorCode);
        Assert.Equal(TripState.Matching, trip.State);
        Assert.Null(trip.Assignment);
        Assert.Equal(version, trip.Version);
    }

    [Fact]
    public void Created_StartsMatching_WithRoundOne_AndTimestamp()
    {
        var trip = TripTestBuilders.NewTrip();
        var version = trip.Version;

        var outcome = trip.StartMatching(TripTestBuilders.ActionMoment);

        Assert.True(outcome.IsSuccess);
        Assert.False(outcome.IsReplay);
        Assert.Equal(TripErrorCode.None, outcome.ErrorCode);
        Assert.Equal(TripCompensation.None, outcome.Compensation);
        Assert.Equal(TripState.Matching, trip.State);
        Assert.Equal(1, trip.CurrentMatchingRound);
        Assert.Null(trip.Assignment);
        Assert.Equal(version + 1, trip.Version);
        Assert.Equal(TripTestBuilders.ActionMoment, trip.UpdatedAt);
    }
}
