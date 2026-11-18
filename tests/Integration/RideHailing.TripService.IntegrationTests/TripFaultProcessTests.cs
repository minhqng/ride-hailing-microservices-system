// Process-level fault proofs: crash and recovery semantics across real OS
// process boundaries.
//
// These are the first end-to-end proofs that terminate and restart the REAL
// compiled Trip service process. The in-process suites proved the same
// properties at the seam (relay test hooks, direct handler invocation,
// WebApplicationFactory hosts); here the crash window, the durable recovery
// and the broker round trips all cross OS process boundaries against real
// PostgreSQL and RabbitMQ.
//
// WHAT EACH SCENARIO PROVES
//   crash before dispatch  the producer process is SIGKILLed while the relay
//           holds the committed AuthorizePayment intent at the deterministic
//           Saga.AfterPersistBeforeDispatch faultpoint; a restarted process
//           reclaims and publishes the SAME messageId through the real broker.
//   crash after dispatch  the process is killed after the authorize dispatch
//           is durably published; the Payment success outcome arrives while
//           the service is DOWN (durably backlogged on the quorum queue); the
//           restarted process continues the SAME saga (identity +
//           operationKey + step lineage) through the remaining lifecycle to
//           terminal COMPLETED, and stale or duplicate late outcomes cannot
//           regress it.
//   late authority facts  DriverReserved after CANCELLED and Payment success
//           after CANCELLED are delivered through the real broker to the real
//           process; terminal state never regresses and cleanup uses the exact
//           identities only; a logically duplicate delivery is a durable
//           BusinessReplay, never a second mutation.
//   compensation crash  cancellation compensation survives a crash with
//           obligations outstanding: the backlogged release confirmation
//           resolves the exact reservation identity after restart, and
//           COMPENSATED is reached only when every leg resolved. The
//           authorized-after-cancel race resolves through the exact void key
//           and the exact release identity in the void-leg scenario.
//
// Every synchronization point is an authoritative oracle: a database row
// state, a queue content/consumer count, the faultpoint reached marker, or
// the killed process's verified exit. No sleep coordinates a race.

using Microsoft.EntityFrameworkCore;
using RabbitMQ.Client;
using RideHailing.TripService.Domain.Sagas;
using RideHailing.TripService.Domain.Trips;
using RideHailing.TripService.Integration.FaultPoints;
using RideHailing.TripService.Integration.Messaging;

namespace RideHailing.TripService.IntegrationTests;

