// B-W09-01/02/05: Driver authoritative events are durable Outbox intents
// committed with the Driver row, then published by C's shared relay on REAL
// PostgreSQL + REAL RabbitMQ. These tests assert the full seam: same-tx
// recording, atomic rollback on Outbox failure, exact replay without a second
// intent, confirmed publish/mark, stable message identity across relay
// restart, and broker-unavailable retention. No fake-green path.
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using RabbitMQ.Client;
using RideHailing.DriverService.Application.Messaging;
using RideHailing.DriverService.Domain.Drivers;
using RideHailing.DriverService.Infrastructure.Persistence;
using RideHailing.Messaging.Outbox;
using RideHailing.Messaging.Reliability;
using RideHailing.Messaging.Topology;

namespace RideHailing.DriverService.IntegrationTests;

[Collection(DriverBrokerCollection.Name)]
public class DriverEventPublishTests
{
    private readonly DriverPostgresDatabase _db;
    private readonly RabbitMqDatabase _rabbit;

    public DriverEventPublishTests(DriverPostgresDatabase db, RabbitMqDatabase rabbit)
    {
        _db = db;
        _rabbit = rabbit;
    }

    private sealed class TestLogger<T> : ILogger<T>
    {
        IDisposable? ILogger.BeginScope<TState>(TState state) => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
        }
    }

    private sealed class SimulatedCrashException : Exception;

    private static readonly string[] DriverTestKeys =
    [
        "driver.available.v1", "driver.offline.v1", "driver.reserved.v1",
        "driver.reservation-released.v1", "driver.location-accepted.v1",
    ];

    private ConnectionFactory Factory(string clientName = "w9-test") => new()
    {
        Uri = new Uri(_rabbit.ConnectionString),
        ClientProvidedName = clientName,
        AutomaticRecoveryEnabled = false,
    };

    private static OutboxRelayOptions RelayOptions(string relayId,
        TimeSpan? claimLease = null,
        Func<OutboxMessage, CancellationToken, Task>? beforePublish = null,
        Func<OutboxMessage, CancellationToken, Task>? afterPublishBeforeMark = null,
        int maxAttempts = 5,
        TimeSpan? retryDelay = null) => new()
    {
        RelayId = relayId,
        BatchSize = 100,
        ClaimLease = claimLease ?? TimeSpan.FromMinutes(1),
        PollInterval = TimeSpan.FromMilliseconds(50),
        MaxAttempts = maxAttempts,
        RetryDelay = retryDelay ?? TimeSpan.Zero,
        BeforePublish = beforePublish,
        AfterPublishBeforeMark = afterPublishBeforeMark,
    };

    private async Task PrepareAsync()
    {
        // This class owns the broker collection's driver_db. Truncating between
        // tests prevents a previous test's pending intents from being claimed
        // by this test's relay; tests within one xUnit collection are serial.
        await using var conn = await _db.DataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("TRUNCATE outbox_messages;", conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<Guid> SeedAvailableAsync(DriverRepository repo)
    {
        var driver = Driver.Create(Guid.NewGuid(), Guid.NewGuid());
        driver.GoOnline(driver.Version);
        Assert.True(await repo.TryInsertAsync(driver));
        return driver.DriverId;
    }

    private async Task<IReadOnlyList<OutboxMessage>> LoadDriverOutboxAsync(Guid driverId)
    {
        var ids = new List<string>();
        await using (var conn = await _db.DataSource.OpenConnectionAsync())
        await using (var cmd = new NpgsqlCommand(
            "SELECT message_id FROM outbox_messages WHERE aggregate_id = @id " +
            "ORDER BY occurred_at, message_id;", conn))
        {
            cmd.Parameters.AddWithValue("id", driverId.ToString("N"));
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                ids.Add(reader.GetString(0));
            }
        }

        var rows = new List<OutboxMessage>(ids.Count);
        foreach (var id in ids)
        {
            rows.Add((await _db.OutboxStore.LoadAsync(id))!);
        }

        return rows;
    }

    private async Task DeclareTestQueueAsync(string queue)
    {
        // Bind BEFORE any publish: a topic exchange drops messages with no
        // matching binding at publish time, so the queue must exist first.
        // RabbitMQ 4.x forbids transient non-exclusive queues, so test queues
        // are durable classic (the container is disposed after the run).
        using var connection = await Factory("w9-test-declare").CreateConnectionAsync();
        await RideTopology.DeclareAsync(connection);
        await using var channel = await connection.CreateChannelAsync();
        await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false);
        foreach (var key in DriverTestKeys)
        {
            await channel.QueueBindAsync(queue, "ride.events", key);
        }
    }

    private async Task<(string RoutingKey, JsonElement Envelope, IDictionary<string, object?> Headers)>
        ConsumeOneAsync(string queue, TimeSpan? timeout = null)
    {
        using var connection = await Factory("w9-test-consume").CreateConnectionAsync();
        await RideTopology.DeclareAsync(connection);
        await using var channel = await connection.CreateChannelAsync();
        await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false);
        foreach (var key in DriverTestKeys)
        {
            await channel.QueueBindAsync(queue, "ride.events", key);
        }

        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(30));
        while (!cts.IsCancellationRequested)
        {
            var result = await channel.BasicGetAsync(queue, autoAck: true, cts.Token);
            if (result is not null)
            {
                var headers = new Dictionary<string, object?>();
                if (result.BasicProperties?.Headers is not null)
                {
                    foreach (var kv in result.BasicProperties.Headers)
                    {
                        headers[kv.Key] = kv.Value;
                    }
                }

                return (result.RoutingKey,
                    JsonDocument.Parse(Encoding.UTF8.GetString(result.Body.ToArray())).RootElement,
                    headers);
            }

            try
            {
                await Task.Delay(100, cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        Assert.Fail("Expected a published Driver event on the wire.");
        throw new InvalidOperationException("unreachable");
    }

    private async Task<List<(string RoutingKey, JsonElement Envelope, IDictionary<string, object?> Headers)>>
        ConsumeManyAsync(string queue, int count, TimeSpan? timeout = null)
    {
        var messages = new List<(string, JsonElement, IDictionary<string, object?>)>(count);
        for (var i = 0; i < count; i++)
        {
            messages.Add(await ConsumeOneAsync(queue, timeout));
        }

        return messages;
    }

    private static string Prop(JsonElement envelope, params string[] path)
    {
        var current = envelope;
        foreach (var segment in path)
        {
            current = current.GetProperty(segment);
        }

        return current.GetString()!;
    }

    [Fact]
    public async Task Reserve_Win_Records_Outbox_In_Same_Transaction_Then_Relay_Publishes_Same_Identity()
    {
        await PrepareAsync();
        var repo = _db.CreateRepository();
        var driverId = await SeedAvailableAsync(repo);
        var start = (await repo.LoadAsync(driverId))!;
        var trip = Guid.NewGuid();
        var reservation = Guid.NewGuid();

        var won = await repo.TryReserveAsync(driverId, trip, reservation, start.Version, 3);
        Assert.Equal(ReserveOutcome.Won, won.Outcome);

        // The intent exists BEFORE any broker interaction. The repository has
        // no broker dependency; the relay is the only publisher.
        var row = Assert.Single(await LoadDriverOutboxAsync(driverId));
        Assert.Equal(OutboxStatus.Pending, row.Status);
        Assert.Equal(DriverEventRoutes.EventsExchange, row.Exchange);
        Assert.Equal(DriverEventRoutes.Reserved, row.RoutingKey);
        Assert.Equal(DriverEventTypes.Reserved, row.MessageType);
        Assert.Equal(driverId.ToString("N"), row.AggregateId);
        Assert.Equal(start.Version + 1, row.AggregateVersion);
        using (var payload = JsonDocument.Parse(row.PayloadJson))
        {
            Assert.Equal(3, payload.RootElement.GetProperty("matchingRound").GetInt32());
        }

        var queue = "w9-test-reserve-" + Guid.NewGuid().ToString("N");
        await DeclareTestQueueAsync(queue);
        await using var publisher = new ConfirmedPublisher(Factory("w9-test-relay"));
        var relay = new OutboxRelay(_db.OutboxStore, new RabbitOutboxPublisher(publisher),
            RelayOptions("w9-reserve-" + Guid.NewGuid().ToString("N")));
        Assert.Equal(1, await relay.RunOnceAsync());

        var (routing, envelope, _) = await ConsumeOneAsync(queue);
        Assert.Equal("driver.reserved.v1", routing);
        Assert.Equal("DriverReservedV1", Prop(envelope, "messageType"));
        Assert.Equal(1, envelope.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(row.MessageId, Prop(envelope, "messageId"));
        Assert.Equal(row.CorrelationId, Prop(envelope, "correlationId"));
        Assert.Equal(driverId, Guid.Parse(Prop(envelope, "payload", "driverId")));
        Assert.Equal("RESERVED", Prop(envelope, "payload", "status"));
        Assert.Equal(trip, Guid.Parse(Prop(envelope, "payload", "tripId")));
        Assert.Equal(reservation, Guid.Parse(Prop(envelope, "payload", "reservationId")));
        Assert.Equal(3, envelope.GetProperty("payload").GetProperty("matchingRound").GetInt32());
        Assert.Equal("driver", Prop(envelope, "aggregateType"));
        Assert.Equal(driverId.ToString("N"), Prop(envelope, "aggregateId"));

        var published = await _db.OutboxStore.LoadAsync(row.MessageId);
        Assert.Equal(OutboxStatus.Published, published!.Status);
        Assert.NotNull(published.PublishedAt);
        Assert.Null(published.ClaimedBy);

        // Exact same-effect replay: no second committed effect, no second
        // intent, no version or reservation change. The original row remains
        // the single authoritative event for this reservation.
        var replayed = await repo.TryReserveAsync(
            driverId, trip, reservation, start.Version, matchingRound: 9);
        Assert.Equal(ReserveOutcome.Replayed, replayed.Outcome);
        var afterReplay = (await repo.LoadAsync(driverId))!;
        Assert.Equal(start.Version + 1, afterReplay.Version);
        Assert.Equal(3, afterReplay.ReservationRound);
        Assert.Single(await LoadDriverOutboxAsync(driverId));
    }

    [Fact]
    public async Task Release_Offline_Online_Record_Canonical_Events_Without_Broker_Before_Relay()
    {
        await PrepareAsync();
        var repo = _db.CreateRepository();
        var driverId = await SeedAvailableAsync(repo);
        var start = (await repo.LoadAsync(driverId))!;
        var trip = Guid.NewGuid();
        var reservation = Guid.NewGuid();
        Assert.Equal(ReserveOutcome.Won,
            (await repo.TryReserveAsync(driverId, trip, reservation, start.Version)).Outcome);

        var released = await repo.TryReleaseAsync(driverId, trip, reservation);
        Assert.Equal(DriverMutationOutcome.Applied, released.Outcome);
        var afterRelease = (await repo.LoadAsync(driverId))!;

        // Exact-token replay after the effect is a no-op: no new row.
        var releaseReplay = await repo.TryReleaseAsync(driverId, trip, reservation);
        Assert.Equal(DriverMutationOutcome.Unchanged, releaseReplay.Outcome);
        Assert.Equal(afterRelease.Version, (await repo.LoadAsync(driverId))!.Version);

        // available -> offline -> available, each a real effect.
        Assert.Equal(DriverMutationOutcome.Applied,
            (await repo.TryGoOfflineAsync(driverId, afterRelease.Version)).Outcome);
        var offline = (await repo.LoadAsync(driverId))!;
        Assert.Equal(DriverMutationOutcome.Unchanged,
            (await repo.TryGoOfflineAsync(driverId, offline.Version)).Outcome);
        Assert.Equal(DriverMutationOutcome.Applied,
            (await repo.TryGoOnlineAsync(driverId, offline.Version)).Outcome);
        var online = (await repo.LoadAsync(driverId))!;
        Assert.Equal(DriverMutationOutcome.Unchanged,
            (await repo.TryGoOnlineAsync(driverId, online.Version)).Outcome);

        // Exactly four effects: reserve, release, offline, online.
        var rows = await LoadDriverOutboxAsync(driverId);
        Assert.Equal(4, rows.Count);
        Assert.All(rows, row => Assert.Equal(OutboxStatus.Pending, row.Status));
        Assert.Equal(
            new[]
            {
                DriverEventRoutes.Reserved, DriverEventRoutes.ReservationReleased,
                DriverEventRoutes.Offline, DriverEventRoutes.Available,
            }.OrderBy(value => value, StringComparer.Ordinal),
            rows.Select(row => row.RoutingKey).OrderBy(value => value, StringComparer.Ordinal));

        var queue = "w9-test-routes-" + Guid.NewGuid().ToString("N");
        await DeclareTestQueueAsync(queue);
        await using var publisher = new ConfirmedPublisher(Factory("w9-test-routes-relay"));
        var relay = new OutboxRelay(_db.OutboxStore, new RabbitOutboxPublisher(publisher),
            RelayOptions("w9-routes-" + Guid.NewGuid().ToString("N")));
        Assert.Equal(4, await relay.RunOnceAsync());

        var delivered = await ConsumeManyAsync(queue, 4);
        Assert.Equal(
            new[]
            {
                "driver.reserved.v1", "driver.reservation-released.v1",
                "driver.offline.v1", "driver.available.v1",
            }.OrderBy(value => value, StringComparer.Ordinal),
            delivered.Select(message => message.RoutingKey).OrderBy(value => value, StringComparer.Ordinal));
        Assert.Equal(rows.Select(row => row.MessageId).OrderBy(value => value, StringComparer.Ordinal),
            delivered.Select(message => Prop(message.Envelope, "messageId"))
                .OrderBy(value => value, StringComparer.Ordinal));

        var releasedEnvelope = delivered.Single(message =>
            message.RoutingKey == "driver.reservation-released.v1").Envelope;
        Assert.Equal("AVAILABLE", Prop(releasedEnvelope, "payload", "status"));
        Assert.Equal(trip, Guid.Parse(Prop(releasedEnvelope, "payload", "tripId")));
        Assert.Equal(reservation, Guid.Parse(Prop(releasedEnvelope, "payload", "reservationId")));

        foreach (var row in rows)
        {
            var stored = await _db.OutboxStore.LoadAsync(row.MessageId);
            Assert.Equal(OutboxStatus.Published, stored!.Status);
        }
    }

    [Fact]
    public async Task Forced_Outbox_Insert_Failure_Rolls_Back_Every_Guarded_Transition()
    {
        await PrepareAsync();
        var repo = _db.CreateRepository();
        var reserveDriverId = await SeedAvailableAsync(repo);
        var reserveStart = (await repo.LoadAsync(reserveDriverId))!;

        var releaseDriverId = await SeedAvailableAsync(repo);
        var releaseStart = (await repo.LoadAsync(releaseDriverId))!;
        var trip = Guid.NewGuid();
        var reservation = Guid.NewGuid();
        Assert.Equal(ReserveOutcome.Won,
            (await repo.TryReserveAsync(releaseDriverId, trip, reservation, releaseStart.Version)).Outcome);

        var offlineDriverId = await SeedAvailableAsync(repo);
        var offlineStart = (await repo.LoadAsync(offlineDriverId))!;

        var offlineDriver = Driver.Create(Guid.NewGuid(), Guid.NewGuid());
        var onlineDriverId = offlineDriver.DriverId;
        Assert.True(await repo.TryInsertAsync(offlineDriver));
        var onlineStart = (await repo.LoadAsync(onlineDriverId))!;

        await using (var conn = await _db.DataSource.OpenConnectionAsync())
        {
            await using var cmd = new NpgsqlCommand("""
                CREATE OR REPLACE FUNCTION w9_fail_outbox_insert() RETURNS trigger AS $$
                BEGIN
                    RAISE EXCEPTION 'simulated Outbox insert failure';
                END;
                $$ LANGUAGE plpgsql;
                CREATE TRIGGER w9_fail_outbox_insert
                    BEFORE INSERT ON outbox_messages
                    FOR EACH ROW EXECUTE FUNCTION w9_fail_outbox_insert();
                """, conn);
            await cmd.ExecuteNonQueryAsync();
        }

        try
        {
            await Assert.ThrowsAsync<PostgresException>(() => repo.TryReserveAsync(
                reserveDriverId, Guid.NewGuid(), Guid.NewGuid(), reserveStart.Version));

            var reserveAfter = (await repo.LoadAsync(reserveDriverId))!;
            Assert.Equal(DriverStatus.Available, reserveAfter.Status);
            Assert.Equal(reserveStart.Version, reserveAfter.Version);
            Assert.Null(reserveAfter.ReservationId);
            Assert.Empty(await LoadDriverOutboxAsync(reserveDriverId));

            await Assert.ThrowsAsync<PostgresException>(() =>
                repo.TryReleaseAsync(releaseDriverId, trip, reservation));

            var releaseAfter = (await repo.LoadAsync(releaseDriverId))!;
            Assert.Equal(DriverStatus.Reserved, releaseAfter.Status);
            Assert.Equal(releaseStart.Version + 1, releaseAfter.Version);
            Assert.Equal(trip, releaseAfter.ReservedTripId);
            Assert.Equal(reservation, releaseAfter.ReservationId);
            Assert.Single(await LoadDriverOutboxAsync(releaseDriverId));

            await Assert.ThrowsAsync<PostgresException>(() =>
                repo.TryGoOfflineAsync(offlineDriverId, offlineStart.Version));

            var offlineAfter = (await repo.LoadAsync(offlineDriverId))!;
            Assert.Equal(DriverStatus.Available, offlineAfter.Status);
            Assert.Equal(offlineStart.Version, offlineAfter.Version);
            Assert.Empty(await LoadDriverOutboxAsync(offlineDriverId));

            await Assert.ThrowsAsync<PostgresException>(() =>
                repo.TryGoOnlineAsync(onlineDriverId, onlineStart.Version));

            var onlineAfter = (await repo.LoadAsync(onlineDriverId))!;
            Assert.Equal(DriverStatus.Offline, onlineAfter.Status);
            Assert.Equal(onlineStart.Version, onlineAfter.Version);
            Assert.Empty(await LoadDriverOutboxAsync(onlineDriverId));
        }
        finally
        {
            await using var conn = await _db.DataSource.OpenConnectionAsync();
            await using var drop = new NpgsqlCommand(
                "DROP TRIGGER IF EXISTS w9_fail_outbox_insert ON outbox_messages; " +
                "DROP FUNCTION IF EXISTS w9_fail_outbox_insert();", conn);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task Missing_Outbox_Schema_Fails_Closed_Without_State_Change()
    {
        await PrepareAsync();
        var repo = _db.CreateRepository();
        var driverId = await SeedAvailableAsync(repo);
        var start = (await repo.LoadAsync(driverId))!;
        var backupTable = "outbox_messages_w9_missing_" + Guid.NewGuid().ToString("N");

        await using (var conn = await _db.DataSource.OpenConnectionAsync())
        {
            await using var cmd = new NpgsqlCommand(
                $"ALTER TABLE outbox_messages RENAME TO {backupTable};", conn);
            await cmd.ExecuteNonQueryAsync();
        }

        try
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => repo.TryReserveAsync(
                driverId, Guid.NewGuid(), Guid.NewGuid(), start.Version));
            Assert.Equal(PostgresErrorCodes.UndefinedTable, error.SqlState);

            var after = (await repo.LoadAsync(driverId))!;
            Assert.Equal(DriverStatus.Available, after.Status);
            Assert.Equal(start.Version, after.Version);
            Assert.Null(after.ReservedTripId);
            Assert.Null(after.ReservationId);
        }
        finally
        {
            await using var conn = await _db.DataSource.OpenConnectionAsync();
            await using var cmd = new NpgsqlCommand(
                $"ALTER TABLE {backupTable} RENAME TO outbox_messages;", conn);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task Crash_After_Publish_Before_Mark_Republishes_Same_MessageId()
    {
        await PrepareAsync();
        var repo = _db.CreateRepository();
        var driverId = await SeedAvailableAsync(repo);
        var start = (await repo.LoadAsync(driverId))!;
        Assert.Equal(ReserveOutcome.Won, (await repo.TryReserveAsync(
            driverId, Guid.NewGuid(), Guid.NewGuid(), start.Version)).Outcome);
        var row = Assert.Single(await LoadDriverOutboxAsync(driverId));

        var queue = "w9-test-marklost-" + Guid.NewGuid().ToString("N");
        await DeclareTestQueueAsync(queue);
        await using var publisher = new ConfirmedPublisher(Factory("w9-test-marklost"));

        var relayA = new OutboxRelay(_db.OutboxStore, new RabbitOutboxPublisher(publisher),
            RelayOptions("w9-marklost-a-" + Guid.NewGuid().ToString("N"),
                afterPublishBeforeMark: (_, _) =>
                    Task.FromException(new SimulatedCrashException())));
        await Assert.ThrowsAsync<SimulatedCrashException>(() => relayA.RunOnceAsync());

        var afterCrash = await _db.OutboxStore.LoadAsync(row.MessageId);
        Assert.Equal(OutboxStatus.Pending, afterCrash!.Status);
        Assert.NotNull(afterCrash.ClaimedBy);
        Assert.Null(afterCrash.PublishedAt);

        var first = await ConsumeOneAsync(queue);
        Assert.Equal(row.MessageId, Prop(first.Envelope, "messageId"));

        var relayB = new OutboxRelay(_db.OutboxStore, new RabbitOutboxPublisher(publisher),
            RelayOptions("w9-marklost-b-" + Guid.NewGuid().ToString("N"),
                claimLease: TimeSpan.Zero));
        Assert.Equal(1, await relayB.RunOnceAsync());

        var second = await ConsumeOneAsync(queue);
        Assert.Equal(row.MessageId, Prop(second.Envelope, "messageId"));

        var final = await _db.OutboxStore.LoadAsync(row.MessageId);
        Assert.Equal(OutboxStatus.Published, final!.Status);
        Assert.Single(await LoadDriverOutboxAsync(driverId));
    }

    [Fact]
    public async Task Crash_After_Claim_Before_Publish_Leaves_Pending_Row_For_Restarted_Relay()
    {
        await PrepareAsync();
        var repo = _db.CreateRepository();
        var driverId = await SeedAvailableAsync(repo);
        var start = (await repo.LoadAsync(driverId))!;
        Assert.Equal(ReserveOutcome.Won, (await repo.TryReserveAsync(
            driverId, Guid.NewGuid(), Guid.NewGuid(), start.Version)).Outcome);
        var row = Assert.Single(await LoadDriverOutboxAsync(driverId));

        await using var publisher = new ConfirmedPublisher(Factory("w9-test-claimlost"));
        var relayA = new OutboxRelay(_db.OutboxStore, new RabbitOutboxPublisher(publisher),
            RelayOptions("w9-claimlost-a-" + Guid.NewGuid().ToString("N"),
                beforePublish: (_, _) => Task.FromException(new SimulatedCrashException())));
        await Assert.ThrowsAsync<SimulatedCrashException>(() => relayA.RunOnceAsync());

        var afterCrash = await _db.OutboxStore.LoadAsync(row.MessageId);
        Assert.Equal(OutboxStatus.Pending, afterCrash!.Status);
        Assert.NotNull(afterCrash.ClaimedBy);

        var queue = "w9-test-claimlost-" + Guid.NewGuid().ToString("N");
        await DeclareTestQueueAsync(queue);
        var relayB = new OutboxRelay(_db.OutboxStore, new RabbitOutboxPublisher(publisher),
            RelayOptions("w9-claimlost-b-" + Guid.NewGuid().ToString("N"),
                claimLease: TimeSpan.Zero));
        Assert.Equal(1, await relayB.RunOnceAsync());

        var delivered = await ConsumeOneAsync(queue);
        Assert.Equal(row.MessageId, Prop(delivered.Envelope, "messageId"));
        Assert.Equal(OutboxStatus.Published,
            (await _db.OutboxStore.LoadAsync(row.MessageId))!.Status);
    }

    [Fact]
    public async Task Broker_Unavailable_Keeps_Intent_Pending_Then_Relay_Recovers()
    {
        await PrepareAsync();
        var repo = _db.CreateRepository();
        var driverId = await SeedAvailableAsync(repo);
        var start = (await repo.LoadAsync(driverId))!;
        Assert.Equal(ReserveOutcome.Won, (await repo.TryReserveAsync(
            driverId, Guid.NewGuid(), Guid.NewGuid(), start.Version)).Outcome);
        var row = Assert.Single(await LoadDriverOutboxAsync(driverId));

        // Port 1 is a real refused connection, not a mocked publisher failure.
        var deadFactory = new ConnectionFactory
        {
            HostName = "localhost",
            Port = 1,
            UserName = "guest",
            Password = "guest",
            AutomaticRecoveryEnabled = false,
            RequestedConnectionTimeout = TimeSpan.FromSeconds(2),
        };
        await using (var deadPublisher = new ConfirmedPublisher(deadFactory))
        {
            var failingRelay = new OutboxRelay(_db.OutboxStore,
                new RabbitOutboxPublisher(deadPublisher),
                RelayOptions("w9-dead-" + Guid.NewGuid().ToString("N"),
                    maxAttempts: 3, retryDelay: TimeSpan.Zero));
            Assert.Equal(1, await failingRelay.RunOnceAsync());
        }

        var afterFailure = await _db.OutboxStore.LoadAsync(row.MessageId);
        Assert.Equal(OutboxStatus.Pending, afterFailure!.Status);
        Assert.Equal(1, afterFailure.AttemptCount);
        Assert.NotNull(afterFailure.LastErrorCode);
        Assert.Null(afterFailure.PublishedAt);
        Assert.Null(afterFailure.ClaimedBy);

        var queue = "w9-test-recover-" + Guid.NewGuid().ToString("N");
        await DeclareTestQueueAsync(queue);
        await using var publisher = new ConfirmedPublisher(Factory("w9-test-recover"));
        var recoveringRelay = new OutboxRelay(_db.OutboxStore,
            new RabbitOutboxPublisher(publisher),
            RelayOptions("w9-recover-" + Guid.NewGuid().ToString("N")));
        Assert.Equal(1, await recoveringRelay.RunOnceAsync());

        var delivered = await ConsumeOneAsync(queue);
        Assert.Equal(row.MessageId, Prop(delivered.Envelope, "messageId"));
        var final = await _db.OutboxStore.LoadAsync(row.MessageId);
        Assert.Equal(OutboxStatus.Published, final!.Status);
        Assert.Null(final.LastErrorCode);
    }

    [Fact]
    public async Task Location_Accept_Publishes_Sample_Fact()
    {
        var payload = new DriverLocationEventPayload(
            Guid.NewGuid(), 106.7, 10.9, "sess", 12,
            new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
        var queue = "w9-test-loc-" + Guid.NewGuid().ToString("N");
        await DeclareTestQueueAsync(queue);
        await using var locPub = LocationPublisher();
        Assert.True(await locPub.PublishLocationAsync(payload));
        var (route, envelope, _) = await ConsumeOneAsync(queue);
        Assert.Equal("driver.location-accepted.v1", route);
        Assert.Equal("DriverLocationAcceptedV1", Prop(envelope, "messageType"));
        Assert.Equal(payload.DriverId, Guid.Parse(Prop(envelope, "payload", "driverId")));
        Assert.Equal(12L, envelope.GetProperty("payload").GetProperty("sequenceNo").GetInt64());
    }

    [Fact]
    public async Task Publish_Failure_Returns_False_Without_Throwing()
    {
        var dead = new DriverEventPublisher(
            Options.Create(new DriverBrokerOptions { Host = "localhost", Port = 1, PublishTimeoutSeconds = 2 }),
            new TestLogger<DriverEventPublisher>());
        Assert.False(await dead.PublishLocationAsync(new DriverLocationEventPayload(
            Guid.NewGuid(), 0, 0, "s", 0, DateTimeOffset.UtcNow)));
        await dead.DisposeAsync();
    }

    [Fact]
    public async Task Trace_Parent_Flows_Into_Published_Headers()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);

        using var producer = new Activity("w9-test-publish");
        producer.Start();
        var payload = new DriverLocationEventPayload(
            Guid.NewGuid(), 106.7, 10.9, "sess", 1, DateTimeOffset.UtcNow);
        var queue = "w9-test-trace-" + Guid.NewGuid().ToString("N");
        await DeclareTestQueueAsync(queue);
        await using var tracePub = LocationPublisher();
        Assert.True(await tracePub.PublishLocationAsync(payload));
        producer.Stop();

        var (_, _, headers) = await ConsumeOneAsync(queue);
        Assert.True(RideHailing.Messaging.Contracts.TraceHeaderPropagation.TryExtract(headers, out var ctx));
        Assert.Equal(producer.TraceId, ctx.TraceId);
    }

    private DriverEventPublisher LocationPublisher()
    {
        // Derive connection from the real test broker (Testcontainers maps a
        // random host port with guest credentials); defaults would auth-fail.
        var uri = new Uri(_rabbit.ConnectionString);
        var userInfo = uri.UserInfo.Split(':', 2);
        return new DriverEventPublisher(
            Options.Create(new DriverBrokerOptions
            {
                Host = uri.Host,
                Port = uri.Port == -1 ? 5672 : uri.Port,
                Username = userInfo.Length > 0 && userInfo[0].Length > 0
                    ? Uri.UnescapeDataString(userInfo[0]) : "guest",
                Password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : "guest",
            }),
            new TestLogger<DriverEventPublisher>());
    }
}
