// W9 Trip Transactional Outbox proofs (A-W09-01..10).
//
// ONE PROPERTY: a committed Trip business transition never loses the outgoing
// intent that transition requires. The Trip service appends a pending row in
// the SAME EF unit of work as the business state; the shared C-owned relay
// claims/publishes/marks later with the same stable messageId.
//
// Each test runs against its own disposable probe database. That keeps the
// global Outbox backlog deterministic (exactly the rows this test created) and
// prevents a test relay from publishing another test's pending intent.
//
// WHAT IS PROVEN HERE (A-side):
//   * migration produces the shared contract exactly (fresh + W8 upgrade);
//   * Trip EF rows are readable/writable by C's NpgsqlOutboxStore;
//   * state + intent commit or roll back together (including OCC losers);
//   * replay/stale/terminal transitions never append a second intent;
//   * cancellation emits exact Cancel/Release commands and no W10 command;
//   * crash before publish, lost mark after publish and failed publication
//     retain identity and reach a recoverable/visible terminal state;
//   * backlog stats (pending_count / oldest_pending_age / failed_visible)
//     reflect Trip-created rows.
//
// REL-05's consumer half (same messageId delivered twice) is Trip's existing
// durable receipt path, re-asserted here after the W9 change.

using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using RideHailing.Messaging.Contracts;
using RideHailing.Messaging.Observability;
using RideHailing.Messaging.Outbox;
using RideHailing.Messaging.Topology;
using RideHailing.TripService.Application.Trips.Lifecycle;
using RideHailing.TripService.Domain.Trips;
using RideHailing.TripService.Infrastructure.Persistence;
using RideHailing.TripService.Integration.Messaging;

namespace RideHailing.TripService.IntegrationTests;

