// A-W11 process-level fault proofs (Doc 05A §19, Doc 03 REL-04/REL-07/REL-17,
// Doc 04 §22).
//
// These are the first A-owned proofs that terminate and restart the REAL
// compiled Trip service process. The W09/W10 suites proved the same
// properties at the in-process seam (relay test hooks, direct handler
// invocation, WebApplicationFactory hosts); here the crash window, the
// durable recovery and the broker round trips all cross OS process
// boundaries against real PostgreSQL and RabbitMQ.
//
// WHAT EACH SCENARIO PROVES
//   REL-04  the producer process is SIGKILLed while the relay holds the
//           committed AuthorizePayment intent at the deterministic
//           Saga.AfterPersistBeforeDispatch faultpoint; a restarted process
//           reclaims and publishes the SAME messageId through the real broker.
//   REL-07  the process is killed after the authorize dispatch is durably
//           published; the Payment success outcome arrives while the service
//           is DOWN (durably backlogged on the quorum queue); the restarted
//           process continues the SAME saga (identity + operationKey + step
//           lineage) through the remaining lifecycle to terminal COMPLETED,
//           and stale/duplicate late outcomes cannot regress it.
//   REL-17  late authority facts (DriverReserved after CANCELLED, Payment
//           success after CANCELLED) are delivered through the real broker to
//           the real process; terminal state never regresses and cleanup uses
//           the exact identities only; a logically duplicate delivery is a
//           durable BusinessReplay, never a second mutation.
//   A-W11-05 cancellation compensation survives a crash with obligations
//           outstanding: the backlogged release confirmation resolves the
//           exact reservation identity after restart, and COMPENSATED is
//           reached only when every leg resolved. The authorized-after-cancel
//           race resolves through the exact void key and the exact release
//           identity in the void-leg scenario.
//
// Every synchronization point is an authoritative oracle: a database row
// state, a queue content/consumer count, the faultpoint reached marker, or
// the killed process's verified exit. No sleep coordinates a race
// (Doc 03 §1.5, Doc 05A §19.2 A-W11-06).

using Microsoft.EntityFrameworkCore;
using RabbitMQ.Client;
using RideHailing.TripService.Domain.Sagas;
using RideHailing.TripService.Domain.Trips;
using RideHailing.TripService.Integration.FaultPoints;
using RideHailing.TripService.Integration.Messaging;

namespace RideHailing.TripService.IntegrationTests;

