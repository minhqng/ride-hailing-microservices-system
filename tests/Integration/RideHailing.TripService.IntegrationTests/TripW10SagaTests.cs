// W10 persisted Ride Saga proofs (A-W10-01..11, REL-09, REL-10).
//
// ONE PROPERTY: every saga step is a durable fact committed with exactly the
// Trip transition and outgoing intent it requires, and no duplicate, stale,
// uncertain or out-of-order Payment/Driver fact can move persisted state
// outside the frozen lineage (Doc 05A §18, Doc 02 §14, DSI-026..034).
//
// Each test runs against its own disposable PostgreSQL probe database and
// every stage opens a FRESH DbContext, so each step proves restart recovery
// from persisted state alone (Doc 02 §14.7). Payment outcomes arrive as
// authoritative envelopes through the real TripMessageHandler transaction
// (receipt + Trip + saga + intents in one commit). The Payment-side command
// consumer/provider (C-W10) and the Driver-side command consumer (B-W10)
// are cross-owner pending; their execution is not faked here.
//
// WHAT IS PROVEN HERE (A-side):
//   * ride_sagas migration contract (fresh chain + W09 upgrade);
//   * DriverAccepted commits Trip + Saga + AuthorizePayment atomically, and
//     a duplicate acceptance never creates a second logical authorize;
//   * PaymentAuthorized under the exact operationKey readies the ride once;
//     wrong/missing lineage, duplicates and stale outcomes never advance;
//   * DECLINED compensates with the exact release identity (REL-09);
//     uncertain outcomes never compensate and keep the same key;
//   * cancel-race authorization dispatches the exact void and completes
//     compensation only when both obligations resolve;
//   * Start/Complete emit the durable Driver commands and capture only after
//     the completion pivot (DSI-027);
//   * capture failure/uncertainty never regresses COMPLETED (REL-10, DSI-028)
//     and never mints a second logical capture;
//   * conflicting concurrent outcomes resolve as one winner + one OCC loser;
//   * the full happy-ride A-leg reaches saga COMPLETED with one consistent
//     command sequence.

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using RideHailing.Messaging.Contracts;
using RideHailing.Messaging.Reliability;
using RideHailing.TripService.Application.Trips.Lifecycle;
using RideHailing.TripService.Domain.Sagas;
using RideHailing.TripService.Domain.Trips;
using RideHailing.TripService.Infrastructure.Persistence;
using RideHailing.TripService.Integration.Messaging;

namespace RideHailing.TripService.IntegrationTests;

