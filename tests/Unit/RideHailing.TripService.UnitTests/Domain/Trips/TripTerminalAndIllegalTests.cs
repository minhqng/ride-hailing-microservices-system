// Illegal/terminal suite: proves DSI-010 (terminal monotonicity) against
// every implemented active operation, plus the assignment and completion
// guards. Cancellation from CREATED and READY_TO_START is intentionally
// untested here: that product policy is not frozen, so no test may lock in
// an allow or reject semantic for those two branches.

using RideHailing.TripService.Domain.Trips;

namespace RideHailing.TripService.UnitTests.Domain.Trips;

public sealed class TripTerminalAndIllegalTests
{
    private static readonly TripState[] TerminalStates =
        [TripState.Completed, TripState.Cancelled, TripState.MatchFailed];

    private static readonly (string Name, Func<Trip, TripOutcome> Act)[] ActiveOperations =
    [
        ("ApplyDriverReserved", t => t.ApplyDriverReserved(
            TripTestBuilders.DriverId, TripTestBuilders.ReservationId, 1)),
        ("RecordDriverAccepted", t => t.RecordDriverAccepted(
            TripTestBuilders.DriverId, TripTestBuilders.ReservationId)),
        ("MarkPaymentAuthorized", t => t.MarkPaymentAuthorized()),
        ("MarkPaymentFailed", t => t.MarkPaymentFailed()),
        ("MarkMatchingFailed", t => t.MarkMatchingFailed()),
        ("Start", t => t.Start(TripTestBuilders.DriverId, TripTestBuilders.ReservationId)),
        ("Complete", t => t.Complete(TripTestBuilders.DriverId, TripTestBuilders.ReservationId)),
        ("Cancel", t => t.Cancel()),
    ];

    public static IEnumerable<object[]> TerminalCases()
    {
        foreach (var terminal in TerminalStates)
        {
            foreach (var (name, act) in ActiveOperations)
            {
                yield return new object[] { terminal, name, act };
            }
        }
    }

    [Theory]
    [MemberData(nameof(TerminalCases))]
    public void TerminalState_RejectsEveryActiveOperationWithoutMutation(
        TripState terminal, string operation, Func<Trip, TripOutcome> act)
    {
        var trip = TripTestBuilders.AtState(terminal);
        var version = trip.Version;
        var assignment = trip.Assignment;

        var outcome = act(trip);

        Assert.False(outcome.IsSuccess, $"{terminal.ToCanonicalName()} + {operation}: expected rejection.");
        Assert.Equal(TripErrorCode.TerminalState, outcome.ErrorCode);
        Assert.Equal(terminal, trip.State);
        Assert.Same(assignment, trip.Assignment);
        Assert.Equal(version, trip.Version);
    }

    [Fact]
    public void Trip_TerminalCompleted_RejectsCancel()
    {
        var trip = TripTestBuilders.AtState(TripState.Completed);
        var version = trip.Version;

        var outcome = trip.Cancel();

        Assert.False(outcome.IsSuccess);
        Assert.Equal(TripErrorCode.TerminalState, outcome.ErrorCode);
        Assert.Equal(TripState.Completed, trip.State);
        Assert.Equal(version, trip.Version);
    }

    [Fact]
    public void Trip_Cancelled_RejectsDriverReservedAsCurrent()
    {
        var trip = TripTestBuilders.AtState(TripState.Cancelled);
        var version = trip.Version;
        var assignment = trip.Assignment;

        var outcome = trip.ApplyDriverReserved(
            TripTestBuilders.OtherDriverId, TripTestBuilders.OtherReservationId, 2);

        Assert.False(outcome.IsSuccess);
        Assert.Equal(TripErrorCode.TerminalState, outcome.ErrorCode);
        Assert.Equal(TripState.Cancelled, trip.State);
        Assert.Same(assignment, trip.Assignment);
        Assert.Equal(version, trip.Version);
    }

    [Fact]
    public void Trip_MatchFailed_RejectsDriverAccepted()
    {
        var trip = TripTestBuilders.AtState(TripState.MatchFailed, withAssignment: false);
        var version = trip.Version;

        var outcome = trip.RecordDriverAccepted(
            TripTestBuilders.DriverId, TripTestBuilders.ReservationId);

        Assert.False(outcome.IsSuccess);
        Assert.Equal(TripErrorCode.TerminalState, outcome.ErrorCode);
        Assert.Equal(TripState.MatchFailed, trip.State);
        Assert.Null(trip.Assignment);
        Assert.Equal(version, trip.Version);
    }

    [Theory]
    [InlineData(TripState.Created)]
    [InlineData(TripState.Matching)]
    [InlineData(TripState.DriverReserved)]
    [InlineData(TripState.DriverAccepted)]
    [InlineData(TripState.ReadyToStart)]
    public void Trip_Complete_OnlyFromInProgress_RejectsOtherStates(TripState state)
    {
        var trip = TripTestBuilders.AtState(state, withAssignment: true);
        var version = trip.Version;

        var outcome = trip.Complete(TripTestBuilders.DriverId, TripTestBuilders.ReservationId);

        Assert.False(outcome.IsSuccess);
        Assert.Equal(state, trip.State);
        Assert.Equal(version, trip.Version);
    }

