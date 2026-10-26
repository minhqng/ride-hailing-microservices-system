using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using RabbitMQ.Client;
using RideHailing.DriverService.Application.Messaging;
using RideHailing.DriverService.Domain.Drivers;
using RideHailing.DriverService.Infrastructure.Persistence;
using RideHailing.Messaging.Contracts;
using RideHailing.Messaging.Outbox;
using RideHailing.Messaging.Reliability;
using RideHailing.Messaging.Topology;
using RideHailing.TripService.Domain.Trips;
using RideHailing.TripService.Infrastructure.Persistence;
using RideHailing.TripService.Integration.Messaging;
using Xunit.Abstractions;

namespace RideHailing.DriverService.IntegrationTests;

[Collection(DriverBrokerCollection.Name)]
public sealed class DriverCompensationTests
{
    private readonly DriverPostgresDatabase db;
    private readonly RabbitMqDatabase rabbit;
    private readonly ITestOutputHelper _output;

    public DriverCompensationTests(
        DriverPostgresDatabase db, RabbitMqDatabase rabbit, ITestOutputHelper output)
    {
        this.db = db;
        this.rabbit = rabbit;
        _output = output;
    }

    private ConnectionFactory Factory => new() { Uri = new(rabbit.ConnectionString), AutomaticRecoveryEnabled = false };

    private static Trip CancelledTrip(Guid tripId, Guid driver, Guid reservation, int round = 1)
    {
        var now = DateTimeOffset.UtcNow;
        var trip = Trip.Create(tripId, Guid.NewGuid(), new(10.7, 106.6), new(10.8, 106.7),
            new("VND", 50000m, "compensation-contract", now), now);
        Assert.True(trip.StartMatching(now).IsSuccess);
        Assert.True(trip.Cancel(now).IsSuccess);
        Assert.True(trip.RecordPreStartTerminalReservation(driver, reservation, round, now).IsSuccess);
        return trip;
    }

    private static MessageEnvelope<JsonElement> Release(Guid driver, Guid tripId, Guid reservation, int round = 1)
    {
        var trip = CancelledTrip(tripId, driver, reservation, round);
        var intent = Assert.Single(trip.CleanupIntents, i => i.Kind == TripCleanupKind.ReleaseReservation);
        return TripOutboxIntents.ReleaseReservation(trip, intent, intent.CreatedAt).ToEnvelope();
    }

    private async Task<(Guid Driver, Guid Trip, Guid Reservation)> ReserveAsync(int? round = 1)
    {
        var driver = Driver.Create(Guid.NewGuid(), Guid.NewGuid());
        driver.GoOnline(driver.Version);
        var repo = db.CreateRepository();
        Assert.True(await repo.TryInsertAsync(driver));
        var trip = Guid.NewGuid(); var reservation = Guid.NewGuid();
        Assert.Equal(ReserveOutcome.Won, (await repo.TryReserveAsync(driver.DriverId, trip,
            reservation, driver.Version, matchingRound: round)).Outcome);
        return (driver.DriverId, trip, reservation);
    }

    private async Task<long> ScalarAsync(string sql, string id)
    {
        await using var conn = await db.DataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", id);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private Task<long> Events(Guid driver) => ScalarAsync(
        "SELECT count(*) FROM outbox_messages WHERE aggregate_id = @id", driver.ToString("N"));