[Collection(TripPostgresCollection.Name)]
public sealed class TripW10SagaTests(TripPostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 3, 0, 0, TimeSpan.Zero);

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    // ----------------------------------------------------------------- setup

    private static TripDbContext Context(string connectionString, params Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor[] interceptors) =>
        new(new DbContextOptionsBuilder<TripDbContext>().UseNpgsql(connectionString)
            .AddInterceptors(interceptors).Options);

    private static T Handler<T>(TripDbContext db) where T : class =>
        (T)Activator.CreateInstance(typeof(T), db, new Clock(),
            NullLogger<T>.Instance)!;

    private async Task<string> NewProbeAsync(string prefix)
    {
        var name = $"{prefix}_{Guid.NewGuid():N}";
        var maintenance = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        {
            Database = "postgres",
        }.ToString();
        await using (var db = Context(maintenance))
        {
            var connection = db.Database.GetDbConnection();
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE \"{name}\"";
            await command.ExecuteNonQueryAsync();
        }

        var connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        {
            Database = name,
        }.ToString();
        await using (var db = Context(connectionString))
        {
            await db.Database.MigrateAsync();
        }

        return connectionString;
    }

    private static readonly Guid DriverId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ReservationId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    /// <summary>Seeds a MATCHING trip through the real StartMatching intake.</summary>
    private static async Task<Guid> SeedMatchingAsync(string cs)
    {
        var tripId = Guid.NewGuid();
        var riderId = Guid.NewGuid();
        await using var db = Context(cs);
        db.Trips.Add(Trip.Create(tripId, riderId, new(10.7, 106.6), new(10.8, 106.7),
            new("VND", 50000m, "w10", Now), Now));
        await db.SaveChangesAsync();
        var matching = Handler<StartMatchingHandler>(db);
        Assert.True((await matching.HandleAsync(tripId, riderId)).IsSuccess);
        return tripId;
    }

    /// <summary>Drives the durable core through DriverReserved + DriverAccepted.</summary>
    private static async Task<RideSaga> DriveAcceptedAsync(string cs, Guid tripId)
    {
        await using (var db = Context(cs))
        {
            var reserve = Handler<RecordDriverReservationHandler>(db);
            Assert.True((await reserve.HandleAsync(tripId, DriverId, ReservationId, 1)).IsSuccess);
        }

        await using (var db = Context(cs))
        {
            var accept = Handler<RecordDriverAcceptedHandler>(db);
            var accepted = await accept.HandleAsync(tripId, DriverId, ReservationId);
            Assert.True(accepted.IsSuccess);
            Assert.Equal(TripState.DriverAccepted, accepted.State);
        }

        await using var verify = Context(cs);
        return await verify.RideSagas.AsNoTracking().SingleAsync(s => s.TripId == tripId);
    }

    private static MessageEnvelope<JsonElement> PaymentEnvelope(
        string messageType, Guid tripId, string? operationKey, string? sagaId, string? reasonCode = null,
        string? messageId = null)
    {
        var payload = new Dictionary<string, object> { ["tripId"] = tripId };
        if (reasonCode is not null)
        {
            payload["reasonCode"] = reasonCode;
        }

        return MessageEnvelope<JsonElement>.Create(
            messageType, tripId.ToString("D"), JsonSerializer.SerializeToElement(payload),
            messageId: messageId ?? $"proof-{Guid.NewGuid():N}", occurredAt: Now,
            sagaId: sagaId, operationKey: operationKey);
    }

    private static async Task<TripMessageReceipt> DeliverAsync(
        string cs, string consumer, MessageEnvelope<JsonElement> envelope)
    {
        await using var db = Context(cs);
        var handler = new TripMessageHandler(db, new Clock(), NullLogger<TripMessageHandler>.Instance);
        await handler.HandleAsync(consumer, envelope, CancellationToken.None);
        await using var verify = Context(cs);
        return await verify.MessageReceipts.AsNoTracking().SingleAsync(
            r => r.Consumer == consumer && r.MessageId == envelope.MessageId);
    }

    private static async Task<Trip> TripAsync(string cs, Guid tripId)
    {
        await using var db = Context(cs);
        return await db.Trips.AsNoTracking().Include(t => t.CleanupIntents).SingleAsync(t => t.TripId == tripId);
    }

    private static async Task<RideSaga> SagaAsync(string cs, Guid tripId)
    {
        await using var db = Context(cs);
        return await db.RideSagas.AsNoTracking().SingleAsync(s => s.TripId == tripId);
    }

    private static async Task<List<TripOutboxMessage>> IntentsAsync(string cs, Guid tripId)
    {
        await using var db = Context(cs);
        return await db.OutboxMessages.AsNoTracking()
            .Where(m => m.AggregateId == tripId.ToString("D"))
            .OrderBy(m => m.OccurredAt).ThenBy(m => m.MessageId)
            .ToListAsync();
    }

    private static async Task RideReadyAsync(string cs, Guid tripId, RideSaga saga)
    {
        var receipt = await DeliverAsync(cs, "trip.payment-outcome.q",
            PaymentEnvelope("PaymentAuthorizedV1", tripId, saga.AuthorizeOperationKey, saga.SagaId.ToString("D")));
        Assert.Equal("Applied", receipt.Disposition);
    }

    private static async Task<RideSaga> RideStartedAsync(string cs, Guid tripId)
    {
        await using var db = Context(cs);
        var start = Handler<StartTripHandler>(db);
        var result = await start.HandleAsync(tripId, DriverId, ReservationId);
        Assert.True(result.IsSuccess);
        Assert.Equal(TripState.InProgress, result.State);
        return await SagaAsync(cs, tripId);
    }

    private static async Task<RideSaga> RideCompletedAsync(string cs, Guid tripId)
    {
        await using var db = Context(cs);
        var complete = Handler<CompleteTripHandler>(db);
        var result = await complete.HandleAsync(tripId, DriverId, ReservationId);
        Assert.True(result.IsSuccess);
        Assert.Equal(TripState.Completed, result.State);
        return await SagaAsync(cs, tripId);
    }

    // ------------------------------------------------------- migration proof

    [Fact]
    public async Task SagaSchema_ExistsAfterFreshChainAndW09Upgrade_WithLineageChecks()
    {
        var cs = await NewProbeAsync("trip_w10_schema");
        await using var db = Context(cs);
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await connection.OpenAsync();

        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT conname FROM pg_constraint c JOIN pg_class t ON t.oid=c.conrelid " +
                "WHERE t.relname='ride_sagas' AND c.contype IN ('c','p') ORDER BY conname";
            var names = new List<string>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) names.Add(reader.GetString(0));
            Assert.Contains("PK_ride_sagas", names);
            Assert.Contains("CK_ride_sagas_operation_lineage", names);
            Assert.Contains("CK_ride_sagas_status_domain", names);
            Assert.Contains("CK_ride_sagas_step_domain", names);
            Assert.Contains("CK_ride_sagas_step_key_agreement", names);
        }

        // The storage lineage CHECK rejects a foreign operation key outright.
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT count(*) FROM pg_indexes WHERE indexname='UX_ride_sagas_one_per_trip' AND indexdef LIKE '%UNIQUE%'";
            Assert.Equal(1L, await command.ExecuteScalarAsync());
        }

        // Upgrade path: a W09-schema database (pre-saga) migrates forward
        // without touching existing rows.
        var upgradeCs = await NewProbeAsync("trip_w10_upgrade");
        Guid tripId;
        await using (var db2 = Context(upgradeCs))
        {
            await db2.Database.MigrateAsync("20261025031423_W09_TripOutbox");
            var trip = Trip.Create(Guid.NewGuid(), Guid.NewGuid(), new(10.7, 106.6), new(10.8, 106.7),
                new("VND", 50000m, "w10", Now), Now);
            trip.StartMatching(Now);
            db2.Trips.Add(trip);
            db2.OutboxMessages.Add(TripOutboxMessage.From(TripOutboxIntents.RideRequested(trip, Now)));
            await db2.SaveChangesAsync();
            tripId = trip.TripId;
        }

        await using (var db2 = Context(upgradeCs))
        {
            await db2.Database.MigrateAsync();
            Assert.True(await db2.Trips.AsNoTracking().AnyAsync(t => t.TripId == tripId));
            Assert.False(await db2.RideSagas.AsNoTracking().AnyAsync());
        }
    }

    // ------------------------------------------------- A-W10-02 atomicity

    [Fact]
    public async Task DriverAccepted_CommitsTripSagaAndAuthorizeAtomically_DuplicateNeverReauthorizes()
    {
        var cs = await NewProbeAsync("trip_w10_accept");
        var tripId = await SeedMatchingAsync(cs);
        var saga = await DriveAcceptedAsync(cs, tripId);

        Assert.Equal(RideSagaStatus.Active, saga.Status);
        Assert.Equal(RideSagaStep.AuthorizePending, saga.CurrentStep);
        Assert.Equal(ReservationId, saga.ReservationId);
        Assert.Equal(RideSagaOperationKeys.Authorize(tripId), saga.AuthorizeOperationKey);

        var intents = await IntentsAsync(cs, tripId);
        var authorize = Assert.Single(intents, m => m.MessageType == "AuthorizePaymentV1");
        Assert.Equal(TripSagaIntents.AuthorizePaymentMessageId(tripId), authorize.MessageId);
        Assert.Equal(RideSagaOperationKeys.Authorize(tripId), authorize.OperationKey);
        Assert.Equal(saga.SagaId.ToString("D"), authorize.SagaId);
        Assert.Equal("ride.commands", authorize.Exchange);
        Assert.Equal("payment.authorize.v1", authorize.RoutingKey);
        Assert.Equal("PENDING", authorize.Status);

        // A duplicate acceptance (fresh context = restart) is a replay: no
        // second saga, no second logical authorize, no version churn.
        await using (var db = Context(cs))
        {
            var accept = Handler<RecordDriverAcceptedHandler>(db);
            var replay = await accept.HandleAsync(tripId, DriverId, ReservationId);
            Assert.True(replay.IsSuccess);
            Assert.True(replay.IsReplay);
        }

        Assert.Equal(1, (await IntentsAsync(cs, tripId)).Count(m => m.MessageType == "AuthorizePaymentV1"));
        var unchanged = await SagaAsync(cs, tripId);
        Assert.Equal(saga.StepVersion, unchanged.StepVersion);
    }

    // --------------------------------------------- A-W10-03 authorized path

    [Fact]
    public async Task PaymentAuthorized_UnderExactLineage_ReadiesTripOnce_RejectsForeignLineage()
    {
        var cs = await NewProbeAsync("trip_w10_authorized");
        var tripId = await SeedMatchingAsync(cs);
        var saga = await DriveAcceptedAsync(cs, tripId);

        // Foreign operationKey: a different key never authorizes this saga.
        var foreign = await DeliverAsync(cs, "trip.payment-outcome.q",
            PaymentEnvelope("PaymentAuthorizedV1", tripId, $"{tripId:D}:authorize:v9", saga.SagaId.ToString("D")));
        Assert.Equal("NeedsReview", foreign.Disposition);
        Assert.Equal("PAYMENT_OPERATION_MISMATCH", foreign.Reason);
        Assert.Equal(TripState.DriverAccepted, (await TripAsync(cs, tripId)).State);

        // Missing lineage entirely: with a saga awaiting its step, an envelope
        // without the operationKey is a lineage violation needing review — a
        // tripId alone never authorizes (the W8 rule, now stronger).
        var naked = await DeliverAsync(cs, "trip.payment-outcome.q",
            PaymentEnvelope("PaymentAuthorizedV1", tripId, null, null));
        Assert.Equal("NeedsReview", naked.Disposition);
        Assert.Equal("PAYMENT_OPERATION_MISMATCH", naked.Reason);
        Assert.Equal(TripState.DriverAccepted, (await TripAsync(cs, tripId)).State);

        // The exact lineage readies the ride (DSI-026 prerequisite of start).
        await RideReadyAsync(cs, tripId, saga);
        Assert.Equal(TripState.ReadyToStart, (await TripAsync(cs, tripId)).State);
        var ready = await SagaAsync(cs, tripId);
        Assert.Equal(RideSagaStep.Authorized, ready.CurrentStep);

        // A duplicate success under a FRESH messageId is a business replay:
        // no second advance, no extra intent.
        var duplicate = await DeliverAsync(cs, "trip.payment-outcome.q",
            PaymentEnvelope("PaymentAuthorizedV1", tripId, saga.AuthorizeOperationKey,
                saga.SagaId.ToString("D"), messageId: $"dup-{Guid.NewGuid():N}"));
        Assert.Equal("BusinessReplay", duplicate.Disposition);
        Assert.Equal(ready.StepVersion, (await SagaAsync(cs, tripId)).StepVersion);
        Assert.Equal(TripState.ReadyToStart, (await TripAsync(cs, tripId)).State);
    }

    // ------------------------------------------------ A-W10-04 / REL-09

    [Fact]
    public async Task DeclinedAuthorization_CompensatesWithExactRelease_UncertainNeverCompensates()
    {
        var cs = await NewProbeAsync("trip_w10_rel9");
        var tripId = await SeedMatchingAsync(cs);
        var saga = await DriveAcceptedAsync(cs, tripId);

        // UNCERTAIN outcome first: transient provider trouble is never a
        // terminal failure (DECLINED ≠ UNKNOWN); the saga keeps waiting on
        // the SAME key and the ride state is untouched.
        var uncertain = await DeliverAsync(cs, "trip.payment-outcome.q",
            PaymentEnvelope("PaymentAuthorizationFailedV1", tripId, saga.AuthorizeOperationKey,
                saga.SagaId.ToString("D"), reasonCode: "TRANSIENT_FAILURE"));
        Assert.Equal("PendingWorkflow", uncertain.Disposition);
        Assert.Equal("PAYMENT_OUTCOME_UNRESOLVED", uncertain.Reason);
        var awaiting = await SagaAsync(cs, tripId);
        Assert.Equal(RideSagaStep.AuthorizePending, awaiting.CurrentStep);
        Assert.Equal(RideSagaStatus.Active, awaiting.Status);
        Assert.Equal("TRANSIENT_FAILURE", awaiting.LastErrorCode);
        Assert.Equal(saga.AuthorizeOperationKey, awaiting.AuthorizeOperationKey);
        Assert.Equal(TripState.DriverAccepted, (await TripAsync(cs, tripId)).State);
        Assert.DoesNotContain(await IntentsAsync(cs, tripId), m => m.MessageType.Contains("Release"));

        // The terminal DECLINED fact then cancels the ride, persists the
        // compensation step and stages the EXACT release identity (REL-09).
        var declined = await DeliverAsync(cs, "trip.payment-outcome.q",
            PaymentEnvelope("PaymentAuthorizationFailedV1", tripId, saga.AuthorizeOperationKey,
                saga.SagaId.ToString("D"), reasonCode: "DECLINED"));
        Assert.Equal("Applied", declined.Disposition);

        var trip = await TripAsync(cs, tripId);
        Assert.Equal(TripState.Cancelled, trip.State);
        var compensating = await SagaAsync(cs, tripId);
        Assert.Equal(RideSagaStatus.Compensating, compensating.Status);
        Assert.Equal(RideSagaStep.ReleasePending, compensating.CurrentStep);

        var intents = await IntentsAsync(cs, tripId);
        var release = Assert.Single(intents, m => m.MessageType == "ReleaseDriverReservationV1");
        var releasePayload = JsonDocument.Parse(release.PayloadJson).RootElement;
        // Exact identity tuple — never a Driver id alone (DSI-033).
        Assert.Equal(tripId, releasePayload.GetProperty("tripId").GetGuid());
        Assert.Equal(DriverId, releasePayload.GetProperty("driverId").GetGuid());
        Assert.Equal(ReservationId, releasePayload.GetProperty("reservationId").GetGuid());
        Assert.Equal(1, releasePayload.GetProperty("matchingRound").GetInt32());
        Assert.Single(intents, m => m.MessageType == "CancelMatchingRoundV1");
        Assert.Single(intents, m => m.MessageType == "AuthorizePaymentV1");
        // No capture/void was ever minted: compensation is release-only.
        Assert.DoesNotContain(intents, m => m.MessageType is "CapturePaymentV1" or "VoidPaymentAuthorizationV1");
        // DECLINED is conclusive no-effect evidence: the reconcile obligation
        // is discharged in the same commit.
        Assert.All(trip.CleanupIntents.Where(i => i.Kind == TripCleanupKind.ReconcilePayment),
            i => Assert.NotNull(i.CompletedAt));

        // A late Driver release confirmation finishes the compensation.
        var released = await DeliverAsync(cs, "trip.driver-state.q",
            MessageEnvelope<JsonElement>.Create("DriverReservationReleasedV1", tripId.ToString("D"),
                JsonSerializer.SerializeToElement(new
                {
                    tripId, driverId = DriverId, reservationId = ReservationId,
                })));
        Assert.Equal("Applied", released.Disposition);
        Assert.Equal(RideSagaStatus.Compensated, (await SagaAsync(cs, tripId)).Status);
    }

    [Fact]
    public async Task DeclinedAfterCancel_ReleasesWithoutVoid_AndStaleSuccessCannotRevive()
    {
        var cs = await NewProbeAsync("trip_w10_rel9_cancel_first");
        var tripId = await SeedMatchingAsync(cs);
        var saga = await DriveAcceptedAsync(cs, tripId);

        // Rider cancels while the authorize outcome is still in flight.
        await using (var db = Context(cs))
        {
            var trip = await db.Trips.SingleAsync(t => t.TripId == tripId);
            var cancel = Handler<CancelRideHandler>(db);
            Assert.True((await cancel.HandleAsync(tripId, trip.RiderId)).IsSuccess);
        }

        var awaiting = await SagaAsync(cs, tripId);
        Assert.Equal(RideSagaStatus.Compensating, awaiting.Status);
        Assert.Equal(RideSagaStep.AuthorizePending, awaiting.CurrentStep);

        // The outcome then resolves DECLINED: no effect existed, so no void.
        var declined = await DeliverAsync(cs, "trip.payment-outcome.q",
            PaymentEnvelope("PaymentAuthorizationFailedV1", tripId, saga.AuthorizeOperationKey,
                saga.SagaId.ToString("D"), reasonCode: "DECLINED"));
        Assert.Equal("Applied", declined.Disposition);
        var afterDecline = await SagaAsync(cs, tripId);
        Assert.Equal(RideSagaStep.ReleasePending, afterDecline.CurrentStep);
        Assert.Null(afterDecline.VoidOperationKey);
        Assert.DoesNotContain(await IntentsAsync(cs, tripId), m => m.MessageType == "VoidPaymentAuthorizationV1");
        Assert.Equal(TripState.Cancelled, (await TripAsync(cs, tripId)).State);
    }

    // ------------------------------------------------------- cancel race void

    [Fact]
    public async Task AuthorizedAfterCancel_DispatchesExactVoid_CompensationNeedsBothHalves()
    {
        var cs = await NewProbeAsync("trip_w10_void");
        var tripId = await SeedMatchingAsync(cs);
        var saga = await DriveAcceptedAsync(cs, tripId);

        Guid riderId;
        await using (var db = Context(cs))
        {
            var trip = await db.Trips.SingleAsync(t => t.TripId == tripId);
            riderId = trip.RiderId;
            var cancel = Handler<CancelRideHandler>(db);
            Assert.True((await cancel.HandleAsync(tripId, riderId)).IsSuccess);
        }

        // The authorization won the race at Payment authority: an exact void
        // with the stable void key is dispatched and the ride stays CANCELLED.
        var authorized = await DeliverAsync(cs, "trip.payment-outcome.q",
            PaymentEnvelope("PaymentAuthorizedV1", tripId, saga.AuthorizeOperationKey, saga.SagaId.ToString("D")));
        Assert.Equal("Applied", authorized.Disposition);
        Assert.Equal(TripState.Cancelled, (await TripAsync(cs, tripId)).State);

        var voiding = await SagaAsync(cs, tripId);
        Assert.Equal(RideSagaStatus.Compensating, voiding.Status);
        Assert.Equal(RideSagaStep.VoidPending, voiding.CurrentStep);
        Assert.Equal(RideSagaOperationKeys.Void(tripId), voiding.VoidOperationKey);
        var voidIntent = Assert.Single(await IntentsAsync(cs, tripId),
            m => m.MessageType == "VoidPaymentAuthorizationV1");
        Assert.Equal(RideSagaOperationKeys.Void(tripId), voidIntent.OperationKey);
        Assert.Equal(saga.SagaId.ToString("D"), voidIntent.SagaId);

        // A foreign void lineage never resolves this saga.
        var foreignVoid = await DeliverAsync(cs, "trip.payment-outcome.q",
            PaymentEnvelope("PaymentAuthorizationVoidedV1", tripId, $"{tripId:D}:void:v9",
                saga.SagaId.ToString("D")));
        Assert.Equal("NeedsReview", foreignVoid.Disposition);

        // Driver confirms the exact release first: the saga still waits on
        // the void outcome (both obligations must resolve).
        await DeliverAsync(cs, "trip.driver-state.q",
            MessageEnvelope<JsonElement>.Create("DriverReservationReleasedV1", tripId.ToString("D"),
                JsonSerializer.SerializeToElement(new { tripId, driverId = DriverId, reservationId = ReservationId })));
        Assert.Equal(RideSagaStatus.Compensating, (await SagaAsync(cs, tripId)).Status);

        // Payment confirms the void: compensation completes atomically-honest.
        var voided = await DeliverAsync(cs, "trip.payment-outcome.q",
            PaymentEnvelope("PaymentAuthorizationVoidedV1", tripId, RideSagaOperationKeys.Void(tripId),
                saga.SagaId.ToString("D")));
        Assert.Equal("Applied", voided.Disposition);
        Assert.Equal(RideSagaStatus.Compensated, (await SagaAsync(cs, tripId)).Status);
        var finalTrip = await TripAsync(cs, tripId);
        Assert.All(finalTrip.CleanupIntents.Where(i => i.Kind != TripCleanupKind.CancelMatching),
            i => Assert.NotNull(i.CompletedAt));
    }

    // -------------------------------------------- A-W10-05/06/07 commands

    [Fact]
    public async Task StartRequiresAuthorization_CompleteDispatchesCaptureOnce_WithDriverCommands()
    {
        var cs = await NewProbeAsync("trip_w10_progress");
        var tripId = await SeedMatchingAsync(cs);
        var saga = await DriveAcceptedAsync(cs, tripId);

        // Start before authoritative authorization: rejected, no driver
        // command, no mutation (DSI-026 via the saga's ride phase).
        await using (var db = Context(cs))
        {
            var start = Handler<StartTripHandler>(db);
            var refused = await start.HandleAsync(tripId, DriverId, ReservationId);
            Assert.False(refused.IsSuccess);
        }

        Assert.Equal(TripState.DriverAccepted, (await TripAsync(cs, tripId)).State);
        Assert.DoesNotContain(await IntentsAsync(cs, tripId), m => m.MessageType == "MarkDriverTripStartedV1");

        await RideReadyAsync(cs, tripId, saga);
        var started = await RideStartedAsync(cs, tripId);
        Assert.Equal(RideSagaStep.Authorized, started.CurrentStep);
        var markStarted = Assert.Single(await IntentsAsync(cs, tripId),
            m => m.MessageType == "MarkDriverTripStartedV1");
        Assert.Equal($"trip:{tripId:N}:mark-started:{ReservationId:N}", markStarted.MessageId);

        // A duplicate start replays without a second command.
        await using (var db = Context(cs))
        {
            var start = Handler<StartTripHandler>(db);
            Assert.True((await start.HandleAsync(tripId, DriverId, ReservationId)).IsReplay);
        }

        Assert.Equal(1, (await IntentsAsync(cs, tripId)).Count(m => m.MessageType == "MarkDriverTripStartedV1"));

        // Complete: capture ONLY now, with the frozen capture lineage, plus
        // the driver completion command (DSI-027).
        var capturing = await RideCompletedAsync(cs, tripId);
        Assert.Equal(RideSagaStep.CapturePending, capturing.CurrentStep);
        Assert.Equal(RideSagaOperationKeys.Capture(tripId), capturing.CaptureOperationKey);
        var intents = await IntentsAsync(cs, tripId);
        var capture = Assert.Single(intents, m => m.MessageType == "CapturePaymentV1");
        Assert.Equal(RideSagaOperationKeys.Capture(tripId), capture.OperationKey);
        Assert.Equal(saga.SagaId.ToString("D"), capture.SagaId);
        Assert.Equal("payment.capture.v1", capture.RoutingKey);
        Assert.Single(intents, m => m.MessageType == "MarkDriverTripCompletedV1");

        // Driver's authoritative completion fact arrives late: agreement
        // confirmation only — never a second capture (one logical effect).
        var confirmation = await DeliverAsync(cs, "trip.driver-state.q",
            MessageEnvelope<JsonElement>.Create("DriverTripCompletedV1", tripId.ToString("D"),
                JsonSerializer.SerializeToElement(new
                {
                    tripId, driverId = DriverId, reservationId = ReservationId, status = "AVAILABLE",
                })));
        Assert.Equal("BusinessReplay", confirmation.Disposition);
        Assert.Equal(1, (await IntentsAsync(cs, tripId)).Count(m => m.MessageType == "CapturePaymentV1"));
        Assert.Equal(RideSagaStep.CapturePending, (await SagaAsync(cs, tripId)).CurrentStep);
    }

    // --------------------------------------------- A-W10-08 / REL-10 / DSI-028

    [Fact]
    public async Task CaptureFailure_AfterCompletion_NeverRegressesTheRide_NeverReMintsTheKey()
    {
        var cs = await NewProbeAsync("trip_w10_rel10");
        var tripId = await SeedMatchingAsync(cs);
        var saga = await DriveAcceptedAsync(cs, tripId);
        await RideReadyAsync(cs, tripId, saga);
        await RideStartedAsync(cs, tripId);
        var capturing = await RideCompletedAsync(cs, tripId);
        var captureKey = capturing.CaptureOperationKey;
        Assert.Equal(TripState.Completed, (await TripAsync(cs, tripId)).State);

        // Transient capture trouble: durable observation, ride untouched.
        var transient = await DeliverAsync(cs, "trip.payment-outcome.q",
            PaymentEnvelope("PaymentCapturePendingV1", tripId, captureKey, saga.SagaId.ToString("D"),
                reasonCode: "TRANSIENT_FAILURE"));
        Assert.Equal("Applied", transient.Disposition);
        var pending = await SagaAsync(cs, tripId);
        Assert.Equal(RideSagaStep.CapturePending, pending.CurrentStep);
        Assert.Equal(RideSagaStatus.Active, pending.Status);
        Assert.Equal(captureKey, pending.CaptureOperationKey);
        Assert.Equal(TripState.Completed, (await TripAsync(cs, tripId)).State);

        // Explicit review: visible resolution state, ride still untouched.
        var review = await DeliverAsync(cs, "trip.payment-outcome.q",
            PaymentEnvelope("PaymentRequiresReviewV1", tripId, captureKey, saga.SagaId.ToString("D"),
                reasonCode: "RETRY_EXHAUSTED"));
        Assert.Equal("Applied", review.Disposition);
        var reviewed = await SagaAsync(cs, tripId);
        Assert.Equal(RideSagaStatus.RequiresReview, reviewed.Status);
        Assert.Equal(RideSagaStep.CapturePending, reviewed.CurrentStep);
        Assert.Equal(TripState.Completed, (await TripAsync(cs, tripId)).State);

        // A DECLINED capture still never regresses the completed ride.
        var declined = await DeliverAsync(cs, "trip.payment-outcome.q",
            PaymentEnvelope("PaymentAuthorizationFailedV1", tripId, captureKey, saga.SagaId.ToString("D"),
                reasonCode: "DECLINED", messageId: $"stale-{Guid.NewGuid():N}"));
        Assert.Equal("NeedsReview", declined.Disposition);
        Assert.Equal(TripState.Completed, (await TripAsync(cs, tripId)).State);

        // Exactly one logical capture exists through every failure shape.
        Assert.Equal(1, (await IntentsAsync(cs, tripId)).Count(m => m.MessageType == "CapturePaymentV1"));

        // Forward recovery completes on the authoritative capture success.
        var captured = await DeliverAsync(cs, "trip.payment-outcome.q",
            PaymentEnvelope("PaymentCapturedV1", tripId, captureKey, saga.SagaId.ToString("D")));
        Assert.Equal("Applied", captured.Disposition);
        var done = await SagaAsync(cs, tripId);
        Assert.Equal(RideSagaStatus.Completed, done.Status);
        Assert.Equal(RideSagaStep.Captured, done.CurrentStep);
        Assert.Equal(TripState.Completed, (await TripAsync(cs, tripId)).State);
    }

    // ------------------------------------------------------ restart + OCC

    [Fact]
    public async Task RestartRecoversFromPersistedState_LateFactsClassifyAsStale()
    {
        var cs = await NewProbeAsync("trip_w10_restart");
        var tripId = await SeedMatchingAsync(cs);
        var saga = await DriveAcceptedAsync(cs, tripId);
        await RideReadyAsync(cs, tripId, saga);

        // Every delivery above already used a fresh context; a very late
        // DECLINED for the resolved authorize step must not regress the ride.
        var late = await DeliverAsync(cs, "trip.payment-outcome.q",
            PaymentEnvelope("PaymentAuthorizationFailedV1", tripId, saga.AuthorizeOperationKey,
                saga.SagaId.ToString("D"), reasonCode: "DECLINED"));
        Assert.Equal("NeedsReview", late.Disposition);
        Assert.Equal("STALE_PAYMENT_OUTCOME", late.Reason);
        Assert.Equal(TripState.ReadyToStart, (await TripAsync(cs, tripId)).State);
        Assert.Equal(RideSagaStep.Authorized, (await SagaAsync(cs, tripId)).CurrentStep);
    }

    [Fact]
    public async Task ConflictingConcurrentOutcomes_ExactlyOneApplies_NoOrphanEffects()
    {
        var cs = await NewProbeAsync("trip_w10_occ");
        var tripId = await SeedMatchingAsync(cs);
        var saga = await DriveAcceptedAsync(cs, tripId);

        // Deterministic interleave: both handlers must finish LOADING (receipt
        // claim + Trip + saga at the same versions) before EITHER saves. A
        // barrier in front of the single SaveChanges guarantees the overlap
        // that a bare Task.Run only achieves by luck under load.
        var saveBarrier = new TwoPartySaveBarrier();
        var winner = Context(cs, new SaveBarrierInterceptor(saveBarrier));
        var loser = Context(cs, new SaveBarrierInterceptor(saveBarrier));
        var winnerHandler = new TripMessageHandler(winner, new Clock(), NullLogger<TripMessageHandler>.Instance);
        var loserHandler = new TripMessageHandler(loser, new Clock(), NullLogger<TripMessageHandler>.Instance);
        var success = PaymentEnvelope("PaymentAuthorizedV1", tripId, saga.AuthorizeOperationKey,
            saga.SagaId.ToString("D"), messageId: $"win-{Guid.NewGuid():N}");
        var failure = PaymentEnvelope("PaymentAuthorizationFailedV1", tripId, saga.AuthorizeOperationKey,
            saga.SagaId.ToString("D"), reasonCode: "DECLINED", messageId: $"lose-{Guid.NewGuid():N}");

        var winnerTask = Task.Run(() => winnerHandler.HandleAsync("trip.payment-outcome.q", success, CancellationToken.None));
        var loserTask = Task.Run(() => loserHandler.HandleAsync("trip.payment-outcome.q", failure, CancellationToken.None));
        await Task.WhenAll(winnerTask, loserTask).ContinueWith(_ => { }, TaskContinuationOptions.ExecuteSynchronously);

        // One writer commits; the OCC loser surfaces as a transient conflict
        // for redelivery — never a silent second effect. Its receipt rolls
        // back with the lost effect (indivisibility).
        foreach (var task in new[] { winnerTask, loserTask })
        {
            Assert.True(task.IsCompletedSuccessfully || task.IsFaulted);
        }

        var conflicts = new[] { winnerTask, loserTask }
            .Where(t => t.IsFaulted)
            .Select(t => t.Exception!.GetBaseException())
            .ToList();
        Assert.Single(conflicts);
        var conflict = Assert.IsType<DeliveryFailureException>(conflicts[0]);
        Assert.Equal(FailureKind.Transient, conflict.Kind);
        Assert.Equal("TRIP_CONCURRENCY_CONFLICT", conflict.Code);

        await winner.DisposeAsync();
        await loser.DisposeAsync();

        var trip = await TripAsync(cs, tripId);
        var final = await SagaAsync(cs, tripId);
        Assert.True(trip.State is TripState.ReadyToStart or TripState.Cancelled);
        if (trip.State == TripState.ReadyToStart)
        {
            Assert.Equal(RideSagaStep.Authorized, final.CurrentStep);
        }
        else
        {
            Assert.Equal(RideSagaStep.ReleasePending, final.CurrentStep);
        }

        // The losing receipt was rolled back with its effect.
        await using var verify = Context(cs);
        var persisted = await verify.MessageReceipts.AsNoTracking()
            .Where(r => r.TripId == tripId).ToListAsync();
        Assert.Single(persisted);
    }

    // ------------------------------------------------------ happy ride leg

    [Fact]
    public async Task CancelRace_ReviewSignalNeverOwnsCompensation_VoidStillDispatchesAndResolves()
    {
        var cs = await NewProbeAsync("trip_w10_review_race");
        var tripId = await SeedMatchingAsync(cs);
        var saga = await DriveAcceptedAsync(cs, tripId);

        await using (var db = Context(cs))
        {
            var trip = await db.Trips.SingleAsync(t => t.TripId == tripId);
            var cancel = Handler<CancelRideHandler>(db);
            Assert.True((await cancel.HandleAsync(tripId, trip.RiderId)).IsSuccess);
        }

        // Payment first signals REQUIRES_REVIEW for the authorize key: an
        // observation only — compensation keeps ownership of the status, so
        // the void leg below can still resolve (regression: review clobber).
        var review = await DeliverAsync(cs, "trip.payment-outcome.q",
            PaymentEnvelope("PaymentRequiresReviewV1", tripId, saga.AuthorizeOperationKey,
                saga.SagaId.ToString("D"), reasonCode: "RETRY_EXHAUSTED"));
        Assert.Equal("Applied", review.Disposition);
        var observed = await SagaAsync(cs, tripId);
        Assert.Equal(RideSagaStatus.Compensating, observed.Status);
        Assert.Equal(RideSagaStep.AuthorizePending, observed.CurrentStep);
        Assert.Equal("RETRY_EXHAUSTED", observed.LastErrorCode);

        // The authorization then wins at Payment authority: the exact void
        // dispatches despite the earlier review signal.
        var authorized = await DeliverAsync(cs, "trip.payment-outcome.q",
            PaymentEnvelope("PaymentAuthorizedV1", tripId, saga.AuthorizeOperationKey, saga.SagaId.ToString("D")));
        Assert.Equal("Applied", authorized.Disposition);
        Assert.Equal(RideSagaStep.VoidPending, (await SagaAsync(cs, tripId)).CurrentStep);
        Assert.Single(await IntentsAsync(cs, tripId), m => m.MessageType == "VoidPaymentAuthorizationV1");

        // A review signal for the VOID key is likewise an observation only.
        var voidReview = await DeliverAsync(cs, "trip.payment-outcome.q",
            PaymentEnvelope("PaymentRequiresReviewV1", tripId, RideSagaOperationKeys.Void(tripId),
                saga.SagaId.ToString("D"), reasonCode: "VOID_REVIEW", messageId: $"vr-{Guid.NewGuid():N}"));
        Assert.Equal("Applied", voidReview.Disposition);
        Assert.Equal(RideSagaStatus.Compensating, (await SagaAsync(cs, tripId)).Status);

        // Driver confirms the exact release while the void is still pending.
        await DeliverAsync(cs, "trip.driver-state.q",
            MessageEnvelope<JsonElement>.Create("DriverReservationReleasedV1", tripId.ToString("D"),
                JsonSerializer.SerializeToElement(new { tripId, driverId = DriverId, reservationId = ReservationId })));
        Assert.Equal(RideSagaStep.VoidPending, (await SagaAsync(cs, tripId)).CurrentStep);

        // The void outcome still resolves and completes both obligations.
        var voided = await DeliverAsync(cs, "trip.payment-outcome.q",
            PaymentEnvelope("PaymentAuthorizationVoidedV1", tripId, RideSagaOperationKeys.Void(tripId),
                saga.SagaId.ToString("D")));
        Assert.Equal("Applied", voided.Disposition);
        Assert.Equal(RideSagaStatus.Compensated, (await SagaAsync(cs, tripId)).Status);
    }

    [Fact]
    public async Task CancelRace_ReleaseConfirmedFirst_LaterDeclinedFinishesCompensation()
    {
        var cs = await NewProbeAsync("trip_w10_release_first");
        var tripId = await SeedMatchingAsync(cs);
        var saga = await DriveAcceptedAsync(cs, tripId);

        await using (var db = Context(cs))
        {
            var toCancel = await db.Trips.SingleAsync(t => t.TripId == tripId);
            var cancel = Handler<CancelRideHandler>(db);
            Assert.True((await cancel.HandleAsync(tripId, toCancel.RiderId)).IsSuccess);
        }

        // Driver confirms the exact release BEFORE Payment resolves the
        // authorize outcome — the natural ordering. The confirmation commits
        // (regression: this used to throw and dead-letter the event).
        var released = await DeliverAsync(cs, "trip.driver-state.q",
            MessageEnvelope<JsonElement>.Create("DriverReservationReleasedV1", tripId.ToString("D"),
                JsonSerializer.SerializeToElement(new { tripId, driverId = DriverId, reservationId = ReservationId })));
        Assert.Equal("Applied", released.Disposition);
        Assert.Equal(RideSagaStatus.Compensating, (await SagaAsync(cs, tripId)).Status);
        Assert.Equal(RideSagaStep.AuthorizePending, (await SagaAsync(cs, tripId)).CurrentStep);

        // Payment then resolves DECLINED: no void needed, and compensation
        // finishes NOW from the durable ledger instead of waiting for an
        // already-consumed release event.
        var declined = await DeliverAsync(cs, "trip.payment-outcome.q",
            PaymentEnvelope("PaymentAuthorizationFailedV1", tripId, saga.AuthorizeOperationKey,
                saga.SagaId.ToString("D"), reasonCode: "DECLINED"));
        Assert.Equal("Applied", declined.Disposition);
        Assert.Equal(RideSagaStatus.Compensated, (await SagaAsync(cs, tripId)).Status);
        var releasedTrip = await TripAsync(cs, tripId);
        Assert.All(releasedTrip.CleanupIntents.Where(i => i.Kind != TripCleanupKind.CancelMatching),
            i => Assert.NotNull(i.CompletedAt));
    }

    [Fact]
    public async Task HappyRide_ALeg_ReachesCapturedSaga_WithOneConsistentCommandSequence()
    {
        var cs = await NewProbeAsync("trip_w10_happy");
        var tripId = await SeedMatchingAsync(cs);
        var saga = await DriveAcceptedAsync(cs, tripId);

        await RideReadyAsync(cs, tripId, saga);
        await RideStartedAsync(cs, tripId);
        var capturing = await RideCompletedAsync(cs, tripId);

        var captured = await DeliverAsync(cs, "trip.payment-outcome.q",
            PaymentEnvelope("PaymentCapturedV1", tripId, capturing.CaptureOperationKey,
                capturing.SagaId.ToString("D")));
        Assert.Equal("Applied", captured.Disposition);

        var trip = await TripAsync(cs, tripId);
        Assert.Equal(TripState.Completed, trip.State);
        var done = await SagaAsync(cs, tripId);
        Assert.Equal(RideSagaStatus.Completed, done.Status);
        Assert.Equal(RideSagaStep.Captured, done.CurrentStep);

        // The full durable command lineage of one happy ride: exactly one of
        // each logical message, all PENDING for the relay.
        var intents = await IntentsAsync(cs, tripId);
        Assert.Equal(
            new HashSet<string> { "RideRequestedV1", "AuthorizePaymentV1", "MarkDriverTripStartedV1", "CapturePaymentV1", "MarkDriverTripCompletedV1" },
            intents.Select(m => m.MessageType).ToHashSet());
        Assert.All(intents, m => Assert.Equal("PENDING", m.Status));
        var authorize = intents.Single(m => m.MessageType == "AuthorizePaymentV1");
        var capture = intents.Single(m => m.MessageType == "CapturePaymentV1");
        Assert.Equal(done.SagaId.ToString("D"), authorize.SagaId);
        Assert.Equal(done.SagaId.ToString("D"), capture.SagaId);
        Assert.Equal(RideSagaOperationKeys.Authorize(tripId), authorize.OperationKey);
        Assert.Equal(RideSagaOperationKeys.Capture(tripId), capture.OperationKey);
    }
}