[Collection(TripPostgresCollection.Name)]
public sealed class TripFaultProcessTests(TripPostgresFixture postgres, TripRabbitFixture rabbit)
    : IClassFixture<TripRabbitFixture>
{
    private static readonly string[] ManagedQueues =
    [
        "payment.commands.q", "driver.commands.q", "matching.commands.q",
        "matching.ride-requested.q", "trip.driver-state.q", "trip.payment-outcome.q", "trip.matching-outcome.q",
    ];

    private static readonly TimeSpan StateDeadline = TimeSpan.FromSeconds(30);

    private async Task<string> NewScenarioDatabaseAsync(string prefix) =>
        await TripProcessHarness.NewProbeDatabaseAsync(postgres.ConnectionString, prefix);

    // =====================================================================
    // — real producer process crash at the saga dispatch boundary
    // =====================================================================

    [Fact]
    public async Task ProducerProcess_CrashedAtSagaDispatchFaultPoint_RestartPublishesSameIdentity()
    {
        var connectionString = await NewScenarioDatabaseAsync("trip_w11_rel04");
        var seed = await TripProcessHarness.SeedDriverAcceptedAuthorizePendingAsync(connectionString);
        await TripProcessHarness.PurgeQueuesAsync(rabbit.Factory, ManagedQueues);

        var signalDirectory = Path.Combine(Path.GetTempPath(), $"trip-rel04-{Guid.NewGuid():N}");
        Directory.CreateDirectory(signalDirectory);
        var faultPoint = TripFaultPointNames.SagaAfterPersistBeforeDispatch;
        var reachedPath = TripFaultPointGate.ReachedPath(signalDirectory, faultPoint);

        await using (var crashed = await TripProcess.StartAsync(
            connectionString, rabbit.Factory, "rel04-crash", signalDirectory))
        {
            // Deterministic barrier: the relay holds the committed authorize
            // intent immediately before publication.
            await TripProcessWait.UntilAsync("faultpoint reached marker",
                () => Task.FromResult(File.Exists(reachedPath)), StateDeadline);

            // --- crash point: authoritative pre-crash state ----------------
            var trip = await TripProcessHarness.TripAsync(connectionString, seed.TripId);
            var saga = await TripProcessHarness.SagaAsync(connectionString, seed.TripId);
            var intent = await TripProcessHarness.OutboxRowAsync(connectionString, seed.AuthorizeMessageId);
            Assert.Equal(TripState.DriverAccepted, trip.State);
            Assert.Equal(RideSagaStatus.Active, saga.Status);
            Assert.Equal(RideSagaStep.AuthorizePending, saga.CurrentStep);
            Assert.Equal(seed.AuthorizeOperationKey, saga.AuthorizeOperationKey);
            Assert.NotNull(intent);
            Assert.Equal("PENDING", intent!.Status);
            Assert.Equal(seed.AuthorizeMessageId, intent.MessageId);
            Assert.NotNull(intent.ClaimedBy); // claimed by the dying relay, never published.
            Assert.Null(intent.PublishedAt);
            Assert.Equal(0u, await TripProcessHarness.QueueMessageCountAsync(rabbit.Factory, "payment.commands.q"));

            // Abrupt termination of the exact process the harness launched.
            await crashed.KillAsync();

            // The kill left the dispatch unpublished: no posthumous publish.
            var afterKill = await TripProcessHarness.OutboxRowAsync(connectionString, seed.AuthorizeMessageId);
            Assert.Equal("PENDING", afterKill!.Status);
            Assert.Null(afterKill.PublishedAt);
        }

        // --- recovery: a NEW process on the SAME database and broker -------
        await using (await TripProcess.StartAsync(connectionString, rabbit.Factory, "rel04-restart"))
        {
            await TripProcessWait.UntilAsync("authorize intent PUBLISHED after restart", async () =>
                (await TripProcessHarness.OutboxRowAsync(connectionString, seed.AuthorizeMessageId))?.Status ==
                "PUBLISHED", StateDeadline);

            // The identical logical message reached the real broker exactly once.
            var delivered = await TripProcessHarness.DrainQueueAsync(rabbit.Factory, "payment.commands.q");
            var authorize = Assert.Single(delivered, m => m.MessageId == seed.AuthorizeMessageId);
            Assert.Equal("AuthorizePaymentV1", authorize.MessageType);
            Assert.Equal(seed.SagaId.ToString("D"), authorize.SagaId);
            Assert.Equal(seed.AuthorizeOperationKey, authorize.OperationKey);

            // No second logical transition: the restart dispatched the pending
            // intent; it did not re-execute the business transition.
            var trip = await TripProcessHarness.TripAsync(connectionString, seed.TripId);
            var saga = await TripProcessHarness.SagaAsync(connectionString, seed.TripId);
            var intent = await TripProcessHarness.OutboxRowAsync(connectionString, seed.AuthorizeMessageId);
            Assert.Equal(TripState.DriverAccepted, trip.State);
            Assert.Equal(RideSagaStep.AuthorizePending, saga.CurrentStep);
            Assert.Equal(seed.SagaStepVersion, saga.StepVersion);
            Assert.Equal("PUBLISHED", intent!.Status);
            Assert.Equal(0, intent.AttemptCount); // the crash burned no publication attempt.
            // Exactly the staged-by-transition intents exist (RideRequested +
            // its Rider notification + the assignment notification +
            // AuthorizePayment): the restart added nothing.
            Assert.Equal(4, await TripProcessHarness.OutboxCountAsync(connectionString, seed.TripId));
        }
    }

    // =====================================================================
    // — real saga restart with a durably backlogged payment outcome
    // =====================================================================

    [Fact]
    public async Task SagaRestart_WhilePaymentOutcomeBacklogs_SameSagaContinuesToCompletion()
    {
        var connectionString = await NewScenarioDatabaseAsync("trip_w11_rel07");
        var seed = await TripProcessHarness.SeedDriverAcceptedAuthorizePendingAsync(connectionString);
        await TripProcessHarness.PurgeQueuesAsync(rabbit.Factory, ManagedQueues);

        int stepVersionBeforeKill;
        await using (var first = await TripProcess.StartAsync(connectionString, rabbit.Factory, "rel07-first"))
        {
            // DB state barrier: the authorize dispatch is durably published
            // while the saga itself still awaits its outcome — the exact
            // crash point.
            await TripProcessWait.UntilAsync("authorize intent PUBLISHED", async () =>
                (await TripProcessHarness.OutboxRowAsync(connectionString, seed.AuthorizeMessageId))?.Status ==
                "PUBLISHED", StateDeadline);
            stepVersionBeforeKill = (await TripProcessHarness.SagaAsync(connectionString, seed.TripId)).StepVersion;

            await first.KillAsync();
        }

        // The broker unregisters the dead connection's consumers before the
        // backlog is asserted (bounded authoritative poll, not a sleep).
        await TripProcessWait.UntilAsync("trip consumers unregistered after kill", async () =>
            await TripProcessHarness.QueueConsumerCountAsync(rabbit.Factory, "trip.payment-outcome.q") == 0u,
            StateDeadline);

        // The Payment success (harness acting as the Payment authority
        // publisher with the saga's exact lineage) arrives while the Trip
        // service is DOWN.
        var outcome = TripProcessHarness.PaymentAuthorized(seed.TripId, seed.SagaId, seed.AuthorizeOperationKey);
        await TripProcessHarness.PublishAsync(rabbit.Factory, "payment.authorized.v1", outcome);

        // Durable backlog evidence: the quorum queue holds the outcome ready
        // with no consumer attached while the service is down.
        Assert.Equal(0u, await TripProcessHarness.QueueConsumerCountAsync(rabbit.Factory, "trip.payment-outcome.q"));
        Assert.Equal(1u, await TripProcessHarness.QueueMessageCountAsync(rabbit.Factory, "trip.payment-outcome.q"));

        await using (await TripProcess.StartAsync(connectionString, rabbit.Factory, "rel07-restart"))
        {
            // The SAME saga continues: identity, operationKey and step lineage
            // preserved; the backlogged outcome legally advances the ride.
            await TripProcessWait.UntilAsync("trip READY_TO_START after restart", async () =>
                (await TripProcessHarness.TripAsync(connectionString, seed.TripId)).State == TripState.ReadyToStart,
                StateDeadline);

            var saga = await TripProcessHarness.SagaAsync(connectionString, seed.TripId);
            Assert.Equal(seed.SagaId, saga.SagaId);
            Assert.Equal(seed.AuthorizeOperationKey, saga.AuthorizeOperationKey);
            Assert.Equal(RideSagaStatus.Active, saga.Status);
            Assert.Equal(RideSagaStep.Authorized, saga.CurrentStep);
            Assert.Equal(stepVersionBeforeKill + 1, saga.StepVersion); // one legal advance, nothing more.

            var receipt = await TripProcessHarness.ReceiptAsync(connectionString, outcome.MessageId);
            Assert.NotNull(receipt);
            Assert.Equal("Applied", receipt!.Disposition);

            // No second dispatch of the already-published authorize intent.
            var delivered = await TripProcessHarness.DrainQueueAsync(rabbit.Factory, "payment.commands.q");
            Assert.Single(delivered, m => m.MessageId == seed.AuthorizeMessageId);

            // ---- the restarted saga drives the FULL remaining lifecycle ----
            await TripProcessHarness.PublishAsync(rabbit.Factory, "driver.trip-started.v1",
                TripProcessHarness.DriverTripStarted(seed.TripId, seed.DriverId, seed.ReservationId));
            await TripProcessWait.UntilAsync("trip IN_PROGRESS", async () =>
                (await TripProcessHarness.TripAsync(connectionString, seed.TripId)).State == TripState.InProgress,
                StateDeadline);

            await TripProcessHarness.PublishAsync(rabbit.Factory, "driver.trip-completed.v1",
                TripProcessHarness.DriverTripCompleted(seed.TripId, seed.DriverId, seed.ReservationId));
            var captureMessageId = TripSagaIntents.CapturePaymentMessageId(seed.TripId);
            await TripProcessWait.UntilAsync("trip COMPLETED with CAPTURE_PENDING saga", async () =>
                (await TripProcessHarness.TripAsync(connectionString, seed.TripId)).State == TripState.Completed &&
                (await TripProcessHarness.SagaAsync(connectionString, seed.TripId)).CurrentStep ==
                    RideSagaStep.CapturePending, StateDeadline);
            await TripProcessWait.UntilAsync("capture intent PUBLISHED", async () =>
                (await TripProcessHarness.OutboxRowAsync(connectionString, captureMessageId))?.Status == "PUBLISHED",
                StateDeadline);

            var captured = await TripProcessHarness.DrainQueueAsync(rabbit.Factory, "payment.commands.q");
            var capture = Assert.Single(captured, m => m.MessageId == captureMessageId);
            Assert.Equal("CapturePaymentV1", capture.MessageType);
            Assert.Equal(RideSagaOperationKeys.Capture(seed.TripId), capture.OperationKey);
            Assert.Equal(seed.SagaId.ToString("D"), capture.SagaId);

            var captureOutcome = TripProcessHarness.PaymentCaptured(
                seed.TripId, seed.SagaId, RideSagaOperationKeys.Capture(seed.TripId));
            await TripProcessHarness.PublishAsync(rabbit.Factory, "payment.captured.v1", captureOutcome);
            await TripProcessWait.UntilAsync("saga terminal COMPLETED/CAPTURED", async () =>
            {
                var s = await TripProcessHarness.SagaAsync(connectionString, seed.TripId);
                return s.Status == RideSagaStatus.Completed && s.CurrentStep == RideSagaStep.Captured;
            }, StateDeadline);

            // ---- Terminal state is monotonic under late events ----
            var versionAtTerminal = (await TripProcessHarness.TripAsync(connectionString, seed.TripId)).Version;
            var sagaVersionAtTerminal = (await TripProcessHarness.SagaAsync(connectionString, seed.TripId)).StepVersion;

            var staleFailure = TripProcessHarness.PaymentAuthorizationDeclined(
                seed.TripId, seed.SagaId, seed.AuthorizeOperationKey);
            await TripProcessHarness.PublishAsync(rabbit.Factory, "payment.authorization-failed.v1", staleFailure);
            await TripProcessWait.UntilAsync("stale failure receipt", async () =>
                await TripProcessHarness.ReceiptAsync(connectionString, staleFailure.MessageId) is not null,
                StateDeadline);
            var staleReceipt = await TripProcessHarness.ReceiptAsync(connectionString, staleFailure.MessageId);
            Assert.Equal("NeedsReview", staleReceipt!.Disposition);
            Assert.Equal("STALE_PAYMENT_OUTCOME", staleReceipt.Reason);

            var duplicateCapture = TripProcessHarness.PaymentCaptured(
                seed.TripId, seed.SagaId, RideSagaOperationKeys.Capture(seed.TripId));
            await TripProcessHarness.PublishAsync(rabbit.Factory, "payment.captured.v1", duplicateCapture);
            await TripProcessWait.UntilAsync("duplicate capture receipt", async () =>
                await TripProcessHarness.ReceiptAsync(connectionString, duplicateCapture.MessageId) is not null,
                StateDeadline);
            Assert.Equal("BusinessReplay",
                (await TripProcessHarness.ReceiptAsync(connectionString, duplicateCapture.MessageId))!.Disposition);

            var tripAfterLate = await TripProcessHarness.TripAsync(connectionString, seed.TripId);
            var sagaAfterLate = await TripProcessHarness.SagaAsync(connectionString, seed.TripId);
            Assert.Equal(TripState.Completed, tripAfterLate.State); // COMPLETED never regresses.
            Assert.Equal(versionAtTerminal, tripAfterLate.Version);
            Assert.Equal(RideSagaStatus.Completed, sagaAfterLate.Status);
            Assert.Equal(sagaVersionAtTerminal, sagaAfterLate.StepVersion);
        }
    }

    // =====================================================================
    // — late DriverReserved on a cancelled trip + logically duplicate
    // delivery (fresh messageId, same authority fact)
    // =====================================================================

    [Fact]
    public async Task LateReservationAfterCancel_RealDelivery_ExactCleanupAndBusinessReplayDuplicate()
    {
        var connectionString = await NewScenarioDatabaseAsync("trip_w11_rel17");
        var seed = await TripProcessHarness.SeedCancelledDuringAuthorizeAsync(connectionString);
        await TripProcessHarness.PurgeQueuesAsync(rabbit.Factory, ManagedQueues);

        await using var host = await TripProcess.StartAsync(connectionString, rabbit.Factory, "rel17-late");

        var trip = await TripProcessHarness.TripAsync(connectionString, seed.TripId);
        Assert.Equal(TripState.Cancelled, trip.State);
        var versionBefore = trip.Version;
        var intentsBefore = await TripProcessHarness.OutboxCountAsync(connectionString, seed.TripId);

        // A late reservation fact for a NEW exact identity on the cancelled trip.
        var lateReservationId = Guid.NewGuid();
        var lateReserved = TripProcessHarness.DriverReserved(seed.TripId, seed.DriverId, lateReservationId);
        await TripProcessHarness.PublishAsync(rabbit.Factory, "driver.reserved.v1", lateReserved);
        await TripProcessWait.UntilAsync("late reservation receipt", async () =>
            await TripProcessHarness.ReceiptAsync(connectionString, lateReserved.MessageId) is not null,
            StateDeadline);
        Assert.Equal("Applied",
            (await TripProcessHarness.ReceiptAsync(connectionString, lateReserved.MessageId))!.Disposition);

        // CANCELLED never reactivates; the late fact created EXACT cleanup.
        var after = await TripProcessHarness.TripAsync(connectionString, seed.TripId);
        Assert.Equal(TripState.Cancelled, after.State);
        Assert.Equal(versionBefore + 1, after.Version);
        var releaseMessageId = $"trip:{seed.TripId:N}:release:{lateReservationId:N}";
        var release = await TripProcessHarness.OutboxRowAsync(connectionString, releaseMessageId);
        Assert.NotNull(release);
        Assert.Equal("ReleaseDriverReservationV1", release!.MessageType);
        Assert.Equal(seed.DriverId, TripProcessHarness.PayloadGuid(release, "driverId"));
        Assert.Equal(lateReservationId, TripProcessHarness.PayloadGuid(release, "reservationId"));
        Assert.Equal(intentsBefore + 1, await TripProcessHarness.OutboxCountAsync(connectionString, seed.TripId));

        // A logically duplicate delivery (fresh messageId, same authority
        // fact) is a durable BusinessReplay: no second business mutation and
        // no second cleanup intent (consumer half at process level).
        var duplicateFact = TripProcessHarness.DriverReserved(seed.TripId, seed.DriverId, lateReservationId);
        await TripProcessHarness.PublishAsync(rabbit.Factory, "driver.reserved.v1", duplicateFact);
        await TripProcessWait.UntilAsync("duplicate fact receipt", async () =>
            await TripProcessHarness.ReceiptAsync(connectionString, duplicateFact.MessageId) is not null,
            StateDeadline);
        Assert.Equal("BusinessReplay",
            (await TripProcessHarness.ReceiptAsync(connectionString, duplicateFact.MessageId))!.Disposition);

        var afterDuplicate = await TripProcessHarness.TripAsync(connectionString, seed.TripId);
        Assert.Equal(TripState.Cancelled, afterDuplicate.State);
        Assert.Equal(versionBefore + 1, afterDuplicate.Version);
        Assert.Equal(intentsBefore + 1, await TripProcessHarness.OutboxCountAsync(connectionString, seed.TripId));
        Assert.Equal(2, await CountReceiptsAsync(connectionString, seed.TripId));
    }

    // =====================================================================
    // Authorized after cancel: exact void compensation through the real
    // process (void leg then release leg)
    // =====================================================================

    [Fact]
    public async Task AuthorizedAfterCancel_VoidAndReleaseLegs_ExactIdentityToCompensated()
    {
        var connectionString = await NewScenarioDatabaseAsync("trip_w11_void");
        var seed = await TripProcessHarness.SeedCancelledDuringAuthorizeAsync(connectionString);
        await TripProcessHarness.PurgeQueuesAsync(rabbit.Factory, ManagedQueues);

        await using var host = await TripProcess.StartAsync(connectionString, rabbit.Factory, "void");

        // The authorization raced the cancellation and WON at Payment
        // authority: the hold exists, so an exact void with the frozen void
        // key is dispatched. The Trip stays CANCELLED.
        var authorized = TripProcessHarness.PaymentAuthorized(seed.TripId, seed.SagaId, seed.AuthorizeOperationKey);
        await TripProcessHarness.PublishAsync(rabbit.Factory, "payment.authorized.v1", authorized);
        await TripProcessWait.UntilAsync("saga VOID_PENDING", async () =>
            (await TripProcessHarness.SagaAsync(connectionString, seed.TripId)).CurrentStep == RideSagaStep.VoidPending,
            StateDeadline);

        var trip = await TripProcessHarness.TripAsync(connectionString, seed.TripId);
        var saga = await TripProcessHarness.SagaAsync(connectionString, seed.TripId);
        Assert.Equal(TripState.Cancelled, trip.State); // never revived.
        Assert.Equal(RideSagaStatus.Compensating, saga.Status);
        Assert.Equal(RideSagaOperationKeys.Void(seed.TripId), saga.VoidOperationKey);

        // The void command carries the frozen lineage through the real broker.
        var voidMessageId = TripSagaIntents.VoidPaymentAuthorizationMessageId(seed.TripId);
        await TripProcessWait.UntilAsync("void intent PUBLISHED", async () =>
            (await TripProcessHarness.OutboxRowAsync(connectionString, voidMessageId))?.Status == "PUBLISHED",
            StateDeadline);
        var voided = Assert.Single(
            (await TripProcessHarness.DrainQueueAsync(rabbit.Factory, "payment.commands.q")),
            m => m.MessageId == voidMessageId);
        Assert.Equal(RideSagaOperationKeys.Void(seed.TripId), voided.OperationKey);
        Assert.Equal(seed.SagaId.ToString("D"), voided.SagaId);

        // Payment authority confirms the void (harness publisher, exact key).
        var voidOutcome = TripProcessHarness.PaymentAuthorizationVoided(
            seed.TripId, seed.SagaId, RideSagaOperationKeys.Void(seed.TripId));
        await TripProcessHarness.PublishAsync(rabbit.Factory, "payment.authorization-voided.v1", voidOutcome);
        await TripProcessWait.UntilAsync("saga RELEASE_PENDING after void", async () =>
            (await TripProcessHarness.SagaAsync(connectionString, seed.TripId)).CurrentStep ==
                RideSagaStep.ReleasePending, StateDeadline);

        // The release leg resolves with the EXACT reservation identity.
        var released = TripProcessHarness.DriverReservationReleased(seed.TripId, seed.DriverId, seed.ReservationId);
        await TripProcessHarness.PublishAsync(rabbit.Factory, "driver.reservation-released.v1", released);
        await TripProcessWait.UntilAsync("saga terminal COMPENSATED", async () =>
            (await TripProcessHarness.SagaAsync(connectionString, seed.TripId)).Status == RideSagaStatus.Compensated,
            StateDeadline);

        var finalSaga = await TripProcessHarness.SagaAsync(connectionString, seed.TripId);
        var finalTrip = await TripProcessHarness.TripAsync(connectionString, seed.TripId);
        Assert.Equal(RideSagaStatus.Compensated, finalSaga.Status); // both legs resolved.
        Assert.Equal(TripState.Cancelled, finalTrip.State);
        Assert.Equal("Applied", (await TripProcessHarness.ReceiptAsync(connectionString, released.MessageId))!
            .Disposition);
        Assert.Equal("Applied", (await TripProcessHarness.ReceiptAsync(connectionString, voidOutcome.MessageId))!
            .Disposition);
    }

    // =====================================================================
    // Crash with compensation obligations outstanding
    // =====================================================================

    [Fact]
    public async Task CompensationOutstanding_ProcessCrashed_BackloggedConfirmationResolvesAfterRestart()
    {
        var connectionString = await NewScenarioDatabaseAsync("trip_w11_comp");
        var seed = await TripProcessHarness.SeedDriverAcceptedAuthorizePendingAsync(connectionString);
        await TripProcessHarness.PurgeQueuesAsync(rabbit.Factory, ManagedQueues);

        var releaseMessageId = $"trip:{seed.TripId:N}:release:{seed.ReservationId:N}";

        await using (var first = await TripProcess.StartAsync(connectionString, rabbit.Factory, "comp-first"))
        {
            // The authorize command is dispatched, then Payment authority
            // returns a terminal DECLINED: the ride
            // cancels and compensation obligations become outstanding.
            await TripProcessWait.UntilAsync("authorize intent PUBLISHED", async () =>
                (await TripProcessHarness.OutboxRowAsync(connectionString, seed.AuthorizeMessageId))?.Status ==
                "PUBLISHED", StateDeadline);
            var declined = TripProcessHarness.PaymentAuthorizationDeclined(
                seed.TripId, seed.SagaId, seed.AuthorizeOperationKey);
            await TripProcessHarness.PublishAsync(rabbit.Factory, "payment.authorization-failed.v1", declined);
            await TripProcessWait.UntilAsync("trip CANCELLED with release obligation", async () =>
                (await TripProcessHarness.TripAsync(connectionString, seed.TripId)).State == TripState.Cancelled &&
                await TripProcessHarness.OutboxRowAsync(connectionString, releaseMessageId) is not null,
                StateDeadline);
            Assert.Equal("Applied",
                (await TripProcessHarness.ReceiptAsync(connectionString, declined.MessageId))!.Disposition);

            // Crash with the release obligation unresolved.
            await first.KillAsync();
        }

        // The broker unregisters the dead consumers before the backlog is
        // asserted (bounded authoritative poll, not a sleep).
        await TripProcessWait.UntilAsync("trip consumers unregistered after kill", async () =>
            await TripProcessHarness.QueueConsumerCountAsync(rabbit.Factory, "trip.driver-state.q") == 0u,
            StateDeadline);

        // While the Trip service is DOWN, Driver (harness publisher) confirms
        // the exact release; the confirmation durably backlogs.
        var released = TripProcessHarness.DriverReservationReleased(seed.TripId, seed.DriverId, seed.ReservationId);
        await TripProcessHarness.PublishAsync(rabbit.Factory, "driver.reservation-released.v1", released);
        Assert.Equal(0u, await TripProcessHarness.QueueConsumerCountAsync(rabbit.Factory, "trip.driver-state.q"));
        Assert.Equal(1u, await TripProcessHarness.QueueMessageCountAsync(rabbit.Factory, "trip.driver-state.q"));

        await using (await TripProcess.StartAsync(connectionString, rabbit.Factory, "comp-restart"))
        {
            await TripProcessWait.UntilAsync("saga COMPENSATED after restart", async () =>
                (await TripProcessHarness.SagaAsync(connectionString, seed.TripId)).Status ==
                    RideSagaStatus.Compensated, StateDeadline);

            var saga = await TripProcessHarness.SagaAsync(connectionString, seed.TripId);
            var trip = await TripProcessHarness.TripAsync(connectionString, seed.TripId);
            Assert.Equal(RideSagaStatus.Compensated, saga.Status);
            Assert.Equal(TripState.Cancelled, trip.State); // terminal monotonicity.
            Assert.Equal(seed.SagaId, saga.SagaId);
            Assert.Equal(seed.AuthorizeOperationKey, saga.AuthorizeOperationKey);

            // The exact reservation identity was released, the release command
            // was dispatched by the restarted relay, and no void obligation
            // ever existed on the DECLINED path.
            await TripProcessWait.UntilAsync("release intent PUBLISHED after restart", async () =>
                (await TripProcessHarness.OutboxRowAsync(connectionString, releaseMessageId))?.Status == "PUBLISHED",
                StateDeadline);
            var release = await TripProcessHarness.OutboxRowAsync(connectionString, releaseMessageId);
            Assert.Equal(seed.ReservationId, TripProcessHarness.PayloadGuid(release!, "reservationId"));
            Assert.Null(await TripProcessHarness.OutboxRowAsync(
                connectionString, TripSagaIntents.VoidPaymentAuthorizationMessageId(seed.TripId)));

            Assert.Equal("Applied",
                (await TripProcessHarness.ReceiptAsync(connectionString, released.MessageId))!.Disposition);
        }
    }

    private static async Task<int> CountReceiptsAsync(string connectionString, Guid tripId)
    {
        await using var db = TripProcessHarness.Context(connectionString);
        return await db.MessageReceipts.AsNoTracking().CountAsync(r => r.TripId == tripId);
    }
}