[Collection(TripPostgresCollection.Name)]
public sealed class TripW09OutboxTests(TripPostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 1, 0, 0, TimeSpan.Zero);

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static readonly string[] SharedContractColumns =
    [
        "aggregate_id", "aggregate_type", "aggregate_version", "attempt_count", "causation_id",
        "claimed_at", "claimed_by", "correlation_id", "exchange", "headers_json", "last_error_at",
        "last_error_code", "message_id", "message_type", "next_attempt_at", "occurred_at",
        "operation_key", "payload_json", "published_at", "routing_key", "saga_id", "schema_version",
        "status",
    ];

    // ----------------------------------------------------------------- helpers

    private static TripDbContext Context(string connectionString, params IInterceptor[] interceptors) =>
        new(new DbContextOptionsBuilder<TripDbContext>().UseNpgsql(connectionString)
            .AddInterceptors(interceptors).Options);

    private async Task<string> NewProbeAsync(string prefix, bool migrate = true)
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
        if (migrate)
        {
            await using var db = Context(connectionString);
            await db.Database.MigrateAsync();
        }

        return connectionString;
    }

    private static Trip NewTrip(Guid? tripId = null, Guid? riderId = null) => Trip.Create(
        tripId ?? Guid.NewGuid(), riderId ?? Guid.NewGuid(), new(10.7, 106.6), new(10.8, 106.7),
        new("VND", 50000m, "w9", Now), Now);

    private static async Task<Trip> SeedCreatedAsync(string connectionString)
    {
        var trip = NewTrip();
        await using var db = Context(connectionString);
        db.Trips.Add(trip);
        await db.SaveChangesAsync();
        return trip;
    }

    private static async Task<(Trip Trip, string MessageId)> SeedMatchingWithIntentAsync(string connectionString)
    {
        var trip = NewTrip();
        trip.StartMatching(Now);
        await using var db = Context(connectionString);
        db.Trips.Add(trip);
        db.OutboxMessages.Add(TripOutboxMessage.From(TripOutboxIntents.RideRequested(trip, Now)));
        await db.SaveChangesAsync();
        return (trip, TripOutboxIntents.RideRequestedMessageId(trip));
    }

    private static string AggregateId(Guid tripId) => tripId.ToString("D");

    private static async Task<List<TripOutboxMessage>> IntentsAsync(string connectionString, Guid tripId)
    {
        await using var db = Context(connectionString);
        return await db.OutboxMessages.AsNoTracking()
            .Where(m => m.AggregateId == AggregateId(tripId))
            .OrderBy(m => m.MessageType)
            .ToListAsync();
    }

    private static JsonElement Payload(TripOutboxMessage message) =>
        JsonDocument.Parse(message.PayloadJson).RootElement.Clone();

    private static MessageEnvelope<JsonElement> Reserved(Trip trip, Guid driverId, Guid reservationId, int round = 1) =>
        MessageEnvelope<JsonElement>.Create("DriverReservedV1", AggregateId(trip.TripId),
            JsonSerializer.SerializeToElement(new
            {
                tripId = trip.TripId, driverId, reservationId, matchingRound = round,
            }));

    private sealed class RecordingPublisher : IOutboxPublisher
    {
        public ConcurrentQueue<OutboxMessage> Published { get; } = new();

        public Task PublishAsync(OutboxMessage message, CancellationToken token = default)
        {
            Published.Enqueue(message);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingPublisher : IOutboxPublisher
    {
        public Task PublishAsync(OutboxMessage message, CancellationToken token = default) =>
            throw new TimeoutException("Simulated broker/confirm failure.");
    }

    private sealed class SimulatedCrash : Exception;

    // ------------------------------------------------------- migration contract

    [Fact]
    public async Task FreshDatabase_MigratesToSharedOutboxContract_WithNoPendingModelChanges()
    {
        var cs = await NewProbeAsync("trip_w09_fresh");
        await using var db = Context(cs);

        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await connection.OpenAsync();

        async Task<HashSet<string>> ColumnsAsync(string table)
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT column_name FROM information_schema.columns " +
                "WHERE table_schema='public' AND table_name=$1";
            command.Parameters.Add(new NpgsqlParameter { Value = table });
            var names = new HashSet<string>(StringComparer.Ordinal);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) names.Add(reader.GetString(0));
            return names;
        }

        async Task<string> DataTypeAsync(string column)
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT data_type FROM information_schema.columns " +
                "WHERE table_schema='public' AND table_name='outbox_messages' AND column_name=$1";
            command.Parameters.Add(new NpgsqlParameter { Value = column });
            return (await command.ExecuteScalarAsync())?.ToString() ?? "";
        }

        Assert.Equal(SharedContractColumns, (await ColumnsAsync("outbox_messages")).Order().ToArray());

        // Shared store writes/reads typed values from these columns; drift here
        // silently breaks the relay, so pin the important types.
        Assert.Equal("jsonb", await DataTypeAsync("payload_json"));
        Assert.Equal("jsonb", await DataTypeAsync("headers_json"));
        Assert.Equal("bigint", await DataTypeAsync("aggregate_version"));
        Assert.Equal("timestamp with time zone", await DataTypeAsync("occurred_at"));
        Assert.Equal("timestamp with time zone", await DataTypeAsync("next_attempt_at"));
        Assert.Equal("integer", await DataTypeAsync("attempt_count"));

        await using (var constraints = connection.CreateCommand())
        {
            constraints.CommandText =
                "SELECT conname FROM pg_constraint c JOIN pg_class t ON t.oid=c.conrelid " +
                "WHERE t.relname='outbox_messages' AND c.contype IN ('c','p') ORDER BY conname";
            var names = new List<string>();
            await using var reader = await constraints.ExecuteReaderAsync();
            while (await reader.ReadAsync()) names.Add(reader.GetString(0));
            Assert.Equal(new[]
            {
                "outbox_messages_attempt_count_check",
                "outbox_messages_identity_nonempty",
                "outbox_messages_pk",
                "outbox_messages_schema_version_check",
                "outbox_messages_status_check",
            }, names);
        }

        await using (var indexes = connection.CreateCommand())
        {
            indexes.CommandText =
                "SELECT indexname, indexdef FROM pg_indexes " +
                "WHERE schemaname='public' AND tablename='outbox_messages' ORDER BY indexname";
            var definitions = new Dictionary<string, string>(StringComparer.Ordinal);
            await using var reader = await indexes.ExecuteReaderAsync();
            while (await reader.ReadAsync()) definitions[reader.GetString(0)] = reader.GetString(1);

            Assert.Contains("status = 'PENDING'", definitions["ix_outbox_messages_claim"]);
            Assert.Contains("next_attempt_at", definitions["ix_outbox_messages_claim"]);
            Assert.Contains("status = 'FAILED_VISIBLE'", definitions["ix_outbox_messages_failed_visible"]);
        }

        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task UpgradeFromW08_PreservesBusinessRows_AndAddsEmptyOutbox()
    {
        var cs = await NewProbeAsync("trip_w09_upgrade", migrate: false);
        await using (var db = Context(cs))
        {
            var target = db.Database.GetMigrations()
                .Single(m => m.EndsWith("W08_TripMessageReceipts", StringComparison.Ordinal));
            await db.GetService<IMigrator>().MigrateAsync(target);

            // The W09 migration must not touch existing business rows; write a
            // real Trip through the current (unchanged trips) mapping.
            db.Trips.Add(NewTrip());
            await db.SaveChangesAsync();
        }

        Guid tripId;
        await using (var upgrade = Context(cs))
        {
            await upgrade.Database.MigrateAsync();
            Assert.Empty(await upgrade.Database.GetPendingMigrationsAsync());
            Assert.False(upgrade.Database.HasPendingModelChanges());
            tripId = (await upgrade.Trips.AsNoTracking().SingleAsync()).TripId;
            Assert.Equal(0, await upgrade.OutboxMessages.CountAsync());
        }

        await using var verify = Context(cs);
        Assert.Equal(tripId, (await verify.Trips.AsNoTracking().SingleAsync()).TripId);
    }

    // ------------------------------------------- shared-store interoperability

    [Fact]
    public async Task TripIntent_RoundTripsThroughSharedStore_AndStatsExposeBacklog()
    {
        var cs = await NewProbeAsync("trip_w09_compat");
        var (trip, messageId) = await SeedMatchingWithIntentAsync(cs);

        await using var source = NpgsqlDataSource.Create(cs);
        var store = new NpgsqlOutboxStore(source);

        // Observability contract read by the shared relay/metrics.
        var backlog = await store.GetStatsAsync();
        Assert.Equal(1L, backlog.PendingCount);
        Assert.Equal(0L, backlog.FailedVisibleCount);
        Assert.Equal<DateTimeOffset?>(Now, backlog.OldestPendingAt);
        Assert.True(backlog.OldestPendingAgeSeconds(Now.AddSeconds(30)) >= 30);

        // The Trip EF row must be claimable by C's raw-SQL store.
        var claimed = await store.ClaimBatchAsync("compat-relay", 10, TimeSpan.FromMinutes(1), Now.AddSeconds(1));
        var row = Assert.Single(claimed);
        Assert.Equal(messageId, row.MessageId);
        Assert.Equal("RideRequestedV1", row.MessageType);
        Assert.Equal(AggregateId(trip.TripId), row.CorrelationId);
        Assert.Equal(RideTopology.EventsExchange, row.Exchange);
        Assert.Equal("trip.ride-requested.v1", row.RoutingKey);
        Assert.Equal(1, row.SchemaVersion);
        var envelope = row.ToEnvelope();
        Assert.Equal(trip.TripId, envelope.Payload.GetProperty("tripId").GetGuid());
        Assert.Equal(1, envelope.Payload.GetProperty("matchingRound").GetInt32());

        // A store mark must be visible to EF and shrink the backlog.
        Assert.True(await store.MarkPublishedAsync(messageId, Now.AddSeconds(2)));
        var settled = await store.GetStatsAsync();
        Assert.Equal(0L, settled.PendingCount);
        Assert.True(settled.OldestPendingAgeSeconds(Now.AddSeconds(3)) == 0);

        await using (var verify = Context(cs))
        {
            var saved = await verify.OutboxMessages.AsNoTracking().SingleAsync(m => m.MessageId == messageId);
            Assert.Equal("PUBLISHED", saved.Status);
            Assert.Equal(Now.AddSeconds(2), saved.PublishedAt);
            Assert.Null(saved.ClaimedBy);
        }

        // Reverse direction: a row enqueued by the shared store inside a
        // caller transaction is readable through the Trip EF mapping.
        var extra = OutboxMessage.Create(MessageEnvelope<JsonElement>.Create(
            "ProofV1", "correlation-proof", JsonSerializer.SerializeToElement(new { proof = true })),
            RideTopology.EventsExchange, "proof.v1");
        await using (var connection = await source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            Assert.True(await store.EnqueueAsync(connection, transaction, extra));
            await transaction.CommitAsync();
        }

        await using (var verify = Context(cs))
        {
            var mirrored = await verify.OutboxMessages.AsNoTracking().SingleAsync(m => m.MessageId == extra.MessageId);
            Assert.Equal("PENDING", mirrored.Status);
            Assert.Equal("ProofV1", mirrored.MessageType);
            Assert.Equal("{}", mirrored.HeadersJson.Trim());
        }
    }

    // ------------------------------------------------------- atomicity / OCC

    [Fact]
    public async Task FailedSave_RollsBackBusinessTransitionAndIntent()
    {
        var cs = await NewProbeAsync("trip_w09_rollback");
        var trip = await SeedCreatedAsync(cs);

        await using (var broken = Context(cs, new FailSaveBeforeCommit()))
            await Assert.ThrowsAsync<IOException>(() =>
                new StartMatchingHandler(broken, new Clock(), NullLogger<StartMatchingHandler>.Instance)
                    .HandleAsync(trip.TripId, trip.RiderId));

        await using (var verify = Context(cs))
        {
            var saved = await verify.Trips.AsNoTracking().SingleAsync(t => t.TripId == trip.TripId);
            Assert.Equal(TripState.Created, saved.State);
            Assert.Equal(trip.Version, saved.Version);
            Assert.Empty(await verify.OutboxMessages.AsNoTracking().ToListAsync());
        }

        // A later successful attempt commits both halves exactly once.
        await using (var retry = Context(cs))
        {
            var result = await new StartMatchingHandler(retry, new Clock(),
                NullLogger<StartMatchingHandler>.Instance).HandleAsync(trip.TripId, trip.RiderId);
            Assert.True(result.IsSuccess);
            Assert.False(result.IsReplay);
        }

        await using (var verify = Context(cs))
        {
            Assert.Equal(TripState.Matching, (await verify.Trips.AsNoTracking().SingleAsync()).State);
            Assert.Single(await IntentsAsync(cs, trip.TripId));
        }
    }

    [Fact]
    public async Task DuplicateLogicalIntent_BlocksStateCommit_ThenLeavesOneRow()
    {
        var cs = await NewProbeAsync("trip_w09_pk");
        var trip = await SeedCreatedAsync(cs);

        // Simulate a previously persisted intent for the same logical identity
        // (deterministic id) with the business row still un-advanced. The
        // database must refuse to add a second intent or advance the state.
        var template = NewTrip(trip.TripId, trip.RiderId);
        template.StartMatching(Now);
        await using (var seed = Context(cs))
        {
            seed.OutboxMessages.Add(TripOutboxMessage.From(TripOutboxIntents.RideRequested(template, Now)));
            await seed.SaveChangesAsync();
        }

        await using (var conflicting = Context(cs))
        {
            var conflict = await Assert.ThrowsAsync<TripConcurrencyConflictException>(() =>
                new StartMatchingHandler(conflicting, new Clock(), NullLogger<StartMatchingHandler>.Instance)
                    .HandleAsync(trip.TripId, trip.RiderId));
            Assert.Equal(trip.TripId, conflict.TripId);
        }

        await using (var verify = Context(cs))
        {
            var saved = await verify.Trips.AsNoTracking().SingleAsync(t => t.TripId == trip.TripId);
            Assert.Equal(TripState.Created, saved.State);
            Assert.Equal(trip.Version, saved.Version);
            Assert.Single(await IntentsAsync(cs, trip.TripId));
        }
    }

    [Fact]
    public async Task ConcurrentStartMatching_OneWinner_OneIntent_NoOrphan()
    {
        var cs = await NewProbeAsync("trip_w09_occ");
        var trip = await SeedCreatedAsync(cs);
        var barrier = new TwoWriterBarrier();

        async Task<Exception?> Attempt()
        {
            await using var db = Context(cs, barrier);
            return await Record.ExceptionAsync(() =>
                new StartMatchingHandler(db, new Clock(), NullLogger<StartMatchingHandler>.Instance)
                    .HandleAsync(trip.TripId, trip.RiderId));
        }

        var errors = await Task.WhenAll(Attempt(), Attempt());
        Assert.Single(errors, e => e is null);
        var loser = Assert.Single(errors, e => e is not null);
        // Bound by the Version token first; the deterministic Outbox identity
        // is a second database fence for the same logical message. Both are
        // reported as a Trip concurrency conflict so callers reread the
        // winner's committed state instead of treating a lost race as a fault.
        var conflict = Assert.IsType<TripConcurrencyConflictException>(loser);
        Assert.Equal(trip.TripId, conflict.TripId);

        await using (var verify = Context(cs))
        {
            var saved = await verify.Trips.AsNoTracking().SingleAsync(t => t.TripId == trip.TripId);
            Assert.Equal(TripState.Matching, saved.State);
            Assert.Equal(trip.Version + 1, saved.Version);
            var intent = Assert.Single(await IntentsAsync(cs, trip.TripId));
            Assert.Equal(TripOutboxIntents.RideRequestedMessageId(saved), intent.MessageId);
        }

        // Replay of the committed transition must not append anything.
        await using (var replay = Context(cs))
        {
            var result = await new StartMatchingHandler(replay, new Clock(),
                NullLogger<StartMatchingHandler>.Instance).HandleAsync(trip.TripId, trip.RiderId);
            Assert.True(result.IsSuccess);
            Assert.True(result.IsReplay);
        }

        Assert.Single(await IntentsAsync(cs, trip.TripId));
    }

    // ------------------------------------------------------ replay/stale/terminal

    [Fact]
    public async Task ReplayPaths_NeverAppendSecondIntent()
    {
        var cs = await NewProbeAsync("trip_w09_replay");
        var trip = await SeedCreatedAsync(cs);

        await using (var db = Context(cs))
        {
            var handler = new StartMatchingHandler(db, new Clock(), NullLogger<StartMatchingHandler>.Instance);
            Assert.False((await handler.HandleAsync(trip.TripId, trip.RiderId)).IsReplay);
            Assert.True((await handler.HandleAsync(trip.TripId, trip.RiderId)).IsReplay);
        }

        await using (var db = Context(cs))
        {
            var handler = new CancelRideHandler(db, new Clock(), NullLogger<CancelRideHandler>.Instance);
            Assert.False((await handler.HandleAsync(trip.TripId, trip.RiderId)).IsReplay);
            Assert.True((await handler.HandleAsync(trip.TripId, trip.RiderId)).IsReplay);
        }

        await using (var verify = Context(cs))
        {
            var intents = await IntentsAsync(cs, trip.TripId);
            Assert.Equal(2, intents.Count);
            Assert.Single(intents, i => i.MessageType == "RideRequestedV1");
            Assert.Single(intents, i => i.MessageType == "CancelMatchingRoundV1");
        }

        // Broker-free create-path intake is idempotent as well.
        var intakeTrip = await SeedCreatedAsync(cs);
        await using (var db = Context(cs))
        {
            var intake = new TripRideRequestedIntake(db, new TripMessagingSettings(true), new Clock(),
                NullLogger<TripRideRequestedIntake>.Instance);
            Assert.True(await intake.EnsureRideRequestedAsync(intakeTrip.TripId, intakeTrip.RiderId, default));
            Assert.True(await intake.EnsureRideRequestedAsync(intakeTrip.TripId, intakeTrip.RiderId, default));
        }

        await using (var verify = Context(cs))
        {
            var saved = await verify.Trips.AsNoTracking().SingleAsync(t => t.TripId == intakeTrip.TripId);
            Assert.Equal(TripState.Matching, saved.State);
            var intent = Assert.Single(await IntentsAsync(cs, intakeTrip.TripId));
            Assert.Equal(TripOutboxIntents.RideRequestedMessageId(saved), intent.MessageId);
        }
    }

    [Fact]
    public async Task RejectedAndStaleTransitions_AppendNoIntent()
    {
        var cs = await NewProbeAsync("trip_w09_stale");
        var trip = await SeedCreatedAsync(cs);

        await using (var db = Context(cs))
            Assert.True((await new StartMatchingHandler(db, new Clock(),
                NullLogger<StartMatchingHandler>.Instance).HandleAsync(trip.TripId, trip.RiderId)).IsSuccess);

        await using (var db = Context(cs))
            Assert.True((await new CancelRideHandler(db, new Clock(),
                NullLogger<CancelRideHandler>.Instance).HandleAsync(trip.TripId, trip.RiderId)).IsSuccess);

        var before = await IntentsAsync(cs, trip.TripId);
        Assert.Equal(2, before.Count);

        // Terminal state rejects a re-open attempt without any new intent.
        await using (var db = Context(cs))
        {
            var rejected = await new StartMatchingHandler(db, new Clock(),
                NullLogger<StartMatchingHandler>.Instance).HandleAsync(trip.TripId, trip.RiderId);
            Assert.False(rejected.IsSuccess);
            Assert.Equal(TripErrorCode.TerminalState, rejected.ErrorCode);
        }

        // A future/unopened round is not evidence of this Trip's attempt.
        await using (var db = Context(cs))
        {
            var handler = new TripMessageHandler(db, TimeProvider.System,
                NullLogger<TripMessageHandler>.Instance);
            await handler.HandleAsync("trip.driver-state.q",
                Reserved(trip, Guid.NewGuid(), Guid.NewGuid(), round: 2), default);
        }

        await using (var verify = Context(cs))
        {
            var saved = await verify.Trips.AsNoTracking().SingleAsync(t => t.TripId == trip.TripId);
            Assert.Equal(TripState.Cancelled, saved.State);
            Assert.Equal(before.Select(i => i.MessageId).Order().ToArray(),
                (await IntentsAsync(cs, trip.TripId)).Select(i => i.MessageId).Order().ToArray());
            var receipt = await verify.MessageReceipts.AsNoTracking()
                .SingleAsync(r => r.TripId == trip.TripId);
            Assert.Equal("StaleIdentity", receipt.Reason);
            Assert.Null(saved.Assignment);
        }
    }

    // -------------------------------------------------------- cancellation wire

    [Fact]
    public async Task CancelAfterReservation_EmitsExactCancelAndRelease_ButNoPaymentCommand()
    {
        var cs = await NewProbeAsync("trip_w09_cancel");
        var trip = await SeedCreatedAsync(cs);
        var driverId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();

        await using (var db = Context(cs))
            Assert.True((await new StartMatchingHandler(db, new Clock(),
                NullLogger<StartMatchingHandler>.Instance).HandleAsync(trip.TripId, trip.RiderId)).IsSuccess);

        await using (var db = Context(cs))
            Assert.True((await new RecordDriverReservationHandler(db, new Clock(),
                NullLogger<RecordDriverReservationHandler>.Instance)
                .HandleAsync(trip.TripId, driverId, reservationId, 1)).IsSuccess);

        await using (var db = Context(cs))
            Assert.True((await new CancelRideHandler(db, new Clock(),
                NullLogger<CancelRideHandler>.Instance).HandleAsync(trip.TripId, trip.RiderId)).IsSuccess);

        var intents = await IntentsAsync(cs, trip.TripId);
        Assert.Equal(3, intents.Count);
        Assert.Single(intents, i => i.MessageType == "RideRequestedV1");

        var cancel = Assert.Single(intents, i => i.MessageType == "CancelMatchingRoundV1");
        Assert.Equal(RideTopology.CommandsExchange, cancel.Exchange);
        Assert.Equal("matching.cancel-round.v1", cancel.RoutingKey);
        Assert.Equal($"trip:{trip.TripId:N}:cancel-round:1", cancel.MessageId);
        Assert.Equal(AggregateId(trip.TripId), cancel.CorrelationId);
        var cancelPayload = Payload(cancel);
        Assert.Equal(trip.TripId, cancelPayload.GetProperty("tripId").GetGuid());
        Assert.Equal(1, cancelPayload.GetProperty("matchingRound").GetInt32());

        var release = Assert.Single(intents, i => i.MessageType == "ReleaseDriverReservationV1");
        Assert.Equal(RideTopology.CommandsExchange, release.Exchange);
        Assert.Equal("driver.release-reservation.v1", release.RoutingKey);
        Assert.Equal($"trip:{trip.TripId:N}:release:{reservationId:N}", release.MessageId);
        var releasePayload = Payload(release);
        Assert.Equal(trip.TripId, releasePayload.GetProperty("tripId").GetGuid());
        Assert.Equal(driverId, releasePayload.GetProperty("driverId").GetGuid());
        Assert.Equal(reservationId, releasePayload.GetProperty("reservationId").GetGuid());
        Assert.Equal(1, releasePayload.GetProperty("matchingRound").GetInt32());

        // W10 owns payment operation lineage: no W9 payment command may exist,
        // even though the Trip keeps its durable ReconcilePayment obligation.
        Assert.DoesNotContain(intents, i => i.MessageType.Contains("Payment", StringComparison.Ordinal));
        await using var verify = Context(cs);
        var saved = await verify.Trips.AsNoTracking().SingleAsync(t => t.TripId == trip.TripId);
        Assert.Equal(TripState.Cancelled, saved.State);
        Assert.Contains(saved.CleanupIntents, i => i.Kind == TripCleanupKind.ReconcilePayment);
    }

    [Fact]
    public async Task LateReservationAfterCancellation_AddsReleaseIntentAtomically()
    {
        var cs = await NewProbeAsync("trip_w09_late");
        var trip = await SeedCreatedAsync(cs);
        await using (var db = Context(cs))
            Assert.True((await new StartMatchingHandler(db, new Clock(),
                NullLogger<StartMatchingHandler>.Instance).HandleAsync(trip.TripId, trip.RiderId)).IsSuccess);
        await using (var db = Context(cs))
            Assert.True((await new CancelRideHandler(db, new Clock(),
                NullLogger<CancelRideHandler>.Instance).HandleAsync(trip.TripId, trip.RiderId)).IsSuccess);

        var driverId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        var envelope = Reserved(trip, driverId, reservationId);
        await using (var db = Context(cs))
            await new TripMessageHandler(db, TimeProvider.System, NullLogger<TripMessageHandler>.Instance)
                .HandleAsync("trip.driver-state.q", envelope, default);

        var intents = await IntentsAsync(cs, trip.TripId);
        Assert.Equal(3, intents.Count);
        var release = Assert.Single(intents, i => i.MessageType == "ReleaseDriverReservationV1");
        Assert.Equal($"trip:{trip.TripId:N}:release:{reservationId:N}", release.MessageId);
        Assert.Equal(reservationId, Payload(release).GetProperty("reservationId").GetGuid());

        await using var verify = Context(cs);
        var saved = await verify.Trips.AsNoTracking().SingleAsync(t => t.TripId == trip.TripId);
        Assert.Equal(TripState.Cancelled, saved.State);
        Assert.Null(saved.Assignment);
        Assert.Contains(saved.CleanupIntents, i => i.ReservationId == reservationId);

        // Receipt and new intent came from the same consumer transaction.
        var receipt = await verify.MessageReceipts.AsNoTracking().SingleAsync(r => r.MessageId == envelope.MessageId);
        Assert.Equal("Applied", receipt.Disposition);
    }

    // ------------------------------------------------- REL-04 crash / restart

    [Fact]
    public async Task CrashBeforePublish_LeavesDurablePendingRow_ThenRestartPublishesSameIdentity()
    {
        var cs = await NewProbeAsync("trip_w09_crash_before");
        var (trip, messageId) = await SeedMatchingWithIntentAsync(cs);

        await using var source = NpgsqlDataSource.Create(cs);
        var store = new NpgsqlOutboxStore(source);
        var delivered = new RecordingPublisher();

        var crashed = new OutboxRelay(store, delivered, new OutboxRelayOptions
        {
            RelayId = "trip-relay-a",
            BatchSize = 10,
            ClaimLease = TimeSpan.FromMinutes(5),
            BeforePublish = (_, _) => Task.FromException(new SimulatedCrash()),
        });
        await Assert.ThrowsAsync<SimulatedCrash>(() => crashed.RunOnceAsync());
        Assert.Empty(delivered.Published);

        await using (var verify = Context(cs))
        {
            var row = await verify.OutboxMessages.AsNoTracking().SingleAsync();
            Assert.Equal(messageId, row.MessageId);
            Assert.Equal("PENDING", row.Status);
            Assert.Equal("trip-relay-a", row.ClaimedBy);
            Assert.Null(row.PublishedAt);
            Assert.Equal(0, row.AttemptCount);
        }

        // Restart: the new relay reclaims the orphaned claim and publishes the
        // identical identity; no second row is created.
        var restarted = new OutboxRelay(store, delivered, new OutboxRelayOptions
        {
            RelayId = "trip-relay-b",
            BatchSize = 10,
            ClaimLease = TimeSpan.Zero,
        });
        Assert.Equal(1, await restarted.RunOnceAsync());

        Assert.Single(delivered.Published);
        Assert.Equal(messageId, delivered.Published.Single().MessageId);
        await using (var verify = Context(cs))
        {
            var row = await verify.OutboxMessages.AsNoTracking().SingleAsync();
            Assert.Equal("PUBLISHED", row.Status);
            Assert.Equal(messageId, row.MessageId);
            Assert.Null(row.ClaimedBy);
            Assert.Equal(AggregateId(trip.TripId), row.CorrelationId);
        }
    }

    [Fact]
    public async Task LostMarkAfterAcceptedPublish_RepublishesSameIdentity_ThenSettles()
    {
        var cs = await NewProbeAsync("trip_w09_lost_mark");
        var (_, messageId) = await SeedMatchingWithIntentAsync(cs);

        await using var source = NpgsqlDataSource.Create(cs);
        var store = new NpgsqlOutboxStore(source);
        var delivered = new RecordingPublisher();

        var lostMark = new OutboxRelay(store, delivered, new OutboxRelayOptions
        {
            RelayId = "trip-relay-a",
            BatchSize = 10,
            ClaimLease = TimeSpan.FromMinutes(5),
            AfterPublishBeforeMark = (_, _) => Task.FromException(new SimulatedCrash()),
        });
        await Assert.ThrowsAsync<SimulatedCrash>(() => lostMark.RunOnceAsync());
        Assert.Single(delivered.Published);

        await using (var verify = Context(cs))
        {
            var row = await verify.OutboxMessages.AsNoTracking().SingleAsync();
            Assert.Equal("PENDING", row.Status);
            Assert.Null(row.PublishedAt);
        }

        var retry = new OutboxRelay(store, delivered, new OutboxRelayOptions
        {
            RelayId = "trip-relay-b",
            BatchSize = 10,
            ClaimLease = TimeSpan.Zero,
        });
        Assert.Equal(1, await retry.RunOnceAsync());

        var publications = delivered.Published.ToArray();
        Assert.Equal(2, publications.Length);
        Assert.All(publications, m => Assert.Equal(messageId, m.MessageId));
        Assert.Equal(publications[0].PayloadJson, publications[1].PayloadJson);

        await using (var verify = Context(cs))
        {
            var row = await verify.OutboxMessages.AsNoTracking().SingleAsync();
            Assert.Equal("PUBLISHED", row.Status);
            Assert.Equal(messageId, row.MessageId);
        }

        // REL-05 consumer half: the same messageId delivered twice (the safe
        // republish above) must apply at most once through Trip's durable
        // receipt fence.
        var trip = await SeedCreatedAsync(cs);
        int versionBeforeReservation;
        await using (var db = Context(cs))
        {
            Assert.True((await new StartMatchingHandler(db, new Clock(),
                NullLogger<StartMatchingHandler>.Instance).HandleAsync(trip.TripId, trip.RiderId)).IsSuccess);
            versionBeforeReservation = (await db.Trips.AsNoTracking()
                .SingleAsync(t => t.TripId == trip.TripId)).Version;
        }

        var envelope = Reserved(trip, Guid.NewGuid(), Guid.NewGuid());
        await using (var db = Context(cs))
        {
            var handler = new TripMessageHandler(db, TimeProvider.System,
                NullLogger<TripMessageHandler>.Instance);
            await handler.HandleAsync("trip.driver-state.q", envelope, default);
            await handler.HandleAsync("trip.driver-state.q", envelope, default);
        }

        await using (var verify = Context(cs))
        {
            var saved = await verify.Trips.AsNoTracking().SingleAsync(t => t.TripId == trip.TripId);
            Assert.Equal(TripState.DriverReserved, saved.State);
            Assert.Equal(versionBeforeReservation + 1, saved.Version);
            Assert.Equal(1, await verify.MessageReceipts.CountAsync(r => r.TripId == trip.TripId));
        }
    }

    // ------------------------------------------------------- failure visibility

    [Fact]
    public async Task FailedPublication_BecomesFailedVisible_AndRelayMetricsAreEmitted()
    {
        var cs = await NewProbeAsync("trip_w09_failed");
        var (_, messageId) = await SeedMatchingWithIntentAsync(cs);

        var captures = new ConcurrentBag<(string Name, double Value, KeyValuePair<string, object?>[] Tags)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == MessagingMetrics.MeterName) l.EnableMeasurementEvents(instrument);
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            captures.Add((instrument.Name, value, tags.ToArray())));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            captures.Add((instrument.Name, value, tags.ToArray())));
        listener.Start();

        await using var source = NpgsqlDataSource.Create(cs);
        var store = new NpgsqlOutboxStore(source);
        var relay = new OutboxRelay(store, new ThrowingPublisher(), new OutboxRelayOptions
        {
            RelayId = "trip-relay-failing",
            BatchSize = 10,
            ClaimLease = TimeSpan.Zero,
            MaxAttempts = 1,
            RetryDelay = TimeSpan.Zero,
        });

        Assert.Equal(1, await relay.RunOnceAsync());

        // The relay reported through the shared instruments; no payload or
        // message identity is allowed to become a metric dimension.
        Assert.Contains(captures, c => c.Name == "outbox.publish" &&
            c.Tags.Any(t => t.Key == "outcome" && Equals(t.Value, "failed")));
        Assert.Contains(captures, c => c.Name == "outbox.claim" && c.Value >= 1);
        Assert.All(captures.Where(c => c.Name is "outbox.publish" or "outbox.claim"),
            c => Assert.DoesNotContain(c.Tags, t => t.Value?.ToString()?.Contains(messageId, StringComparison.Ordinal) == true));

        var stats = await store.GetStatsAsync();
        Assert.Equal(0L, stats.PendingCount);
        Assert.Equal(1L, stats.FailedVisibleCount);
        Assert.Null(stats.OldestPendingAt);

        await using var verify = Context(cs);
        var row = await verify.OutboxMessages.AsNoTracking().SingleAsync();
        Assert.Equal("FAILED_VISIBLE", row.Status);
        Assert.Equal(1, row.AttemptCount);
        Assert.Equal("PUBLISH_TIMEOUT", row.LastErrorCode);
        Assert.NotNull(row.LastErrorAt);
        Assert.Null(row.PublishedAt);
    }

    // ---------------------------------------------------------------- helpers

    private sealed class FailSaveBeforeCommit : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data,
            InterceptionResult<int> result, CancellationToken token = default) =>
            throw new IOException("Injected failure before commit.");
    }

    private sealed class TwoWriterBarrier : SaveChangesInterceptor
    {
        private int arrived;
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data,
            InterceptionResult<int> result, CancellationToken token = default)
        {
            if (Interlocked.Increment(ref arrived) == 2) release.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(15), token);
            return result;
        }
    }
}
