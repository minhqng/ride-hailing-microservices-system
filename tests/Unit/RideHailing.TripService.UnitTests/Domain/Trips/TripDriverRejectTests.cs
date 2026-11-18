// Driver Reject domain proofs.
//
// The rejection is a real terminal transition, never a validation-only
// operation: the exact reservation identity survives in durable cleanup
// intents while the current assignment is cleared so MATCH_FAILED keeps its
// canonical unassigned shape. Idempotent replay is witnessed by the exact
// release intent; every other identity/state is rejected without mutation.

using RideHailing.TripService.Domain.Trips;

namespace RideHailing.TripService.UnitTests.Domain.Trips;

public sealed class TripDriverRejectTests
{
    private static Trip Reserved(int round = 1) =>
        TripTestBuilders.AtState(TripState.DriverReserved, currentRound: round);

    [Fact]
    public void Reject_CurrentAssignment_RecordsExactReleaseAndCancellationOnce()
    {
        var trip = Reserved();

        var outcome = trip.RecordDriverRejected(
            TripTestBuilders.DriverId, TripTestBuilders.ReservationId, TripTestBuilders.ActionMoment);

        Assert.True(outcome.IsSuccess);
        Assert.False(outcome.IsReplay);
        Assert.Equal(TripFailureReason.DriverRejected, trip.FailureReason);
        Assert.Equal(TripState.MatchFailed, trip.State);
        Assert.Null(trip.Assignment);
        Assert.Equal(1, trip.CurrentMatchingRound);
        Assert.Equal(2, trip.CleanupIntents.Count);
        Assert.Single(trip.CleanupIntents, i => i.Kind == TripCleanupKind.CancelMatching);
        var release = Assert.Single(trip.CleanupIntents, i => i.Kind == TripCleanupKind.ReleaseReservation);
        Assert.Equal(TripTestBuilders.DriverId, release.DriverId);
        Assert.Equal(TripTestBuilders.ReservationId, release.ReservationId);
        Assert.Equal(1, release.MatchingRound);
        Assert.Null(release.CompletedAt);
    }

    [Fact]
    public void Reject_RepeatedExactIdentity_IsReplayWithoutSecondObligation()
    {
        var trip = Reserved();
        trip.RecordDriverRejected(
            TripTestBuilders.DriverId, TripTestBuilders.ReservationId, TripTestBuilders.ActionMoment);
        var version = trip.Version;

        var replay = trip.RecordDriverRejected(
            TripTestBuilders.DriverId, TripTestBuilders.ReservationId, TripTestBuilders.ActionMoment);

        Assert.True(replay.IsSuccess);
        Assert.True(replay.IsReplay);
        Assert.Equal(2, trip.CleanupIntents.Count);
        Assert.Equal(version, trip.Version);
        Assert.Equal(1, trip.CurrentMatchingRound);
    }

    [Fact]
    public void Reject_AfterReplayOfDifferentReservation_IsTerminalAndAddsNothing()
    {
        // A stale/foreign reservation on an already rejected Trip is not a
        // replay witness: it cannot reopen the Trip nor add a second release.
        var trip = Reserved();
        trip.RecordDriverRejected(
            TripTestBuilders.DriverId, TripTestBuilders.ReservationId, TripTestBuilders.ActionMoment);

        var outcome = trip.RecordDriverRejected(
            TripTestBuilders.DriverId, TripTestBuilders.OtherReservationId, TripTestBuilders.ActionMoment);

        Assert.False(outcome.IsSuccess);
        Assert.Equal(TripErrorCode.TerminalState, outcome.ErrorCode);
        Assert.Equal(2, trip.CleanupIntents.Count);
    }

    [Fact]
    public void Reject_WrongDriver_IsIdentityMismatchWithoutMutation()
    {
        var trip = Reserved();
        var version = trip.Version;

        var outcome = trip.RecordDriverRejected(
            TripTestBuilders.OtherDriverId, TripTestBuilders.ReservationId, TripTestBuilders.ActionMoment);

        Assert.False(outcome.IsSuccess);
        Assert.Equal(TripErrorCode.AssignmentIdentityMismatch, outcome.ErrorCode);
        Assert.Equal(TripState.DriverReserved, trip.State);
        Assert.NotNull(trip.Assignment);
        Assert.Empty(trip.CleanupIntents);
        Assert.Equal(version, trip.Version);
    }

    [Fact]
    public void Reject_StaleReservationFromOlderGeneration_IsRejectedWithoutCleanup()
    {
        // Current generation is 2; the supplied reservation belongs to the
        // prior attempt. Exact assignment identity must reject it, and the
        // current reservation must not be released by a stale request.
        var trip = Trip.Reconstitute(
            TripTestBuilders.TripId,
            TripTestBuilders.RiderId,
            TripTestBuilders.Location(),
            TripTestBuilders.Location(10.776889, 106.700806),
            TripTestBuilders.Fare(),
            TripState.DriverReserved,
            2,
            new TripAssignment(TripTestBuilders.DriverId, TripTestBuilders.ReservationId, 2),
            TripTestBuilders.CreatedMoment,
            TripTestBuilders.CreatedMoment,
            7);

        var outcome = trip.RecordDriverRejected(
            TripTestBuilders.DriverId, TripTestBuilders.OtherReservationId, TripTestBuilders.ActionMoment);

        Assert.False(outcome.IsSuccess);
        Assert.Equal(TripErrorCode.AssignmentIdentityMismatch, outcome.ErrorCode);
        Assert.Equal(TripState.DriverReserved, trip.State);
        Assert.Equal(2, trip.Assignment!.MatchingRound);
        Assert.Empty(trip.CleanupIntents);
    }

