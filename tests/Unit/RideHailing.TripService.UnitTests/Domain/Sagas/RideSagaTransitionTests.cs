// RideSaga guarded-transition proofs (A-W10-01..08, Doc 05A §18).
//
// ORACLE: the saga advances ONLY on the authoritative fact for its current
// step, exactly once per logical fact (DSI-034), never from an uncertain
// outcome (DECLINED ≠ UNKNOWN), never regressing a terminal workflow
// (DSL-005) and never completing compensation while an obligation is still
// outstanding (DSI-033/DSL-002). Rejected and replay operations leave
// StepVersion and timestamps untouched.

using RideHailing.TripService.Domain.Sagas;

namespace RideHailing.TripService.UnitTests.Domain.Sagas;

public sealed class RideSagaTransitionTests
{
    private static readonly Guid SagaId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
    private static readonly Guid TripId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid ReservationId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly DateTimeOffset StartMoment = new(2026, 10, 25, 1, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Later = new(2026, 10, 25, 1, 5, 0, TimeSpan.Zero);

    private static RideSaga NewSaga() =>
        RideSaga.Start(SagaId, TripId, ReservationId, RideSagaOperationKeys.Authorize(TripId), StartMoment);

    private static RideSaga Authorized()
    {
        var saga = NewSaga();
        Assert.True(saga.RecordAuthorizeSucceeded(StartMoment).IsSuccess);
        return saga;
    }

    private static RideSaga CapturePending()
    {
        var saga = Authorized();
        Assert.True(saga.RecordRideCompleted(RideSagaOperationKeys.Capture(TripId), StartMoment).IsSuccess);
        return saga;
    }

    // ------------------------------------------------------------- creation

    [Fact]
    public void Start_CreatesActiveAuthorizePending_WithFrozenAuthorizeLineage()
    {
        var saga = NewSaga();

        Assert.Equal(RideSagaStatus.Active, saga.Status);
        Assert.Equal(RideSagaStep.AuthorizePending, saga.CurrentStep);
        Assert.Equal(1, saga.StepVersion);
        Assert.Equal(ReservationId, saga.ReservationId);
        Assert.Equal($"{TripId:D}:authorize:v1", saga.AuthorizeOperationKey);
        Assert.Null(saga.CaptureOperationKey);
        Assert.Null(saga.VoidOperationKey);
        // MayStartRide is false until authorization is authoritative (DSI-026).
        Assert.False(saga.MayStartRide);
    }

    [Fact]
    public void Start_RejectsAForeignKeyLineage()
    {
        Assert.Throws<ArgumentException>(() =>
            RideSaga.Start(SagaId, TripId, ReservationId, $"{TripId:D}:authorize:v2", StartMoment));
        Assert.Throws<ArgumentException>(() =>
            RideSaga.Start(Guid.Empty, TripId, ReservationId, RideSagaOperationKeys.Authorize(TripId), StartMoment));
    }

    // --------------------------------------------------- authorize outcomes

    [Fact]
    public void AuthorizeSucceeded_AdvancesToAuthorized_ExactlyOnce()
    {
        var saga = NewSaga();

        var first = saga.RecordAuthorizeSucceeded(Later);
        var duplicate = saga.RecordAuthorizeSucceeded(Later.AddSeconds(1));

        Assert.True(first.IsSuccess);
        Assert.False(first.IsReplay);
        Assert.Equal(RideSagaStep.Authorized, saga.CurrentStep);
        Assert.True(saga.MayStartRide);
        // The duplicate payment success is a replay: no second advance and no
        // StepVersion bump (one logical payment effect per step, DSI-034).
        Assert.True(duplicate.IsSuccess);
        Assert.True(duplicate.IsReplay);
        Assert.Equal(2, saga.StepVersion);
    }

    [Fact]
    public void AuthorizeSucceeded_FromAuthorizePendingOnly_LaterStepsRejectAsStale()
    {
        var saga = CapturePending();
        var version = saga.StepVersion;

        var outcome = saga.RecordAuthorizeSucceeded(Later);

        Assert.False(outcome.IsSuccess);
        Assert.Equal(RideSagaErrorCode.IllegalTransition, outcome.ErrorCode);
        Assert.Equal(RideSagaStep.CapturePending, saga.CurrentStep);
        Assert.Equal(version, saga.StepVersion);
    }

    [Fact]
    public void AuthorizeDeclined_MovesToCompensatingReleasePending()
    {
        var saga = NewSaga();

        var outcome = saga.RecordAuthorizeDeclined("DECLINED", Later);

        Assert.True(outcome.IsSuccess);
        Assert.Equal(RideSagaStatus.Compensating, saga.Status);
        Assert.Equal(RideSagaStep.ReleasePending, saga.CurrentStep);
        Assert.Equal("DECLINED", saga.LastErrorCode);
        Assert.False(saga.MayStartRide);
        // Duplicate terminal failure replays without another transition.
        var duplicate = saga.RecordAuthorizeDeclined("DECLINED", Later.AddSeconds(1));
        Assert.True(duplicate.IsReplay);
    }

    [Fact]
    public void AuthorizeOutcomeUncertain_NeverCompensates_RecordsObservationOnce()
    {
        var saga = NewSaga();

        var first = saga.RecordAuthorizeOutcomeUncertain("TIMEOUT_AFTER_EFFECT", Later);
        var duplicate = saga.RecordAuthorizeOutcomeUncertain("TIMEOUT_AFTER_EFFECT", Later.AddSeconds(1));

        Assert.True(first.IsSuccess);
        Assert.False(first.IsReplay);
        // UNKNOWN must not be mistaken for a terminal failure: the step stays
        // pending on the SAME operationKey for reconciliation (Doc 02 §15.5).
        Assert.Equal(RideSagaStep.AuthorizePending, saga.CurrentStep);
        Assert.Equal(RideSagaStatus.Active, saga.Status);
        Assert.Equal("TIMEOUT_AFTER_EFFECT", saga.LastErrorCode);
        Assert.True(duplicate.IsReplay);
        Assert.Equal(2, saga.StepVersion);
    }

    // -------------------------------------------------------- cancel races

    [Fact]
    public void CancellationDuringAuthorize_FlipsCompensating_KeepsAuthorizePending()
    {
        var saga = NewSaga();

        var outcome = saga.RecordCancellationDuringAuthorize(Later);

        Assert.True(outcome.IsSuccess);
        Assert.Equal(RideSagaStatus.Compensating, saga.Status);
        Assert.Equal(RideSagaStep.AuthorizePending, saga.CurrentStep);
        // Idempotent for a repeated cancellation fact.
        Assert.True(saga.RecordCancellationDuringAuthorize(Later).IsReplay);
    }

    [Fact]
    public void AuthorizeSucceededAfterCancel_RequiresExactVoid_EvenFromCompensation()
    {
        var saga = NewSaga();
        saga.RecordCancellationDuringAuthorize(Later);

        var wrongKey = saga.RecordAuthorizeSucceededAfterCancellation($"{TripId:D}:void:v2", Later);
        Assert.False(wrongKey.IsSuccess);
        Assert.Equal(RideSagaErrorCode.OperationKeyMismatch, wrongKey.ErrorCode);

        var outcome = saga.RecordAuthorizeSucceededAfterCancellation(RideSagaOperationKeys.Void(TripId), Later);
        Assert.True(outcome.IsSuccess);
        Assert.Equal(RideSagaStep.VoidPending, saga.CurrentStep);
        Assert.Equal(RideSagaOperationKeys.Void(TripId), saga.VoidOperationKey);
        Assert.Equal(RideSagaStatus.Compensating, saga.Status);
    }

    [Fact]
    public void DeclinedAfterCancel_ReleasesWithoutVoid()
    {
        var saga = NewSaga();
        saga.RecordCancellationDuringAuthorize(Later);

        var outcome = saga.RecordAuthorizeDeclined("DECLINED", Later);

        Assert.True(outcome.IsSuccess);
        Assert.Equal(RideSagaStep.ReleasePending, saga.CurrentStep);
        // No provider effect existed: no void key is ever minted.
        Assert.Null(saga.VoidOperationKey);
    }

    // --------------------------------------------------- void / release pair

    [Fact]
    public void VoidResolved_CompletesCompensationOnlyWhenReleaseAlsoConfirmed()
    {
        var saga = NewSaga();
        saga.RecordCancellationDuringAuthorize(Later);
        saga.RecordAuthorizeSucceededAfterCancellation(RideSagaOperationKeys.Void(TripId), Later);

        // Void resolves first, release still outstanding: the saga waits on
        // the exact reservation release (DSI-033/DSL-002).
        var pending = saga.RecordVoidResolved(Later, reservationReleaseConfirmed: false);
        Assert.True(pending.IsSuccess);
        Assert.Equal(RideSagaStep.ReleasePending, saga.CurrentStep);
        Assert.Equal(RideSagaStatus.Compensating, saga.Status);
        Assert.True(saga.RecordReservationReleased(Later).IsSuccess);
        Assert.Equal(RideSagaStatus.Compensated, saga.Status);
        Assert.True(saga.Status.IsTerminal());

        // Mirror order: release confirmed first (saga stays on the void),
        // then the void resolution completes compensation directly.
        var mirrored = NewSaga();
        mirrored.RecordCancellationDuringAuthorize(Later);
        mirrored.RecordAuthorizeSucceededAfterCancellation(RideSagaOperationKeys.Void(TripId), Later);
        Assert.True(mirrored.RecordReservationReleased(Later).IsReplay);
        Assert.Equal(RideSagaStep.VoidPending, mirrored.CurrentStep);
        Assert.True(mirrored.RecordVoidResolved(Later, reservationReleaseConfirmed: true).IsSuccess);
        Assert.Equal(RideSagaStatus.Compensated, mirrored.Status);
    }

    [Fact]
    public void ReservationReleased_CompletesReleasePending_ButWaitsForVoidWhenPending()
    {
        var declined = NewSaga();
        declined.RecordAuthorizeDeclined("DECLINED", Later);
        Assert.True(declined.RecordReservationReleased(Later).IsSuccess);
        Assert.Equal(RideSagaStatus.Compensated, declined.Status);

        var voided = NewSaga();
        voided.RecordCancellationDuringAuthorize(Later);
        voided.RecordAuthorizeSucceededAfterCancellation(RideSagaOperationKeys.Void(TripId), Later);
        var stillVoiding = voided.RecordReservationReleased(Later);
        Assert.True(stillVoiding.IsSuccess);
        Assert.True(stillVoiding.IsReplay);
        Assert.Equal(RideSagaStep.VoidPending, voided.CurrentStep);
        Assert.Equal(RideSagaStatus.Compensating, voided.Status);
        Assert.False(voided.Status.IsTerminal());
    }

    // ------------------------------------------------------ ride + capture

    [Fact]
    public void RideCompleted_RequiresActiveAuthorized_SetsStableCaptureKey()
    {
        var saga = Authorized();

        var outcome = saga.RecordRideCompleted(RideSagaOperationKeys.Capture(TripId), Later);
        var duplicate = saga.RecordRideCompleted(RideSagaOperationKeys.Capture(TripId), Later.AddSeconds(1));

        Assert.True(outcome.IsSuccess);
        Assert.Equal(RideSagaStep.CapturePending, saga.CurrentStep);
        Assert.Equal($"{TripId:D}:capture:v1", saga.CaptureOperationKey);
        Assert.True(duplicate.IsReplay);
        Assert.Equal(3, saga.StepVersion);
    }

    [Fact]
    public void RideCompleted_RejectedFromCompensationAndWrongKey()
    {
        var compensating = NewSaga();
        compensating.RecordAuthorizeDeclined("DECLINED", Later);
        Assert.False(compensating.RecordRideCompleted(RideSagaOperationKeys.Capture(TripId), Later).IsSuccess);

        var wrongKey = Authorized();
        var outcome = wrongKey.RecordRideCompleted($"{TripId:D}:capture:v2", Later);
        Assert.False(outcome.IsSuccess);
        Assert.Equal(RideSagaErrorCode.OperationKeyMismatch, outcome.ErrorCode);
        Assert.Equal(RideSagaStep.Authorized, wrongKey.CurrentStep);
    }

    [Fact]
    public void CaptureSucceeded_TerminatesCompleted_AndReplaysAfterwards()
    {
        var saga = CapturePending();

        var first = saga.RecordCaptureSucceeded(Later);
        var duplicate = saga.RecordCaptureSucceeded(Later.AddSeconds(1));

        Assert.True(first.IsSuccess);
        Assert.Equal(RideSagaStatus.Completed, saga.Status);
        Assert.Equal(RideSagaStep.Captured, saga.CurrentStep);
        Assert.True(duplicate.IsReplay);
    }

    [Fact]
    public void CaptureUncertain_NeverAdvancesOrRegresses_TheSameKeyStaysPending()
    {
        var saga = CapturePending();
        var key = saga.CaptureOperationKey;

        var first = saga.RecordCaptureOutcomeUncertain("TRANSIENT_FAILURE", Later);
        var duplicate = saga.RecordCaptureOutcomeUncertain("TRANSIENT_FAILURE", Later.AddSeconds(1));

        Assert.True(first.IsSuccess);
        // DSI-028: the ride already happened; capture uncertainty holds the
        // saga on CAPTURE_PENDING with the SAME capture key (forward recovery).
        Assert.Equal(RideSagaStep.CapturePending, saga.CurrentStep);
        Assert.Equal(RideSagaStatus.Active, saga.Status);
        Assert.Equal(key, saga.CaptureOperationKey);
        Assert.Equal("TRANSIENT_FAILURE", saga.LastErrorCode);
        Assert.True(duplicate.IsReplay);

        // The pending step still resolves on the authoritative outcome later.
        Assert.True(saga.RecordCaptureSucceeded(Later.AddMinutes(1)).IsSuccess);
        Assert.Equal(RideSagaStatus.Completed, saga.Status);
    }

    [Fact]
    public void CaptureUncertain_RejectedOutsideCaptureStep()
    {
        var saga = NewSaga();
        var outcome = saga.RecordCaptureOutcomeUncertain("TRANSIENT_FAILURE", Later);
        Assert.False(outcome.IsSuccess);
        Assert.Equal(RideSagaErrorCode.IllegalTransition, outcome.ErrorCode);
    }

    [Fact]
    public void RequiresReview_PreservesStepForLaterAuthoritativeResolution()
    {
        var pending = NewSaga();
        Assert.True(pending.RecordRequiresReview("RETRY_EXHAUSTED", Later).IsSuccess);
        Assert.Equal(RideSagaStatus.RequiresReview, pending.Status);
        Assert.Equal(RideSagaStep.AuthorizePending, pending.CurrentStep);
        Assert.True(pending.RecordRequiresReview("RETRY_EXHAUSTED", Later).IsReplay);
        // A later authoritative success still resolves the review state.
        Assert.True(pending.RecordAuthorizeSucceeded(Later.AddMinutes(1)).IsSuccess);
        Assert.Equal(RideSagaStatus.Active, pending.Status);
        Assert.Equal(RideSagaStep.Authorized, pending.CurrentStep);

        var capturing = CapturePending();
        Assert.True(capturing.RecordRequiresReview("CAPTURE_RETRY_EXHAUSTED", Later).IsSuccess);
        Assert.Equal(RideSagaStep.CapturePending, capturing.CurrentStep);
    }

    [Fact]
    public void RequiresReview_OnCompensatingSaga_IsObservationOnly_NeverOwnsTheStatus()
    {
        var saga = NewSaga();
        saga.RecordCancellationDuringAuthorize(Later);

        var review = saga.RecordRequiresReview("RETRY_EXHAUSTED", Later);

        Assert.True(review.IsSuccess);
        // A review signal never clobbers COMPENSATING: every compensation
        // resolver requires that status, so ownership must be preserved.
        Assert.Equal(RideSagaStatus.Compensating, saga.Status);
        Assert.Equal(RideSagaStep.AuthorizePending, saga.CurrentStep);
        Assert.Equal("RETRY_EXHAUSTED", saga.LastErrorCode);
        Assert.True(saga.RecordRequiresReview("RETRY_EXHAUSTED", Later.AddSeconds(1)).IsReplay);

        // The void leg still dispatches and resolves after the observation.
        Assert.True(saga.RecordAuthorizeSucceededAfterCancellation(
            RideSagaOperationKeys.Void(TripId), Later).IsSuccess);
        Assert.Equal(RideSagaStep.VoidPending, saga.CurrentStep);
        Assert.True(saga.RecordRequiresReview("VOID_REVIEW", Later).IsSuccess);
        Assert.Equal(RideSagaStatus.Compensating, saga.Status);
        Assert.True(saga.RecordVoidResolved(Later, reservationReleaseConfirmed: true).IsSuccess);
        Assert.Equal(RideSagaStatus.Compensated, saga.Status);
    }

    [Fact]
    public void ReservationReleased_AtAuthorizePending_IsDurableNoOp_LaterOutcomeFinishes()
    {
        var saga = NewSaga();
        saga.RecordCancellationDuringAuthorize(Later);

        // Driver confirms the release before the payment outcome resolves:
        // durable on the Trip ledger, no saga transition, no throw.
        var early = saga.RecordReservationReleased(Later);
        Assert.True(early.IsSuccess);
        Assert.True(early.IsReplay);
        Assert.Equal(RideSagaStatus.Compensating, saga.Status);
        Assert.Equal(RideSagaStep.AuthorizePending, saga.CurrentStep);

        // The DECLINED resolution then finishes compensation immediately
        // (the ledger already proves the release).
        Assert.True(saga.RecordAuthorizeDeclined("DECLINED", Later).IsSuccess);
        Assert.True(saga.RecordReservationReleased(Later).IsSuccess);
        Assert.Equal(RideSagaStatus.Compensated, saga.Status);
    }

    [Fact]
    public void TerminalSagas_RejectEveryReactivation()
    {
        var completed = CapturePending();
        completed.RecordCaptureSucceeded(Later);

        Assert.Equal(RideSagaErrorCode.TerminalState, completed.RecordAuthorizeSucceeded(Later).ErrorCode);
        Assert.Equal(RideSagaErrorCode.TerminalState, completed.RecordRideCompleted(RideSagaOperationKeys.Capture(TripId), Later).ErrorCode);
        Assert.Equal(RideSagaErrorCode.TerminalState, completed.RecordRequiresReview("LATE", Later).ErrorCode);
    }

    // ------------------------------------------------------ reconstruction

    [Fact]
    public void Reconstitute_EnforcesLineageAndCoherence()
    {
        Assert.Throws<ArgumentException>(() => RideSaga.Reconstitute(
            SagaId, TripId, RideSagaStatus.Active, RideSagaStep.AuthorizePending, 1,
            null, RideSagaOperationKeys.Authorize(TripId), null, null, null, null, StartMoment, StartMoment));

        Assert.Throws<ArgumentException>(() => RideSaga.Reconstitute(
            SagaId, TripId, RideSagaStatus.Active, RideSagaStep.AuthorizePending, 1,
            ReservationId, $"{Guid.NewGuid():D}:authorize:v1", null, null, null, null, StartMoment, StartMoment));

        Assert.Throws<ArgumentException>(() => RideSaga.Reconstitute(
            SagaId, TripId, RideSagaStatus.Completed, RideSagaStep.Authorized, 2,
            ReservationId, RideSagaOperationKeys.Authorize(TripId), null, null, null, null, StartMoment, Later));

        var healthy = RideSaga.Reconstitute(
            SagaId, TripId, RideSagaStatus.Active, RideSagaStep.AuthorizePending, 1,
            ReservationId, RideSagaOperationKeys.Authorize(TripId), null, null, null, null, StartMoment, StartMoment);
        Assert.Equal(RideSagaStep.AuthorizePending, healthy.CurrentStep);
    }
}