    private async Task<long> EventsByType(Guid driver, string messageType)
    {
        await using var conn = await db.DataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("""
            SELECT count(*) FROM outbox_messages
            WHERE aggregate_id = @id AND message_type = @type;
            """, conn);
        cmd.Parameters.AddWithValue("id", driver.ToString("N"));
        cmd.Parameters.AddWithValue("type", messageType);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<long> ReleaseEventsForReservation(Guid reservation)
    {
        await using var conn = await db.DataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("""
            SELECT count(*) FROM outbox_messages
            WHERE message_type = 'DriverReservationReleasedV1'
              AND payload_json->>'reservationId' = @reservation;
            """, conn);
        cmd.Parameters.AddWithValue("reservation", reservation.ToString("D"));
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<(string Disposition, string? Reason)> ReadReceiptAsync(
        string messageId)
    {
        await using var conn = await db.DataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("""
            SELECT disposition, reason FROM driver_command_receipts
            WHERE message_id = @id;
            """, conn);
        cmd.Parameters.AddWithValue("id", messageId);
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    private async Task AssertReceipt(MessageEnvelope<JsonElement> command, DriverLifecycleResult expected)
    {
        await using var conn = await db.DataSource.OpenConnectionAsync();
        await using var sql = new NpgsqlCommand("""
            SELECT disposition, reason, event_message_id FROM driver_command_receipts WHERE message_id = @id
            """, conn);
        sql.Parameters.AddWithValue("id", command.MessageId);
        await using var reader = await sql.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(expected.Disposition, reader.GetString(0));
        Assert.Equal(expected.Reason, reader.IsDBNull(1) ? null : reader.GetString(1));
        Assert.Equal(expected.EventMessageId, reader.IsDBNull(2) ? null : reader.GetString(2));
        Assert.False(await reader.ReadAsync());
    }

    [Fact]
    public async Task ProductionRelease_DuplicateAndChangedContent_PreserveExactAuthority()
    {
        var (driver, trip, reservation) = await ReserveAsync();
        var command = Release(driver, trip, reservation);
        Assert.Equal($"trip:{trip:N}:release:{reservation:N}", command.MessageId);
        Assert.Equal(1, command.Payload.GetProperty("matchingRound").GetInt32());
        Assert.Equal(1, DriverCommand.Parse(command).MatchingRound);
        var result = await db.CreateRepository().HandleCommandAsync(command);
        Assert.Equal("Applied", result.Disposition);
        Assert.Equal(4, result.Version);
        var available = (await db.CreateRepository().LoadAsync(driver))!;
        Assert.Equal(DriverStatus.Available, available.Status);
        Assert.Null(available.ReservationId); Assert.Null(available.ReservedTripId);
        Assert.Null(available.ReservationRound); Assert.Null(available.ActiveTripId);
        Assert.Equal(2, await Events(driver));
        var stored = (await db.OutboxStore.LoadAsync(result.EventMessageId!))!;
        Assert.Equal("DriverReservationReleasedV1", stored.MessageType);
        Assert.Equal(trip, stored.ToEnvelope().Payload.GetProperty("tripId").GetGuid());
        Assert.Equal(reservation, stored.ToEnvelope().Payload.GetProperty("reservationId").GetGuid());
        Assert.Equal(command.MessageId, stored.CausationId);
        Assert.Equal(command.OperationKey, stored.OperationKey);
        Assert.Equal(result, await db.CreateRepository().HandleCommandAsync(command));
        await AssertReceipt(command, result);
        var changedPayload = System.Text.Json.Nodes.JsonNode.Parse(command.Payload.GetRawText())!;
        changedPayload["matchingRound"] = 2;
        var changed = command with { Payload = JsonSerializer.SerializeToElement(changedPayload) };
        var poison = await Assert.ThrowsAsync<DeliveryFailureException>(() => db.CreateRepository().HandleCommandAsync(changed));
        Assert.Equal("DRIVER_COMMAND_CONTENT_CONFLICT", poison.Code);
        Assert.Equal(2, await Events(driver));
        Assert.Equal(4, (await db.CreateRepository().LoadAsync(driver))!.Version);
    }

    [Theory]
    [InlineData("round", "RESERVATION_ROUND_MISMATCH")]
    [InlineData("legacy", "RESERVATION_ROUND_REQUIRED")]
    [InlineData("trip", "STALE_RESERVATION")]
    [InlineData("reservation", "STALE_RESERVATION")]
    [InlineData("missing", "DRIVER_NOT_FOUND")]
    [InlineData("started", "RELEASE_PROVENANCE_REQUIRED")]
    [InlineData("available", "RELEASE_PROVENANCE_REQUIRED")]
    [InlineData("offline", "RELEASE_PROVENANCE_REQUIRED")]
    public async Task UnprovenCompensation_IsDurablyVisibleWithoutRelease(string scenario, string reason)
    {
        int? matchingRound = scenario switch
        {
            "legacy" => null,
            "round" => 2,
            _ => 1
        };
        var (driver, trip, reservation) = await ReserveAsync(matchingRound);
        var repo = db.CreateRepository();
        switch (scenario)
        {
            case "started":
                await repo.TryMarkTripStartedAsync(driver, trip, reservation);
                break;
            case "available":
                await repo.TryReleaseAsync(driver, trip, reservation);
                break;
            case "offline":
                await repo.TryReleaseAsync(driver, trip, reservation);
                await repo.TryGoOfflineAsync(driver, 4);
                break;
        }
        var before = (await repo.LoadAsync(driver))!;
        var count = await Events(driver);
        var command = Release(scenario == "missing" ? Guid.NewGuid() : driver,
            scenario == "trip" ? Guid.NewGuid() : trip,
            scenario == "reservation" ? Guid.NewGuid() : reservation);
        var result = await repo.HandleCommandAsync(command);
        Assert.Equal(scenario == "missing" ? "NotFound" : "Rejected", result.Disposition);
        Assert.Equal(reason, result.Reason);
        Assert.Null(result.EventMessageId);
        await AssertReceipt(command, result);
        Assert.Equal(result, await db.CreateRepository().HandleCommandAsync(command));
        var after = (await repo.LoadAsync(driver))!;
        Assert.Equal(before.Version, after.Version); Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.ReservationId, after.ReservationId); Assert.Equal(before.ActiveTripId, after.ActiveTripId);
        Assert.Equal(count, await Events(driver));
    }

    [Fact]
    public async Task PersistedThreeFieldReceipt_ReplaysWithoutReinterpretingRound()
    {
        var (driver, trip, reservation) = await ReserveAsync();
        var current = Release(driver, trip, reservation);
        var payload = System.Text.Json.Nodes.JsonNode.Parse(current.Payload.GetRawText())!;
        payload.AsObject().Remove("matchingRound");
        var legacy = current with { Payload = JsonSerializer.SerializeToElement(payload) };
        // Upgrade fixture: the previous consumer accepted this payload and retained its fingerprint.
        var fingerprint = DriverCommand.Parse(legacy).Fingerprint;
        var released = await db.CreateRepository().TryReleaseAsync(driver, trip, reservation);
        Assert.Equal(DriverMutationOutcome.Applied, released.Outcome);
        var original = new DriverLifecycleResult("Applied", 4,
            DriverEventMessageIds.ForDriverEffect(driver, reservation, 4, "AVAILABLE"));
        await using (var conn = await db.DataSource.OpenConnectionAsync())
        await using (var seed = new NpgsqlCommand("""
            INSERT INTO driver_command_receipts (message_id, fingerprint, disposition, version, event_message_id)
            VALUES (@id, @fingerprint, 'Applied', 4, @event)
            """, conn))
        {
            seed.Parameters.AddWithValue("id", legacy.MessageId);
            seed.Parameters.AddWithValue("fingerprint", fingerprint);
            seed.Parameters.AddWithValue("event", original.EventMessageId!);
            await seed.ExecuteNonQueryAsync();
        }
        var next = Guid.NewGuid();
        await db.CreateRepository().TryReserveAsync(driver, Guid.NewGuid(), next, 4, matchingRound: 2);
        Assert.Equal(original, await db.CreateRepository().HandleCommandAsync(legacy));
        await AssertReceipt(legacy, original);
        await Assert.ThrowsAsync<ArgumentException>(() => db.CreateRepository().HandleCommandAsync(
            legacy with { MessageId = Guid.NewGuid().ToString("N") }));
        var unchanged = (await db.CreateRepository().LoadAsync(driver))!;
        Assert.Equal(next, unchanged.ReservationId); Assert.Equal(5, unchanged.Version);
        Assert.Equal(3, await Events(driver));
    }

    [Fact]
    public async Task LateReleaseAndReplay_CannotFreeNewReservation()
    {
        var (driver, trip, reservation) = await ReserveAsync();
        var command = Release(driver, trip, reservation);
        var result = await db.CreateRepository().HandleCommandAsync(command);
        var newer = Guid.NewGuid(); var nextTrip = Guid.NewGuid();
        Assert.Equal(ReserveOutcome.Won, (await db.CreateRepository().TryReserveAsync(driver,
            nextTrip, newer, 4, matchingRound: 2)).Outcome);
        Assert.Equal(result, await db.CreateRepository().HandleCommandAsync(command));
        var stale = command with { MessageId = Guid.NewGuid().ToString("N") };
        var rejected = await db.CreateRepository().HandleCommandAsync(stale);
        Assert.Equal("Rejected", rejected.Disposition);
        await AssertReceipt(stale, rejected);
        var current = (await db.CreateRepository().LoadAsync(driver))!;
        Assert.Equal(newer, current.ReservationId); Assert.Equal(nextTrip, current.ReservedTripId);
        Assert.Equal(2, current.ReservationRound); Assert.Equal(5, current.Version);
        Assert.Equal(3, await Events(driver));
    }

    [Fact]
    public async Task Con04_LateReleaseR1_ViaBroker_CannotFreeR2_AndReplaysReceipt()
    {
        await PrepareBroker();
        var (driver, trip1, r1) = await ReserveAsync(round: 1);
        var repo = db.CreateRepository();
        var releaseR1 = Release(driver, trip1, r1, round: 1);

        await using var publisher = new ConfirmedPublisher(Factory);
        await using var consumer = await ManualAckConsumer.StartAsync(Factory, publisher,
            DriverCommand.Queue, DriverCommand.SupportedTypes,
            new DriverCommandHandler(repo).HandleAsync, prefetch: 1);

        await publisher.PublishAsync(RideTopology.CommandsExchange,
            "driver.release-reservation.v1", releaseR1);
        await Until(() => Task.FromResult(consumer.AcknowledgedCount == 1));
        var applied = await repo.HandleCommandAsync(releaseR1);
        Assert.Equal("Applied", applied.Disposition);
        Assert.Equal(4, applied.Version);

        var trip2 = Guid.NewGuid();
        var r2 = Guid.NewGuid();
        Assert.Equal(ReserveOutcome.Won, (await repo.TryReserveAsync(
            driver, trip2, r2, 4, matchingRound: 2)).Outcome);

        var before = (await repo.LoadAsync(driver))!;
        var reservedBefore = await EventsByType(driver, DriverEventTypes.Reserved);
        var releasedBefore = await EventsByType(driver, DriverEventTypes.ReservationReleased);

        // New envelope identity => execute the late R1 command. The exact
        // committed envelope then replays its original receipt.
        var lateR1 = releaseR1 with { MessageId = Guid.NewGuid().ToString("N") };
        await publisher.PublishAsync(RideTopology.CommandsExchange,
            "driver.release-reservation.v1", lateR1);
        await publisher.PublishAsync(RideTopology.CommandsExchange,
            "driver.release-reservation.v1", releaseR1);
        await Until(() => Task.FromResult(consumer.AcknowledgedCount == 3));

        // The broker consumer, not a direct call, must have written both
        // receipts and left R2 untouched before any replay below.
        var brokerLateReceipt = await ReadReceiptAsync(lateR1.MessageId);
        Assert.Equal("Rejected", brokerLateReceipt.Disposition);
        Assert.Equal(DriverErrors.StaleReservation, brokerLateReceipt.Reason);
        var brokerOriginalReceipt = await ReadReceiptAsync(releaseR1.MessageId);
        Assert.Equal("Applied", brokerOriginalReceipt.Disposition);
        var brokerFinal = (await repo.LoadAsync(driver))!;
        Assert.Equal(before.Version, brokerFinal.Version);
        Assert.Equal(r2, brokerFinal.ReservationId);
        Assert.Equal(trip2, brokerFinal.ReservedTripId);

        var late = await repo.HandleCommandAsync(lateR1);
        Assert.Equal("Rejected", late.Disposition);
        Assert.Equal(DriverErrors.StaleReservation, late.Reason);
        Assert.Null(late.EventMessageId);
        await AssertReceipt(lateR1, late);
        Assert.Equal(applied, await repo.HandleCommandAsync(releaseR1));

        var after = (await repo.LoadAsync(driver))!;
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(DriverStatus.Reserved, after.Status);
        Assert.Equal(trip2, after.ReservedTripId);
        Assert.Equal(r2, after.ReservationId);
        Assert.Equal(2, after.ReservationRound);
        Assert.Equal(reservedBefore, await EventsByType(driver, DriverEventTypes.Reserved));
        Assert.Equal(releasedBefore, await EventsByType(driver, DriverEventTypes.ReservationReleased));
        Assert.Equal(1, await ReleaseEventsForReservation(r1));
        Assert.Equal(0, await ReleaseEventsForReservation(r2));

        _output.WriteLine(
            $"W11 CON-04-broker driver={driver} r1={r1} r2={r2} trip2={trip2} " +
            $"lateDisposition=Rejected reason={late.Reason} replayDisposition=Applied " +
            $"finalVersion={after.Version} releaseIntentsR2=0");
    }

    [Theory]
    [InlineData("outbox_messages")]
    [InlineData("driver_command_receipts")]
    public async Task RequiredInsertFailure_RollsBackReleaseAndReceipt(string table)
    {
        var (driver, trip, reservation) = await ReserveAsync();
        var command = Release(driver, trip, reservation);
        await using var conn = await db.DataSource.OpenConnectionAsync();
        async Task Sql(string sql) { await using var cmd = new NpgsqlCommand(sql, conn); await cmd.ExecuteNonQueryAsync(); }
        await Sql($"""
            CREATE FUNCTION fail_compensation_insert() RETURNS trigger AS $$
            BEGIN RAISE EXCEPTION 'required compensation insert failed'; END; $$ LANGUAGE plpgsql;
            CREATE TRIGGER fail_compensation_insert BEFORE INSERT ON {table}
            FOR EACH ROW EXECUTE FUNCTION fail_compensation_insert();
            """);
        try
        {
            await Assert.ThrowsAsync<PostgresException>(() => db.CreateRepository().HandleCommandAsync(command));
            var current = (await db.CreateRepository().LoadAsync(driver))!;
            Assert.Equal(DriverStatus.Reserved, current.Status); Assert.Equal(3, current.Version);
            Assert.Equal(reservation, current.ReservationId); Assert.Equal(1, current.ReservationRound);
            Assert.Equal(1, await Events(driver));
            Assert.Equal(0, await ScalarAsync("SELECT count(*) FROM driver_command_receipts WHERE message_id = @id", command.MessageId));
        }
        finally { await Sql($"DROP TRIGGER fail_compensation_insert ON {table}; DROP FUNCTION fail_compensation_insert();"); }
        Assert.Equal("Applied", (await db.CreateRepository().HandleCommandAsync(command)).Disposition);
    }

    private async Task PrepareBroker()
    {
        await using var connection = await Factory.CreateConnectionAsync();
        await RideTopology.DeclareAsync(connection);
        await using var channel = await connection.CreateChannelAsync();
        await channel.QueuePurgeAsync(DriverCommand.Queue);
        await channel.QueuePurgeAsync("trip.driver-state.q");
    }

    private static async Task Until(Func<Task<bool>> assertion)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        while (!await assertion()) await Task.Delay(25, deadline.Token);
    }

    [Fact]
    public async Task BrokerAndDatabaseRestart_PreservePendingCommandReceiptAndEvent()
    {
        await PrepareBroker();
        var (driver, trip, reservation) = await ReserveAsync();
        var command = Release(driver, trip, reservation);
        await using (var before = new ConfirmedPublisher(Factory))
            await before.PublishAsync(RideTopology.CommandsExchange, "driver.release-reservation.v1", command);
        await rabbit.RestartOwnedAsync();
        await db.RestartOwnedAsync();
        await using var publisher = new ConfirmedPublisher(Factory);
        await using (var consumer = await ManualAckConsumer.StartAsync(Factory, publisher, DriverCommand.Queue,
            DriverCommand.SupportedTypes, new DriverCommandHandler(db.CreateRepository()).HandleAsync, prefetch: 1))
            await Until(() => Task.FromResult(consumer.AcknowledgedCount == 1));
        var result = await db.CreateRepository().HandleCommandAsync(command);
        Assert.Equal("Applied", result.Disposition);
        Assert.Equal(OutboxStatus.Pending, (await db.OutboxStore.LoadAsync(result.EventMessageId!))!.Status);
        await db.RestartOwnedAsync();
        Assert.Equal(result, await db.CreateRepository().HandleCommandAsync(command));
        await AssertReceipt(command, result);
        var available = (await db.CreateRepository().LoadAsync(driver))!;
        Assert.Equal(DriverStatus.Available, available.Status); Assert.Equal(4, available.Version);
        Assert.Null(available.ReservationId); Assert.Equal(2, await Events(driver));
        Assert.Equal(OutboxStatus.Pending, (await db.OutboxStore.LoadAsync(result.EventMessageId!))!.Status);
        await new OutboxRelay(db.OutboxStore, new RabbitOutboxPublisher(publisher), new OutboxRelayOptions
            { RelayId = "after-database-restart", BatchSize = 1000 }).RunOnceAsync();
        Assert.Equal(OutboxStatus.Published, (await db.OutboxStore.LoadAsync(result.EventMessageId!))!.Status);
    }

    [Fact]
    public async Task CommitBeforeAck_ReleaseRedeliveryReturnsCommittedReceipt()
    {
        await PrepareBroker();
        var (driver, trip, reservation) = await ReserveAsync();
        var command = Release(driver, trip, reservation);
        await using var publisher = new ConfirmedPublisher(Factory);
        var committed = new TaskCompletionSource<DriverLifecycleResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var first = await ManualAckConsumer.StartAsync(Factory, publisher, DriverCommand.Queue,
            DriverCommand.SupportedTypes, async (message, ct) =>
            {
                committed.TrySetResult(await db.CreateRepository().HandleCommandAsync(message, ct));
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }, prefetch: 1);
        await publisher.PublishAsync(RideTopology.CommandsExchange, "driver.release-reservation.v1", command);
        var result = await committed.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal("Applied", result.Disposition); Assert.Equal(0, first.AcknowledgedCount);
        await first.DisposeAsync();
        await using var second = await ManualAckConsumer.StartAsync(Factory, publisher, DriverCommand.Queue,
            DriverCommand.SupportedTypes, new DriverCommandHandler(db.CreateRepository()).HandleAsync, prefetch: 1);
        await Until(() => Task.FromResult(second.AcknowledgedCount == 1));
        Assert.Equal(result, await db.CreateRepository().HandleCommandAsync(command));
        await AssertReceipt(command, result);
        var available = (await db.CreateRepository().LoadAsync(driver))!;
        Assert.Equal(4, available.Version); Assert.Equal(DriverStatus.Available, available.Status);
        Assert.Null(available.ReservationId);
        Assert.Equal(2, await Events(driver));
    }

    [Fact]
    public async Task ReleaseRelayAndTripIntake_ConfirmOnlyExactCleanup_AndRetainLateObligations()
    {
        await PrepareBroker();
        await using (var cleanup = await db.DataSource.OpenConnectionAsync())
        await using (var truncate = new NpgsqlCommand("TRUNCATE outbox_messages", cleanup))
            await truncate.ExecuteNonQueryAsync();
        var (driver, tripId, reservation) = await ReserveAsync();
        var trip = CancelledTrip(tripId, driver, reservation);
        var database = "trip_compensation_" + Guid.NewGuid().ToString("N");
        await using var admin = await db.DataSource.OpenConnectionAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE {database}", admin)) await create.ExecuteNonQueryAsync();
        var tripConnection = new NpgsqlConnectionStringBuilder(db.ConnectionString) { Database = database, Pooling = false }.ConnectionString;
        TripDbContext TripDb() => new(new DbContextOptionsBuilder<TripDbContext>().UseNpgsql(tripConnection).Options);
        try
        {
            await using (var setup = TripDb())
            {
                await setup.Database.MigrateAsync(); setup.Trips.Add(trip); await setup.SaveChangesAsync();
            }
            await using var publisher = new ConfirmedPublisher(Factory);
            await using var intake = await ManualAckConsumer.StartAsync(Factory, publisher, "trip.driver-state.q",
                TripMessageHandler.Subscriptions["trip.driver-state.q"], async (message, ct) =>
                {
                    await using var scope = TripDb();
                    await new TripMessageHandler(scope, TimeProvider.System, NullLogger<TripMessageHandler>.Instance)
                        .HandleAsync("trip.driver-state.q", message, ct);
                }, prefetch: 1);
            async Task<Trip> Reload()
            {
                await using var scope = TripDb(); return await scope.Trips.SingleAsync(t => t.TripId == tripId);
            }
            var wrong = MessageEnvelope<JsonElement>.Create("DriverReservationReleasedV1", tripId.ToString("D"),
                JsonSerializer.SerializeToElement(new { driverId = driver, tripId, reservationId = Guid.NewGuid() }));
            await publisher.PublishAsync(RideTopology.EventsExchange, "driver.reservation-released.v1", wrong);
            await Until(() => Task.FromResult(intake.AcknowledgedCount == 1));
            Assert.Null(Assert.Single((await Reload()).CleanupIntents, i => i.Kind == TripCleanupKind.ReleaseReservation).CompletedAt);
            await using (var verify = TripDb())
                Assert.Equal("NeedsReview", (await verify.MessageReceipts.SingleAsync(r => r.MessageId == wrong.MessageId)).Disposition);

            var command = Release(driver, tripId, reservation);
            await using var consumer = await ManualAckConsumer.StartAsync(Factory, publisher, DriverCommand.Queue,
                DriverCommand.SupportedTypes, new DriverCommandHandler(db.CreateRepository()).HandleAsync, prefetch: 1);
            await publisher.PublishAsync(RideTopology.CommandsExchange, "driver.release-reservation.v1", command);
            await Until(() => Task.FromResult(consumer.AcknowledgedCount == 1));
            var result = await db.CreateRepository().HandleCommandAsync(command);
            Assert.Equal("Applied", result.Disposition); await AssertReceipt(command, result);
            // The command committed and was ACKed, but no authoritative event has reached Trip yet.
            Assert.Null(Assert.Single((await Reload()).CleanupIntents, i => i.Kind == TripCleanupKind.ReleaseReservation).CompletedAt);
            await using var connection = await Factory.CreateConnectionAsync();
            await using var channel = await connection.CreateChannelAsync();
            var sink = (await channel.QueueDeclareAsync()).QueueName;
            await channel.QueueBindAsync(sink, RideTopology.EventsExchange, "driver.reservation-released.v1");
            var crash = new OutboxRelay(db.OutboxStore, new RabbitOutboxPublisher(publisher), new OutboxRelayOptions
            {
                RelayId = "compensation-mark-loss",
                AfterPublishBeforeMark = (message, _) => message.MessageId == result.EventMessageId
                    ? throw new IOException("confirmed release publish before mark lost") : Task.CompletedTask
            });
            await Assert.ThrowsAsync<IOException>(() => crash.RunOnceAsync());
            await Until(async () => (await Reload()).CleanupIntents.Single(i => i.Kind == TripCleanupKind.ReleaseReservation).CompletedAt is not null);
            await new OutboxRelay(db.OutboxStore, new RabbitOutboxPublisher(publisher), new OutboxRelayOptions
                { RelayId = "compensation-restarted", ClaimLease = TimeSpan.Zero }).RunOnceAsync();
            await Until(() => Task.FromResult(intake.AcknowledgedCount == 4)); // wrong + reserve + release twice
            var first = await channel.BasicGetAsync(sink, true); var second = await channel.BasicGetAsync(sink, true);
            Assert.NotNull(first); Assert.NotNull(second);
            Assert.Equal(result.EventMessageId, first!.BasicProperties.MessageId);
            Assert.Equal(first.BasicProperties.MessageId, second!.BasicProperties.MessageId);
            Assert.Equal(first.Body.ToArray(), second.Body.ToArray());
            Assert.Equal(OutboxStatus.Published, (await db.OutboxStore.LoadAsync(result.EventMessageId!))!.Status);
            await using (var verify = TripDb())
                Assert.Equal(1, await verify.MessageReceipts.CountAsync(r => r.MessageId == result.EventMessageId));
            var terminal = await Reload(); Assert.Equal(TripState.Cancelled, terminal.State);
            Assert.NotNull(terminal.CleanupIntents.Single(i => i.ReservationId == reservation).CompletedAt);
            Assert.Equal(DriverStatus.Available, (await db.CreateRepository().LoadAsync(driver))!.Status);

            // A later real reserve fact creates a separate pending obligation on the terminal Trip.
            var lateReservation = Guid.NewGuid();
            Assert.Equal(ReserveOutcome.Won, (await db.CreateRepository().TryReserveAsync(driver, tripId,
                lateReservation, 4, matchingRound: 1)).Outcome);
            await new OutboxRelay(db.OutboxStore, new RabbitOutboxPublisher(publisher), new OutboxRelayOptions
                { RelayId = "late-reservation" }).RunOnceAsync();
            await Until(() => Task.FromResult(intake.AcknowledgedCount == 5));
            terminal = await Reload(); Assert.Equal(TripState.Cancelled, terminal.State);
            Assert.Null(terminal.CleanupIntents.Single(i => i.ReservationId == lateReservation).CompletedAt);
            Assert.NotNull(terminal.CleanupIntents.Single(i => i.ReservationId == reservation).CompletedAt);
            Assert.Equal(lateReservation, (await db.CreateRepository().LoadAsync(driver))!.ReservationId);
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE {database} WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