    [Fact]
    public void Trip_Complete_OnlyFromInProgress_AcceptsInProgress()
    {
        var trip = TripTestBuilders.AtState(TripState.InProgress);

        var outcome = trip.Complete(TripTestBuilders.DriverId, TripTestBuilders.ReservationId);

        Assert.True(outcome.IsSuccess);
        Assert.Equal(TripState.Completed, trip.State);
    }

    [Theory]
    [InlineData(TripState.Created)]
    [InlineData(TripState.DriverReserved)]
    [InlineData(TripState.DriverAccepted)]
    [InlineData(TripState.ReadyToStart)]
    [InlineData(TripState.InProgress)]
    public void ApplyDriverReserved_FromIllegalState_IsRejected(TripState state)
    {
        var trip = TripTestBuilders.AtState(state);
        var version = trip.Version;

        var outcome = trip.ApplyDriverReserved(
            TripTestBuilders.OtherDriverId, TripTestBuilders.OtherReservationId, 9);

        Assert.False(outcome.IsSuccess);
        Assert.Equal(TripErrorCode.IllegalTransition, outcome.ErrorCode);
        Assert.Equal(state, trip.State);
        Assert.Equal(version, trip.Version);
    }

    [Fact]
    public void SecondCurrentAssignment_IsRejected()
    {
        var trip = TripTestBuilders.AtState(TripState.DriverReserved);
        var assignment = trip.Assignment;
        var version = trip.Version;

        var outcome = trip.ApplyDriverReserved(
            TripTestBuilders.OtherDriverId, TripTestBuilders.OtherReservationId, 2);

        Assert.False(outcome.IsSuccess);
        Assert.Equal(TripState.DriverReserved, trip.State);
        Assert.Same(assignment, trip.Assignment);
        Assert.Equal(version, trip.Version);
    }

    [Fact]
    public void RecordDriverAccepted_WithMismatchedReservationIdentity_IsRejected()
    {
        var trip = TripTestBuilders.AtState(TripState.DriverReserved);
        var version = trip.Version;

        var outcome = trip.RecordDriverAccepted(
            TripTestBuilders.DriverId, TripTestBuilders.OtherReservationId);

        Assert.False(outcome.IsSuccess);
        Assert.Equal(TripErrorCode.AssignmentIdentityMismatch, outcome.ErrorCode);
        Assert.Equal(TripState.DriverReserved, trip.State);
        Assert.Equal(version, trip.Version);
    }

    [Fact]
    public void ReadyToStart_WithoutAssignment_IsStructurallyRejected()
    {
        // DSI-011 is enforced at reconstitution: READY_TO_START inherits the
        // accepted assignment, so persisting it without one is incoherent.
        // The Start null-assignment guard remains as defense-in-depth.
        Assert.Throws<ArgumentException>(() => TripTestBuilders.AtState(TripState.ReadyToStart, withAssignment: false));
    }

    [Fact]
    public void Start_WithMismatchedAssignment_IsRejected()
    {
        var trip = TripTestBuilders.AtState(TripState.ReadyToStart);
        var version = trip.Version;

        var outcome = trip.Start(TripTestBuilders.OtherDriverId, TripTestBuilders.ReservationId);

        Assert.False(outcome.IsSuccess);
        Assert.Equal(TripErrorCode.AssignmentIdentityMismatch, outcome.ErrorCode);
        Assert.Equal(TripState.ReadyToStart, trip.State);
        Assert.Equal(version, trip.Version);
    }

    [Fact]
    public void Cancel_FromInProgress_IsRejected()
    {
        var trip = TripTestBuilders.AtState(TripState.InProgress);
        var version = trip.Version;

        var outcome = trip.Cancel();

        Assert.False(outcome.IsSuccess);
        Assert.Equal(TripErrorCode.IllegalTransition, outcome.ErrorCode);
        Assert.Equal(TripState.InProgress, trip.State);
        Assert.Equal(version, trip.Version);
    }

    [Fact]
    public void MarkMatchingFailed_WithCurrentAssignmentContradiction_IsRejected()
    {
        var trip = Trip.Reconstitute(
            TripTestBuilders.TripId,
            TripTestBuilders.RiderId,
            TripTestBuilders.Location(),
            TripTestBuilders.Location(),
            TripTestBuilders.Fare(),
            TripState.Matching,
            TripTestBuilders.Assignment(),
            DateTimeOffset.UtcNow,
            3);
        var version = trip.Version;

        var outcome = trip.MarkMatchingFailed();

        Assert.False(outcome.IsSuccess);
        Assert.Equal(TripErrorCode.AssignmentAlreadyPresent, outcome.ErrorCode);
        Assert.Equal(TripState.Matching, trip.State);
        Assert.Equal(version, trip.Version);
    }
}
