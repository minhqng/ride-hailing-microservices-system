// Rejected-path no-mutation proofs: for every major rejected outcome the
// aggregate's full footprint (state, assignment identities, round,
// timestamps, Version) is snapshotted before the operation and proven
// identical afterwards. A test that checks only the error code while hidden
// state mutates cannot pass this suite.

using RideHailing.TripService.Domain.Trips;

namespace RideHailing.TripService.UnitTests.Domain.Trips;

public sealed class TripNoMutationProofs
{
    private sealed record Snapshot(
        TripState State,
        Guid? DriverId,
        Guid? ReservationId,
        int AssignmentRound,
        int CurrentRound,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt,
        int Version);

    private static Snapshot Take(Trip trip) => new(
        trip.State,
        trip.Assignment?.DriverId,
        trip.Assignment?.ReservationId,
        trip.Assignment?.MatchingRound ?? 0,
        trip.CurrentMatchingRound,
        trip.CreatedAt,
        trip.UpdatedAt,
        trip.Version);

    public static TheoryData<string, Func<Trip, TripOutcome>> RejectedOperations() => new()
    {
        {
            "StartMatchingOnLaterRound",
            t => t.StartMatching(TripTestBuilders.ActionMoment)
        },
        {
            "ApplyDriverReservedStaleRound",
            t => t.ApplyDriverReserved(
                TripTestBuilders.OtherDriverId, TripTestBuilders.OtherReservationId, 9, TripTestBuilders.ActionMoment)
        },
        {
            "ApplyDriverReservedConflictingIdentity",
            t => t.ApplyDriverReserved(
                TripTestBuilders.OtherDriverId, TripTestBuilders.OtherReservationId, 1, TripTestBuilders.ActionMoment)
        },
        {
            "RecordDriverAcceptedStaleReservation",
            t => t.RecordDriverAccepted(
                TripTestBuilders.DriverId, TripTestBuilders.OtherReservationId, TripTestBuilders.ActionMoment)
        },
        {
            "RecordDriverRejectedMismatch",
            t => t.RecordDriverRejected(
                TripTestBuilders.OtherDriverId, TripTestBuilders.ReservationId)
        },
        {
            "MarkPaymentAuthorizedTooEarly",
            t => t.MarkPaymentAuthorized(TripTestBuilders.ActionMoment)
        },
        {
            "MarkMatchingFailedStale",
            t => t.MarkMatchingFailed(9, TripTestBuilders.ActionMoment)
        },
        {
            "StartWithWrongDriver",
            t => t.Start(TripTestBuilders.OtherDriverId, TripTestBuilders.ReservationId, TripTestBuilders.ActionMoment)
        },
        {
            "CompleteFromWrongState",
            t => t.Complete(TripTestBuilders.DriverId, TripTestBuilders.ReservationId, TripTestBuilders.ActionMoment)
        },
    };

    [Theory]
    [MemberData(nameof(RejectedOperations))]
    public void RejectedOperation_LeavesFullFootprintUntouched(string name, Func<Trip, TripOutcome> act)
    {
        // DRIVER_RESERVED with a current assignment is the richest footprint:
        // every rejected family must preserve all of it.
        var trip = TripTestBuilders.AtState(TripState.DriverReserved);
        var before = Take(trip);

        var outcome = act(trip);

        Assert.False(outcome.IsSuccess, $"{name}: expected rejection.");
        Assert.Equal(before, Take(trip));
    }

    [Fact]
    public void RejectedOperation_OnMatchingRow_PreservesRoundAndTimestamps()
    {
        var trip = TripTestBuilders.MatchingTrip();
        var before = Take(trip);

        var outcome = trip.ApplyDriverReserved(
            TripTestBuilders.DriverId, TripTestBuilders.ReservationId, 7, TripTestBuilders.ActionMoment);

        Assert.False(outcome.IsSuccess);
        Assert.Equal(TripErrorCode.StaleIdentity, outcome.ErrorCode);
        Assert.Equal(before, Take(trip));
    }

    [Fact]
    public void ReplayOperation_PreservesFootprint_WhileReportingReplay()
    {
        var trip = TripTestBuilders.AtState(TripState.DriverReserved);
        var before = Take(trip);

        var outcome = trip.ApplyDriverReserved(
            TripTestBuilders.DriverId, TripTestBuilders.ReservationId, 1, TripTestBuilders.ActionMoment);

        Assert.True(outcome.IsSuccess);
        Assert.True(outcome.IsReplay);
        Assert.Equal(before, Take(trip));
    }
}
