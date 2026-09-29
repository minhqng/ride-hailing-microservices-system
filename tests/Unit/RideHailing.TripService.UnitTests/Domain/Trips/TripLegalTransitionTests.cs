// Table-driven legal-transition suite: only the deterministic W3 domain
// edges actually implemented. StartMatching, DriverRejected policy and the
// unfrozen CREATED / READY_TO_START cancellation branches are deliberately
// absent here.

using RideHailing.TripService.Domain.Trips;

namespace RideHailing.TripService.UnitTests.Domain.Trips;

public sealed class TripLegalTransitionTests
{
    public static TheoryData<string, TripState, bool, Func<Trip, TripOutcome>, TripState, Guid?, Guid?, int> LegalCases() => new()
    {
        {
            "MatchingAppliesDriverReserved", TripState.Matching, false,
            t => t.ApplyDriverReserved(TripTestBuilders.DriverId, TripTestBuilders.ReservationId, 1),
            TripState.DriverReserved, TripTestBuilders.DriverId, TripTestBuilders.ReservationId, 1
        },
        {
            "DriverReservedRecordsDriverAccepted", TripState.DriverReserved, true,
            t => t.RecordDriverAccepted(TripTestBuilders.DriverId, TripTestBuilders.ReservationId),
            TripState.DriverAccepted, TripTestBuilders.DriverId, TripTestBuilders.ReservationId, 1
        },
        {
            "DriverAcceptedMarksPaymentAuthorized", TripState.DriverAccepted, true,
            t => t.MarkPaymentAuthorized(),
            TripState.ReadyToStart, TripTestBuilders.DriverId, TripTestBuilders.ReservationId, 1
        },
        {
            "DriverAcceptedMarksPaymentFailed", TripState.DriverAccepted, true,
            t => t.MarkPaymentFailed(),
            TripState.Cancelled, TripTestBuilders.DriverId, TripTestBuilders.ReservationId, 1
        },
        {
            "MatchingMarksMatchingFailed", TripState.Matching, false,
            t => t.MarkMatchingFailed(),
            TripState.MatchFailed, null, null, 0
        },
        {
            "MatchingCancels", TripState.Matching, false,
            t => t.Cancel(),
            TripState.Cancelled, null, null, 0
        },
        {
            "DriverReservedCancelsPreservingAssignment", TripState.DriverReserved, true,
            t => t.Cancel(),
            TripState.Cancelled, TripTestBuilders.DriverId, TripTestBuilders.ReservationId, 1
        },
        {
            "DriverAcceptedCancelsPreservingAssignment", TripState.DriverAccepted, true,
            t => t.Cancel(),
            TripState.Cancelled, TripTestBuilders.DriverId, TripTestBuilders.ReservationId, 1
        },
        {
            "ReadyToStartStarts", TripState.ReadyToStart, true,
            t => t.Start(TripTestBuilders.DriverId, TripTestBuilders.ReservationId),
            TripState.InProgress, TripTestBuilders.DriverId, TripTestBuilders.ReservationId, 1
        },
        {
            "InProgressCompletes", TripState.InProgress, true,
            t => t.Complete(TripTestBuilders.DriverId, TripTestBuilders.ReservationId),
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
        Assert.Equal(expected, trip.State);
        Assert.Equal(version + 1, trip.Version);

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
        }
    }

    [Fact]
    public void Trip_AssignmentRequiresReservationIdentity()
    {
        var trip = TripTestBuilders.MatchingTrip();
        var version = trip.Version;

        var outcome = trip.ApplyDriverReserved(TripTestBuilders.DriverId, Guid.Empty, 1);

        Assert.False(outcome.IsSuccess);
        Assert.Equal(TripErrorCode.AssignmentRequired, outcome.ErrorCode);
        Assert.Equal(TripState.Matching, trip.State);
        Assert.Null(trip.Assignment);
        Assert.Equal(version, trip.Version);
    }
}