    [Fact]
    public void Reject_EmptyIdentity_IsValidationFailureWithoutMutation()
    {
        var trip = Reserved();

        var outcome = trip.RecordDriverRejected(Guid.Empty, Guid.Empty, TripTestBuilders.ActionMoment);

        Assert.False(outcome.IsSuccess);
        Assert.Equal(TripErrorCode.AssignmentRequired, outcome.ErrorCode);
        Assert.Equal(TripState.DriverReserved, trip.State);
        Assert.Empty(trip.CleanupIntents);
    }

    [Fact]
    public void Reject_RejectsMissingTimestamp()
    {
        var trip = Reserved();

        Assert.Throws<ArgumentException>(() =>
            trip.RecordDriverRejected(
                TripTestBuilders.DriverId, TripTestBuilders.ReservationId, default));
    }

    [Theory]
    [InlineData(TripState.DriverAccepted)]
    [InlineData(TripState.ReadyToStart)]
    [InlineData(TripState.InProgress)]
    public void Reject_AfterAcceptanceWindow_IsIllegalAndStagesNoPayment(TripState state)
    {
        var trip = TripTestBuilders.AtState(state);

        var outcome = trip.RecordDriverRejected(
            TripTestBuilders.DriverId, TripTestBuilders.ReservationId, TripTestBuilders.ActionMoment);

        Assert.False(outcome.IsSuccess);
        Assert.Equal(TripErrorCode.IllegalTransition, outcome.ErrorCode);
        Assert.Equal(state, trip.State);
        Assert.NotNull(trip.Assignment);
        Assert.Equal(TripCompensation.None, outcome.Compensation);
        Assert.Empty(trip.CleanupIntents);
    }

    [Fact]
    public void RejectedTrip_CannotBeReopenedByLateReservationOrAcceptance()
    {
        var trip = Reserved();
        trip.RecordDriverRejected(
            TripTestBuilders.DriverId, TripTestBuilders.ReservationId, TripTestBuilders.ActionMoment);
        var version = trip.Version;

        var lateReservation = trip.ApplyDriverReserved(
            TripTestBuilders.OtherDriverId, TripTestBuilders.OtherReservationId, 1, TripTestBuilders.ActionMoment);
        var lateAcceptance = trip.RecordDriverAccepted(
            TripTestBuilders.DriverId, TripTestBuilders.ReservationId, TripTestBuilders.ActionMoment);
        var startMatching = trip.StartMatching(TripTestBuilders.ActionMoment);

        Assert.False(lateReservation.IsSuccess);
        Assert.Equal(TripErrorCode.TerminalState, lateReservation.ErrorCode);
        Assert.False(lateAcceptance.IsSuccess);
        Assert.Equal(TripErrorCode.TerminalState, lateAcceptance.ErrorCode);
        Assert.False(startMatching.IsSuccess);
        Assert.Equal(TripErrorCode.TerminalState, startMatching.ErrorCode);
        Assert.Equal(TripState.MatchFailed, trip.State);
        Assert.Equal(TripFailureReason.DriverRejected, trip.FailureReason);
        Assert.Null(trip.Assignment);
        Assert.Equal(version, trip.Version);
    }

    [Fact]
    public void NoDriverMatchFailed_RecordsNoDriverReason()
    {
        var trip = TripTestBuilders.MatchingTrip();

        var outcome = trip.MarkMatchingFailed(1, TripTestBuilders.ActionMoment);

        Assert.True(outcome.IsSuccess);
        Assert.Equal(TripState.MatchFailed, trip.State);
        Assert.Equal(TripFailureReason.NoDriverAvailable, trip.FailureReason);
        Assert.Empty(trip.CleanupIntents);
    }

    [Fact]
    public void HistoricalMatchFailedWithoutReason_ReconstitutesAndStaysTerminal()
    {
        var trip = Trip.Reconstitute(
            TripTestBuilders.TripId,
            TripTestBuilders.RiderId,
            TripTestBuilders.Location(),
            TripTestBuilders.Location(10.776889, 106.700806),
            TripTestBuilders.Fare(),
            TripState.MatchFailed,
            1,
            null,
            TripTestBuilders.CreatedMoment,
            TripTestBuilders.CreatedMoment,
            3);

        Assert.Null(trip.FailureReason);

        var outcome = trip.RecordDriverRejected(
            TripTestBuilders.DriverId, TripTestBuilders.ReservationId, TripTestBuilders.ActionMoment);

        Assert.False(outcome.IsSuccess);
        Assert.Equal(TripErrorCode.TerminalState, outcome.ErrorCode);
    }

    [Fact]
    public void FailureReasonOutsideMatchFailed_IsStructuralCorruption()
    {
        Assert.Throws<ArgumentException>(() => Trip.Reconstitute(
            TripTestBuilders.TripId,
            TripTestBuilders.RiderId,
            TripTestBuilders.Location(),
            TripTestBuilders.Location(10.776889, 106.700806),
            TripTestBuilders.Fare(),
            TripState.Cancelled,
            1,
            new TripAssignment(TripTestBuilders.DriverId, TripTestBuilders.ReservationId, 1),
            TripTestBuilders.CreatedMoment,
            TripTestBuilders.CreatedMoment,
            3,
            TripFailureReason.DriverRejected));
    }
}