[Collection(TripPostgresCollection.Name)]
public sealed class TripW11FaultProcessTests(TripPostgresFixture postgres, TripRabbitFixture rabbit)
    : IClassFixture<TripRabbitFixture>
{
    private static readonly string[] ManagedQueues =
    [
        "payment.commands.q", "driver.commands.q", "matching.commands.q",
        "matching.ride-requested.q", "trip.driver-state.q", "trip.payment-outcome.q", "trip.matching-outcome.q",
    ];

    private static readonly TimeSpan StateDeadline = TimeSpan.FromSeconds(30);

    private async Task<string> NewScenarioDatabaseAsync(string prefix) =>
        await TripW11Harness.NewProbeDatabaseAsync(postgres.ConnectionString, prefix);

    // =====================================================================
    // REL-04 — real producer process crash at the saga dispatch boundary
    // =====================================================================

    [Fact]
    public async Task ProducerProcess_CrashedAtSagaDispatchFaultPoint_RestartPublishesSameIdentity()
    {
        var connectionString = await NewScenarioDatabaseAsync("trip_w11_rel04");
        var seed = await TripW11Harness.SeedDriverAcceptedAuthorizePendingAsync(connectionString);
        await TripW11Harness.PurgeQueuesAsync(rabbit.Factory, ManagedQueues);

        var signalDirectory = Path.Combine(Path.GetTempPath(), $"trip-w11-rel04-{Guid.NewGuid():N}");
        Directory.CreateDirectory(signalDirectory);
        var faultPoint = TripFaultPointNames.SagaAfterPersistBeforeDispatch;
        var reachedPath = TripFaultPointGate.ReachedPath(signalDirectory, faultPoint);

        await using (var crashed = await TripProcess.StartAsync(
            connectionString, rabbit.Factory, "rel04-crash", signalDirectory))
        {
            // Deterministic barrier: the relay holds the committed authorize
            // intent immediately before publication.
            await TripW11Wait.UntilAsync("faultpoint reached marker",
                () => Task.FromResult(File.Exists(reachedPath)), StateDeadline);

            // --- crash point: authoritative pre-crash state ----------------
            var trip = await TripW11Harness.TripAsync(connectionString, seed.TripId);
            var saga = await TripW11Harness.SagaAsync(connectionString, seed.TripId);
            var intent = await TripW11Harness.OutboxRowAsync(connectionString, seed.AuthorizeMessageId);
            Assert.Equal(TripState.DriverAccepted, trip.State);
            Assert.Equal(RideSagaStatus.Active, saga.Status);
            Assert.Equal(RideSagaStep.AuthorizePending, saga.CurrentStep);
            Assert.Equal(seed.AuthorizeOperationKey, saga.AuthorizeOperationKey);
            Assert.NotNull(intent);
            Assert.Equal("PENDING", intent!.Status);
            Assert.Equal(seed.AuthorizeMessageId, intent.MessageId);
            Assert.NotNull(intent.ClaimedBy); // claimed by the dying relay, never published.
            Assert.Null(intent.PublishedAt);
            Assert.Equal(0u, await TripW11Harness.QueueMessageCountAsync(rabbit.Factory, "payment.commands.q"));

            // Abrupt termination of the exact process the harness launched.
            await crashed.KillAsync();

            // The kill left the dispatch unpublished: no posthumous publish.
            var afterKill = await TripW11Harness.OutboxRowAsync(connectionString, seed.AuthorizeMessageId);
            Assert.Equal("PENDING", afterKill!.Status);
            Assert.Null(afterKill.PublishedAt);
        }

        // --- recovery: a NEW process on the SAME database and broker -------
        await using (await TripProcess.StartAsync(connectionString, rabbit.Factory, "rel04-restart"))
        {
            await TripW11Wait.UntilAsync("authorize intent PUBLISHED after restart", async () =>
                (await TripW11Harness.OutboxRowAsync(connectionString, seed.AuthorizeMessageId))?.Status ==
                "PUBLISHED", StateDeadline);

            // The identical logical message reached the real broker exactly once.
            var delivered = await TripW11Harness.DrainQueueAsync(rabbit.Factory, "payment.commands.q");
            var authorize = Assert.Single(delivered, m => m.MessageId == seed.AuthorizeMessageId);
            Assert.Equal("AuthorizePaymentV1", authorize.MessageType);
            Assert.Equal(seed.SagaId.ToString("D"), authorize.SagaId);
            Assert.Equal(seed.AuthorizeOperationKey, authorize.OperationKey);

            // No second logical transition: the restart dispatched the pending
            // intent; it did not re-execute the business transition.
            var trip = await TripW11Harness.TripAsync(connectionString, seed.TripId);
            var saga = await TripW11Harness.SagaAsync(connectionString, seed.TripId);
            var intent = await TripW11Harness.OutboxRowAsync(connectionString, seed.AuthorizeMessageId);
            Assert.Equal(TripState.DriverAccepted, trip.State);
            Assert.Equal(RideSagaStep.AuthorizePending, saga.CurrentStep);
            Assert.Equal(seed.SagaStepVersion, saga.StepVersion);
            Assert.Equal("PUBLISHED", intent!.Status);
            Assert.Equal(0, intent.AttemptCount); // the crash burned no publication attempt.
            // Exactly the two staged-by-transition intents exist (RideRequested
            // + AuthorizePayment): the restart added nothing.
            Assert.Equal(2, await TripW11Harness.OutboxCountAsync(connectionString, seed.TripId));
        }
    }

    // =====================================================================
    // REL-07 — real saga restart with a durably backlogged payment outcome
    // =====================================================================

    [Fact]
    public async Task SagaRestart_WhilePaymentOutcomeBacklogs_SameSagaContinuesToCompletion()
    {
        var connectionString = await NewScenarioDatabaseAsync("trip_w11_rel07");
        var seed = await TripW11Harness.SeedDriverAcceptedAuthorizePendingAsync(connectionString);
        await TripW11Harness.PurgeQueuesAsync(rabbit.Factory, ManagedQueues);

        int stepVersionBeforeKill;
        await using (var first = await TripProcess.StartAsync(connectionString, rabbit.Factory, "rel07-first"))
        {
            // DB state barrier: the authorize dispatch is durably published
            // while the saga itself still awaits its outcome — the exact
            // REL-07 crash point.
            await TripW11Wait.UntilAsync("authorize intent PUBLISHED", async () =>
                (await TripW11Harness.OutboxRowAsync(connectionString, seed.AuthorizeMessageId))?.Status ==
                "PUBLISHED", StateDeadline);
            stepVersionBeforeKill = (await TripW11Harness.SagaAsync(connectionString, seed.TripId)).StepVersion;

            await first.KillAsync();
        }

        // The broker unregisters the dead connection's consumers before the
        // backlog is asserted (bounded authoritative poll, not a sleep).
        await TripW11Wait.UntilAsync("trip consumers unregistered after kill", async () =>
            await TripW11Harness.QueueConsumerCountAsync(rabbit.Factory, "trip.payment-outcome.q") == 0u,
            StateDeadline);

        // The Payment success (harness acting as the Payment authority
        // publisher with the saga's exact lineage — C-W10's real Payment
        // producer is pending) arrives while the Trip service is DOWN.
        var outcome = TripW11Harness.PaymentAuthorized(seed.TripId, seed.SagaId, seed.AuthorizeOperationKey);
        await TripW11Harness.PublishAsync(rabbit.Factory, "payment.authorized.v1", outcome);

        // Durable backlog evidence: the quorum queue holds the outcome ready
        // with no consumer attached while the service is down.
        Assert.Equal(0u, await TripW11Harness.QueueConsumerCountAsync(rabbit.Factory, "trip.payment-outcome.q"));
        Assert.Equal(1u, await TripW11Harness.QueueMessageCountAsync(rabbit.Factory, "trip.payment-outcome.q"));

        await using (await TripProcess.StartAsync(connectionString, rabbit.Factory, "rel07-restart"))
        {
            // The SAME saga continues: identity, operationKey and step lineage
            // preserved; the backlogged outcome legally advances the ride.
            await TripW11Wait.UntilAsync("trip READY_TO_START after restart", async () =>
                (await TripW11Harness.TripAsync(connectionString, seed.TripId)).State == TripState.ReadyToStart,
                StateDeadline);

            var saga = await TripW11Harness.SagaAsync(connectionString, seed.TripId);
            Assert.Equal(seed.SagaId, saga.SagaId);
            Assert.Equal(seed.AuthorizeOperationKey, saga.AuthorizeOperationKey);
            Assert.Equal(RideSagaStatus.Active, saga.Status);
            Assert.Equal(RideSagaStep.Authorized, saga.CurrentStep);
            Assert.Equal(stepVersionBeforeKill + 1, saga.StepVersion); // one legal advance, nothing more.

            var receipt = await TripW11Harness.ReceiptAsync(connectionString, outcome.MessageId);
            Assert.NotNull(receipt);
            Assert.Equal("Applied", receipt!.Disposition);

            // No second dispatch of the already-published authorize intent.
            var delivered = await TripW11Harness.DrainQueueAsync(rabbit.Factory, "payment.commands.q");
            Assert.Single(delivered, m => m.MessageId == seed.AuthorizeMessageId);

            // ---- the restarted saga drives the FULL remaining lifecycle ----
            await TripW11Harness.PublishAsync(rabbit.Factory, "driver.trip-started.v1",
                TripW11Harness.DriverTripStarted(seed.TripId, seed.DriverId, seed.ReservationId));
            await TripW11Wait.UntilAsync("trip IN_PROGRESS", async () =>
                (await TripW11Harness.TripAsync(connectionString, seed.TripId)).State == TripState.InProgress,
                StateDeadline);

            await TripW11Harness.PublishAsync(rabbit.Factory, "driver.trip-completed.v1",
                TripW11Harness.DriverTripCompleted(seed.TripId, seed.DriverId, seed.ReservationId));
            var captureMessageId = TripSagaIntents.CapturePaymentMessageId(seed.TripId);
            await TripW11Wait.UntilAsync("trip COMPLETED with CAPTURE_PENDING saga", async () =>
                (await TripW11Harness.TripAsync(connectionString, seed.TripId)).State == TripState.Completed &&
                (await TripW11Harness.SagaAsync(connectionString, seed.TripId)).CurrentStep ==
                    RideSagaStep.CapturePending, StateDeadline);
            await TripW11Wait.UntilAsync("capture intent PUBLISHED", async () =>
                (await TripW11Harness.OutboxRowAsync(connectionString, captureMessageId))?.Status == "PUBLISHED",
                StateDeadline);

            var captured = await TripW11Harness.DrainQueueAsync(rabbit.Factory, "payment.commands.q");
            var capture = Assert.Single(captured, m => m.MessageId == captureMessageId);
            Assert.Equal("CapturePaymentV1", capture.MessageType);
            Assert.Equal(RideSagaOperationKeys.Capture(seed.TripId), capture.OperationKey);
            Assert.Equal(seed.SagaId.ToString("D"), capture.SagaId);

            var captureOutcome = TripW11Harness.PaymentCaptured(
                seed.TripId, seed.SagaId, RideSagaOperationKeys.Capture(seed.TripId));
            await TripW11Harness.PublishAsync(rabbit.Factory, "payment.captured.v1", captureOutcome);
            await TripW11Wait.UntilAsync("saga terminal COMPLETED/CAPTURED", async () =>
            {
                var s = await TripW11Harness.SagaAsync(connectionString, seed.TripId);
                return s.Status == RideSagaStatus.Completed && s.CurrentStep == RideSagaStep.Captured;
            }, StateDeadline);

            // ---- REL-17: terminal state is monotonic under late events -----
            var versionAtTerminal = (await TripW11Harness.TripAsync(connectionString, seed.TripId)).Version;
            var sagaVersionAtTerminal = (await TripW11Harness.SagaAsync(connectionString, seed.TripId)).StepVersion;

            var staleFailure = TripW11Harness.PaymentAuthorizationDeclined(
                seed.TripId, seed.SagaId, seed.AuthorizeOperationKey);
            await TripW11Harness.PublishAsync(rabbit.Factory, "payment.authorization-failed.v1", staleFailure);
            await TripW11Wait.UntilAsync("stale failure receipt", async () =>
                await TripW11Harness.ReceiptAsync(connectionString, staleFailure.MessageId) is not null,
                StateDeadline);
            var staleReceipt = await TripW11Harness.ReceiptAsync(connectionString, staleFailure.MessageId);
            Assert.Equal("NeedsReview", staleReceipt!.Disposition);
            Assert.Equal("STALE_PAYMENT_OUTCOME", staleReceipt.Reason);

            var duplicateCapture = TripW11Harness.PaymentCaptured(
                seed.TripId, seed.SagaId, RideSagaOperationKeys.Capture(seed.TripId));
            await TripW11Harness.PublishAsync(rabbit.Factory, "payment.captured.v1", duplicateCapture);
            await TripW11Wait.UntilAsync("duplicate capture receipt", async () =>
                await TripW11Harness.ReceiptAsync(connectionString, duplicateCapture.MessageId) is not null,
                StateDeadline);
            Assert.Equal("BusinessReplay",
                (await TripW11Harness.ReceiptAsync(connectionString, duplicateCapture.MessageId))!.Disposition);

            var tripAfterLate = await TripW11Harness.TripAsync(connectionString, seed.TripId);
            var sagaAfterLate = await TripW11Harness.SagaAsync(connectionString, seed.TripId);
            Assert.Equal(TripState.Completed, tripAfterLate.State); // COMPLETED never regresses.
            Assert.Equal(versionAtTerminal, tripAfterLate.Version);
            Assert.Equal(RideSagaStatus.Completed, sagaAfterLate.Status);
            Assert.Equal(sagaVersionAtTerminal, sagaAfterLate.StepVersion);
        }
    }

    // =====================================================================
    // REL-17 — late DriverReserved on a cancelled trip + logically duplicate
    // delivery (fresh messageId, same authority fact)
    // =====================================================================

    [Fact]
    public async Task LateReservationAfterCancel_RealDelivery_ExactCleanupAndBusinessReplayDuplicate()
    {
        var connectionString = await NewScenarioDatabaseAsync("trip_w11_rel17");
        var seed = await TripW11Harness.SeedCancelledDuringAuthorizeAsync(connectionString);
        await TripW11Harness.PurgeQueuesAsync(rabbit.Factory, ManagedQueues);

        await using var host = await TripProcess.StartAsync(connectionString, rabbit.Factory, "rel17-late");

        var trip = await TripW11Harness.TripAsync(connectionString, seed.TripId);
        Assert.Equal(TripState.Cancelled, trip.State);
        var versionBefore = trip.Version;
        var intentsBefore = await TripW11Harness.OutboxCountAsync(connectionString, seed.TripId);

        // A late reservation fact for a NEW exact identity on the cancelled trip.
        var lateReservationId = Guid.NewGuid();
        var lateReserved = TripW11Harness.DriverReserved(seed.TripId, seed.DriverId, lateReservationId);
        await TripW11Harness.PublishAsync(rabbit.Factory, "driver.reserved.v1", lateReserved);
        await TripW11Wait.UntilAsync("late reservation receipt", async () =>
            await TripW11Harness.ReceiptAsync(connectionString, lateReserved.MessageId) is not null,
            StateDeadline);
        Assert.Equal("Applied",
            (await TripW11Harness.ReceiptAsync(connectionString, lateReserved.MessageId))!.Disposition);

        // CANCELLED never reactivates; the late fact created EXACT cleanup.
        var after = await TripW11Harness.TripAsync(connectionString, seed.TripId);
        Assert.Equal(TripState.Cancelled, after.State);
        Assert.Equal(versionBefore + 1, after.Version);
        var releaseMessageId = $"trip:{seed.TripId:N}:release:{lateReservationId:N}";
        var release = await TripW11Harness.OutboxRowAsync(connectionString, releaseMessageId);
        Assert.NotNull(release);
        Assert.Equal("ReleaseDriverReservationV1", release!.MessageType);
        Assert.Equal(seed.DriverId, TripW11Harness.PayloadGuid(release, "driverId"));
        Assert.Equal(lateReservationId, TripW11Harness.PayloadGuid(release, "reservationId"));
        Assert.Equal(intentsBefore + 1, await TripW11Harness.OutboxCountAsync(connectionString, seed.TripId));

        // A logically duplicate delivery (fresh messageId, same authority
        // fact) is a durable BusinessReplay: no second business mutation and
        // no second cleanup intent (REL-05 consumer half at process level).
        var duplicateFact = TripW11Harness.DriverReserved(seed.TripId, seed.DriverId, lateReservationId);
        await TripW11Harness.PublishAsync(rabbit.Factory, "driver.reserved.v1", duplicateFact);
        await TripW11Wait.UntilAsync("duplicate fact receipt", async () =>
            await TripW11Harness.ReceiptAsync(connectionString, duplicateFact.MessageId) is not null,
            StateDeadline);
        Assert.Equal("BusinessReplay",
            (await TripW11Harness.ReceiptAsync(connectionString, duplicateFact.MessageId))!.Disposition);

        var afterDuplicate = await TripW11Harness.TripAsync(connectionString, seed.TripId);
        Assert.Equal(TripState.Cancelled, afterDuplicate.State);
        Assert.Equal(versionBefore + 1, afterDuplicate.Version);
        Assert.Equal(intentsBefore + 1, await TripW11Harness.OutboxCountAsync(connectionString, seed.TripId));
        Assert.Equal(2, await CountReceiptsAsync(connectionString, seed.TripId));
    }

    // =====================================================================
    // REL-17(b) + A-W11-05 — authorized-after-cancel exact void compensation
    // through the real process (void leg then release leg)
    // =====================================================================

    [Fact]
    public async Task AuthorizedAfterCancel_VoidAndReleaseLegs_ExactIdentityToCompensated()
    {
        var connectionString = await NewScenarioDatabaseAsync("trip_w11_void");
        var seed = await TripW11Harness.SeedCancelledDuringAuthorizeAsync(connectionString);
        await TripW11Harness.PurgeQueuesAsync(rabbit.Factory, ManagedQueues);

        await using var host = await TripProcess.StartAsync(connectionString, rabbit.Factory, "w11-void");

        // The authorization raced the cancellation and WON at Payment
        // authority: the hold exists, so an exact void with the frozen void
        // key is dispatched. The Trip stays CANCELLED (DSI-010).
        var authorized = TripW11Harness.PaymentAuthorized(seed.TripId, seed.SagaId, seed.AuthorizeOperationKey);
        await TripW11Harness.PublishAsync(rabbit.Factory, "payment.authorized.v1", authorized);
        await TripW11Wait.UntilAsync("saga VOID_PENDING", async () =>
            (await TripW11Harness.SagaAsync(connectionString, seed.TripId)).CurrentStep == RideSagaStep.VoidPending,
            StateDeadline);

        var trip = await TripW11Harness.TripAsync(connectionString, seed.TripId);
        var saga = await TripW11Harness.SagaAsync(connectionString, seed.TripId);
        Assert.Equal(TripState.Cancelled, trip.State); // never revived.
        Assert.Equal(RideSagaStatus.Compensating, saga.Status);
        Assert.Equal(RideSagaOperationKeys.Void(seed.TripId), saga.VoidOperationKey);

        // The void command carries the frozen lineage through the real broker.
        var voidMessageId = TripSagaIntents.VoidPaymentAuthorizationMessageId(seed.TripId);
        await TripW11Wait.UntilAsync("void intent PUBLISHED", async () =>
            (await TripW11Harness.OutboxRowAsync(connectionString, voidMessageId))?.Status == "PUBLISHED",
            StateDeadline);
        var voided = Assert.Single(
            (await TripW11Harness.DrainQueueAsync(rabbit.Factory, "payment.commands.q")),
            m => m.MessageId == voidMessageId);
        Assert.Equal(RideSagaOperationKeys.Void(seed.TripId), voided.OperationKey);
        Assert.Equal(seed.SagaId.ToString("D"), voided.SagaId);

        // Payment authority confirms the void (harness publisher, exact key).
        var voidOutcome = TripW11Harness.PaymentAuthorizationVoided(
            seed.TripId, seed.SagaId, RideSagaOperationKeys.Void(seed.TripId));
        await TripW11Harness.PublishAsync(rabbit.Factory, "payment.authorization-voided.v1", voidOutcome);
        await TripW11Wait.UntilAsync("saga RELEASE_PENDING after void", async () =>
            (await TripW11Harness.SagaAsync(connectionString, seed.TripId)).CurrentStep ==
                RideSagaStep.ReleasePending, StateDeadline);

        // The release leg resolves with the EXACT reservation identity.
        var released = TripW11Harness.DriverReservationReleased(seed.TripId, seed.DriverId, seed.ReservationId);
        await TripW11Harness.PublishAsync(rabbit.Factory, "driver.reservation-released.v1", released);
        await TripW11Wait.UntilAsync("saga terminal COMPENSATED", async () =>
            (await TripW11Harness.SagaAsync(connectionString, seed.TripId)).Status == RideSagaStatus.Compensated,
            StateDeadline);

        var finalSaga = await TripW11Harness.SagaAsync(connectionString, seed.TripId);
        var finalTrip = await TripW11Harness.TripAsync(connectionString, seed.TripId);
        Assert.Equal(RideSagaStatus.Compensated, finalSaga.Status); // both legs resolved.
        Assert.Equal(TripState.Cancelled, finalTrip.State);
        Assert.Equal("Applied", (await TripW11Harness.ReceiptAsync(connectionString, released.MessageId))!
            .Disposition);
        Assert.Equal("Applied", (await TripW11Harness.ReceiptAsync(connectionString, voidOutcome.MessageId))!
            .Disposition);
    }

    // =====================================================================
    // A-W11-05 — crash with compensation obligations outstanding
    // =====================================================================

    [Fact]
    public async Task CompensationOutstanding_ProcessCrashed_BackloggedConfirmationResolvesAfterRestart()
    {
        var connectionString = await NewScenarioDatabaseAsync("trip_w11_comp");
        var seed = await TripW11Harness.SeedDriverAcceptedAuthorizePendingAsync(connectionString);
        await TripW11Harness.PurgeQueuesAsync(rabbit.Factory, ManagedQueues);

        var releaseMessageId = $"trip:{seed.TripId:N}:release:{seed.ReservationId:N}";

        await using (var first = await TripProcess.StartAsync(connectionString, rabbit.Factory, "w11-comp-first"))
        {
            // The authorize command is dispatched, then Payment authority
            // returns a terminal DECLINED (REL-09 semantics): the ride
            // cancels and compensation obligations become outstanding.
            await TripW11Wait.UntilAsync("authorize intent PUBLISHED", async () =>
                (await TripW11Harness.OutboxRowAsync(connectionString, seed.AuthorizeMessageId))?.Status ==
                "PUBLISHED", StateDeadline);
            var declined = TripW11Harness.PaymentAuthorizationDeclined(
                seed.TripId, seed.SagaId, seed.AuthorizeOperationKey);
            await TripW11Harness.PublishAsync(rabbit.Factory, "payment.authorization-failed.v1", declined);
            await TripW11Wait.UntilAsync("trip CANCELLED with release obligation", async () =>
                (await TripW11Harness.TripAsync(connectionString, seed.TripId)).State == TripState.Cancelled &&
                await TripW11Harness.OutboxRowAsync(connectionString, releaseMessageId) is not null,
                StateDeadline);
            Assert.Equal("Applied",
                (await TripW11Harness.ReceiptAsync(connectionString, declined.MessageId))!.Disposition);

            // Crash with the release obligation unresolved.
            await first.KillAsync();
        }

        // The broker unregisters the dead consumers before the backlog is
        // asserted (bounded authoritative poll, not a sleep).
        await TripW11Wait.UntilAsync("trip consumers unregistered after kill", async () =>
            await TripW11Harness.QueueConsumerCountAsync(rabbit.Factory, "trip.driver-state.q") == 0u,
            StateDeadline);

        // While the Trip service is DOWN, Driver (harness publisher) confirms
        // the exact release; the confirmation durably backlogs.
        var released = TripW11Harness.DriverReservationReleased(seed.TripId, seed.DriverId, seed.ReservationId);
        await TripW11Harness.PublishAsync(rabbit.Factory, "driver.reservation-released.v1", released);
        Assert.Equal(0u, await TripW11Harness.QueueConsumerCountAsync(rabbit.Factory, "trip.driver-state.q"));
        Assert.Equal(1u, await TripW11Harness.QueueMessageCountAsync(rabbit.Factory, "trip.driver-state.q"));

        await using (await TripProcess.StartAsync(connectionString, rabbit.Factory, "w11-comp-restart"))
        {
            await TripW11Wait.UntilAsync("saga COMPENSATED after restart", async () =>
                (await TripW11Harness.SagaAsync(connectionString, seed.TripId)).Status ==
                    RideSagaStatus.Compensated, StateDeadline);

            var saga = await TripW11Harness.SagaAsync(connectionString, seed.TripId);
            var trip = await TripW11Harness.TripAsync(connectionString, seed.TripId);
            Assert.Equal(RideSagaStatus.Compensated, saga.Status);
            Assert.Equal(TripState.Cancelled, trip.State); // terminal monotonicity.
            Assert.Equal(seed.SagaId, saga.SagaId);
            Assert.Equal(seed.AuthorizeOperationKey, saga.AuthorizeOperationKey);

            // The exact reservation identity was released, the release command
            // was dispatched by the restarted relay, and no void obligation
            // ever existed on the DECLINED path.
            await TripW11Wait.UntilAsync("release intent PUBLISHED after restart", async () =>
                (await TripW11Harness.OutboxRowAsync(connectionString, releaseMessageId))?.Status == "PUBLISHED",
                StateDeadline);
            var release = await TripW11Harness.OutboxRowAsync(connectionString, releaseMessageId);
            Assert.Equal(seed.ReservationId, TripW11Harness.PayloadGuid(release!, "reservationId"));
            Assert.Null(await TripW11Harness.OutboxRowAsync(
                connectionString, TripSagaIntents.VoidPaymentAuthorizationMessageId(seed.TripId)));

            Assert.Equal("Applied",
                (await TripW11Harness.ReceiptAsync(connectionString, released.MessageId))!.Disposition);
        }
    }

    private static async Task<int> CountReceiptsAsync(string connectionString, Guid tripId)
    {
        await using var db = TripW11Harness.Context(connectionString);
        return await db.MessageReceipts.AsNoTracking().CountAsync(r => r.TripId == tripId);
    }
}
