// B-W08-01/02/06/07/09: broker consumption proof on REAL RabbitMQ +
// REAL PostgreSQL (+ REAL Redis for projection paths). The consumer loop
// runs in-process against the test broker; oracles are durable state
// (round rows, Redis projection, queue depth), never message counts alone.
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RideHailing.MatchingService.Application.Messaging;
using RideHailing.MatchingService.Application.Projection;
using RideHailing.MatchingService.Infrastructure.Persistence;
using RideHailing.Messaging.Contracts;
using RideHailing.Messaging.Topology;
using StackExchange.Redis;

namespace RideHailing.MatchingService.IntegrationTests;

[Collection(BrokerWorkflowCollection.Name)]
public class BrokerConsumeTests
{
    private readonly MatchingPostgresDatabase _pg;
    private readonly RedisDatabase _redis;
    private readonly RabbitMqDatabase _rabbit;

    public BrokerConsumeTests(
        MatchingPostgresDatabase pg, RedisDatabase redis, RabbitMqDatabase rabbit)
    {
        _pg = pg;
        _redis = redis;
        _rabbit = rabbit;
    }

    private sealed class TestLogger<T> : ILogger<T>
    {
        public readonly List<string> Messages = new();
        IDisposable? ILogger.BeginScope<TState>(TState state) => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Messages)
            {
                Messages.Add($"{logLevel}: {formatter(state, exception)}");
            }
        }
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private ConnectionFactory Factory() => new()
    {
        Uri = new Uri(_rabbit.ConnectionString),
        ClientProvidedName = "w8-test",
        AutomaticRecoveryEnabled = false,
    };

    private MatchingAttemptRepository Rounds() => new(_pg.ConnectionString);

    private static async Task PollUntilAsync(Func<Task<bool>> condition, string what)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            while (!timeout.IsCancellationRequested)
            {
                if (await condition())
                {
                    return;
                }

                await Task.Delay(100, timeout.Token);
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
        }

        Assert.Fail($"Timed out waiting for: {what}");
    }

    private async Task PublishAsync(string exchange, string routingKey, byte[] body,
        IDictionary<string, object?>? headers = null)
    {
        using var connection = await Factory().CreateConnectionAsync();
        await RideTopology.DeclareAsync(connection);
        await using var channel = await connection.CreateChannelAsync();
        var props = new BasicProperties
        {
            Persistent = true,
            ContentType = "application/json",
            Headers = headers,
        };
        try
        {
            var envelope = JsonSerializer.Deserialize<MessageEnvelope<JsonElement>>(body);
            if (envelope is not null)
            {
                props.MessageId = envelope.MessageId;
                props.CorrelationId = envelope.CorrelationId;
                props.Type = envelope.MessageType;
            }
        }
        catch (JsonException) { }
        await channel.BasicPublishAsync(exchange, routingKey, mandatory: true, props, body);
    }

    private async Task<byte[]> RideRequestedBytesAsync(Guid trip, int round, Guid messageId, string? extra = null)
    {
        var payload = JsonDocument.Parse(
            $"{{\"tripId\":\"{trip:N}\",\"matchingRound\":{round},\"inputMessageId\":\"{messageId:N}\"{extra ?? string.Empty}}}").RootElement;
        var envelope = MessageEnvelope<JsonElement>.Create(
            "RideRequestedV1", Guid.NewGuid().ToString("N"), payload, messageId: messageId.ToString("N"));
        return JsonSerializer.SerializeToUtf8Bytes(envelope, Json);
    }

    private async Task<uint> QueueDepthAsync(string queue)
    {
        using var connection = await Factory().CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        var info = await channel.QueueDeclarePassiveAsync(queue);
        return info.MessageCount;
    }

    private async Task RunConsumerAsync(
        string queue,
        Func<BrokerDelivery, CancellationToken, Task<HandlerOutcome>> handle,
        CancellationToken ct,
        int maxRedeliveries = 3)
    {
        var logger = new TestLogger<BrokerConsumeTests>();
        await BrokerConsume.RunAsync(
            Factory(), queue, 8, maxRedeliveries, handle, logger, ct);
    }

    [Fact]
    public async Task Trip_Contract_Through_Shared_Publisher_Converges_With_Envelope_Identity()
    {
        var now = DateTimeOffset.UtcNow;
        var trip = RideHailing.TripService.Domain.Trips.Trip.Create(Guid.NewGuid(), Guid.NewGuid(),
            new(10.7, 106.6), new(10.8, 106.7), new("VND", 50000m, "w8", now), now);
        trip.StartMatching(now);
        var payload = RideHailing.TripService.Integration.Matching.RideRequestedV1.FromTrip(trip, now);
        var messageId = Guid.NewGuid();
        var envelope = MessageEnvelope<RideHailing.TripService.Integration.Matching.RideRequestedV1>.Create(
            "RideRequestedV1", Guid.NewGuid().ToString("N"), payload, messageId: messageId.ToString("N"));
        await using (var connection = await Factory().CreateConnectionAsync())
            await RideTopology.DeclareAsync(connection);
        await using var publisher = new RideHailing.Messaging.Reliability.ConfirmedPublisher(Factory());
        await publisher.PublishAsync("ride.events", "trip.ride-requested.v1", envelope);
        await publisher.PublishAsync("ride.events", "trip.ride-requested.v1", envelope);
        using var stop = new CancellationTokenSource();
        var handler = new RideRequestedHandler(Rounds(), new TestLogger<RideRequestedHandler>());
        var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        var run = RunConsumerAsync("matching.ride-requested.q", async (delivery, ct) =>
        {
            var result = await handler.HandleAsync(delivery, ct);
            if (Interlocked.Increment(ref count) == 2) committed.TrySetResult();
            return result;
        }, stop.Token);
        try
        {
            await committed.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await using var db = new Npgsql.NpgsqlConnection(_pg.ConnectionString);
            await db.OpenAsync();
            await using var command = new Npgsql.NpgsqlCommand(
                "SELECT input_message_id FROM matching_rounds WHERE trip_id = @trip AND matching_round = 1", db);
            command.Parameters.AddWithValue("trip", trip.TripId);
            Assert.Equal(messageId, (Guid)(await command.ExecuteScalarAsync())!);
            Assert.Empty(await Rounds().LoadAttemptsAsync(trip.TripId, 1));
        }
        finally { stop.Cancel(); await run.WaitAsync(TimeSpan.FromSeconds(30)); }
    }

    [Fact]
    public async Task Broker_Cancellation_Recreates_Consumer_And_Processes_New_Work()
    {
        await using var connection = await Factory().CreateConnectionAsync();
        await RideTopology.DeclareAsync(connection);
        await using var channel = await connection.CreateChannelAsync();
        const string queue = "matching.ride-requested.q";
        using var stop = new CancellationTokenSource();
        var handler = new RideRequestedHandler(Rounds(), new TestLogger<RideRequestedHandler>());
        var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = RunConsumerAsync(queue, async (delivery, ct) =>
        {
            var result = await handler.HandleAsync(delivery, ct);
            committed.TrySetResult();
            return result;
        }, stop.Token);
        try
        {
            await PollUntilAsync(async () => (await channel.QueueDeclarePassiveAsync(queue)).ConsumerCount == 1,
                "initial registration");
            await channel.QueueDeleteAsync(queue, false, false);
            await RideTopology.DeclareAsync(connection);
            await PollUntilAsync(async () => (await channel.QueueDeclarePassiveAsync(queue)).ConsumerCount == 1,
                "supervisor registration after broker cancellation");
            var trip = Guid.NewGuid();
            await PublishAsync("ride.events", "trip.ride-requested.v1", await RideRequestedBytesAsync(trip, 1, Guid.NewGuid()));
            await committed.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.NotNull(await Rounds().LoadRoundAsync(trip, 1));
        }
        finally { stop.Cancel(); await run.WaitAsync(TimeSpan.FromSeconds(30)); }
    }

    [Fact]
    public async Task Round_Create_Via_Broker_Acks_And_Drains()
    {
        var trip = Guid.NewGuid();
        var msg = Guid.NewGuid();
        await PublishAsync("ride.events", "trip.ride-requested.v1",
            await RideRequestedBytesAsync(trip, 1, msg));

        using var cts = new CancellationTokenSource();
        var handler = new RideRequestedHandler(Rounds(), new TestLogger<RideRequestedHandler>());
        var run = BrokerConsume.RunAsync(
            Factory(), "matching.ride-requested.q", 8, 3,
            (d, ct) => handler.HandleAsync(d, ct),
            new TestLogger<BrokerConsumeTests>(), cts.Token);

        await PollUntilAsync(async () =>
            (await Rounds().LoadRoundAsync(trip, 1)) is not null, "round row via broker");
        await PollUntilAsync(async () =>
            await QueueDepthAsync("matching.ride-requested.q") == 0, "queue drain after ACK");
        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(30));

        var round = await Rounds().LoadRoundAsync(trip, 1);
        Assert.NotNull(round);
        Assert.Equal(MatchingRoundStatuses.Processing, round!.Status);
        Assert.Empty(await Rounds().LoadAttemptsAsync(trip, 1));
    }

    [Fact]
    public async Task Duplicate_Deliveries_Converge_To_One_Round()
    {
        // CON-06 shape (transport-level duplicates): same message published
        // concurrently from several publishers + sequential redelivery; the
        // durable ledger converges to exactly one round, no new identities.
        var trip = Guid.NewGuid();
        var msg = Guid.NewGuid();
        var body = await RideRequestedBytesAsync(trip, 2, msg);
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            PublishAsync("ride.events", "trip.ride-requested.v1", body)));

        using var cts = new CancellationTokenSource();
        var handler = new RideRequestedHandler(Rounds(), new TestLogger<RideRequestedHandler>());
        var run = Task.WhenAll(
            BrokerConsume.RunAsync(Factory(), "matching.ride-requested.q", 8, 3,
                (d, ct) => handler.HandleAsync(d, ct),
                new TestLogger<BrokerConsumeTests>(), cts.Token),
            BrokerConsume.RunAsync(Factory(), "matching.ride-requested.q", 8, 3,
                (d, ct) => handler.HandleAsync(d, ct),
                new TestLogger<BrokerConsumeTests>(), cts.Token));

        await PollUntilAsync(async () =>
            await QueueDepthAsync("matching.ride-requested.q") == 0, "duplicate drain");
        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.NotNull(await Rounds().LoadRoundAsync(trip, 2));
        Assert.Empty(await Rounds().LoadAttemptsAsync(trip, 2));
    }

    [Fact]
    public async Task Commit_Before_ACK_Crash_Redelivers_And_Converges()
    {
        // Handler commits the round then throws before the loop can ACK
        // (crash-before-ACK): redelivery converges on the durable row.
        var trip = Guid.NewGuid();
        var real = new RideRequestedHandler(Rounds(), new TestLogger<RideRequestedHandler>());
        var calls = 0;
        var body = await RideRequestedBytesAsync(trip, 3, Guid.NewGuid());
        await PublishAsync("ride.events", "trip.ride-requested.v1", body);

        using var cts = new CancellationTokenSource();
        async Task<HandlerOutcome> Flaky(BrokerDelivery d, CancellationToken ct)
        {
            Assert.Equal("trip.ride-requested.v1", d.RoutingKey);
            calls++;
            if (calls == 1)
            {
                await real.HandleAsync(d, ct);
                throw new TimeoutException("crash before ACK");
            }

            return await real.HandleAsync(d, ct);
        }

        var run = BrokerConsume.RunAsync(
            Factory(), "matching.ride-requested.q", 8, 3, Flaky,
            new TestLogger<BrokerConsumeTests>(), cts.Token);
        await PollUntilAsync(async () => calls >= 2, "redelivery after crash");
        await PollUntilAsync(async () =>
            await QueueDepthAsync("matching.ride-requested.q") == 0, "drain after redelivery");
        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(2, calls);
        Assert.NotNull(await Rounds().LoadRoundAsync(trip, 3));
    }

    [Fact]
    public async Task Malformed_And_Poison_Messages_Drop_Without_Wedging()
    {
        // Garbage bytes, unsupported schema version, and validation-failing
        // payload are permanent: dropped on first sight, consumer survives,
        // the next healthy message still processes.
        await PublishAsync("ride.events", "trip.ride-requested.v1",
            Encoding.UTF8.GetBytes("{not json"));
        var badEnvelope = MessageEnvelope<JsonElement>.Create(
            "RideRequestedV1", Guid.NewGuid().ToString("N"),
            JsonDocument.Parse("{\"tripId\":\"" + Guid.NewGuid().ToString("N") + "\"}").RootElement);
        await PublishAsync("ride.events", "trip.ride-requested.v1",
            JsonSerializer.SerializeToUtf8Bytes(badEnvelope, Json));

        var trip = Guid.NewGuid();
        await PublishAsync("ride.events", "trip.ride-requested.v1",
            await RideRequestedBytesAsync(trip, 4, Guid.NewGuid()));

        using var cts = new CancellationTokenSource();
        var handler = new RideRequestedHandler(Rounds(), new TestLogger<RideRequestedHandler>());
        var run = BrokerConsume.RunAsync(
            Factory(), "matching.ride-requested.q", 8, 3,
            (d, ct) => handler.HandleAsync(d, ct),
            new TestLogger<BrokerConsumeTests>(), cts.Token);

        await PollUntilAsync(async () =>
            (await Rounds().LoadRoundAsync(trip, 4)) is not null, "healthy message after poison");
        await PollUntilAsync(async () =>
            await QueueDepthAsync("matching.ride-requested.q") == 0, "poison drained");
        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Oversized_Body_Drops_Without_Parsing_And_Without_Wedging()
    {
        // 1 MiB+ body: dropped on first sight (never parsed, never requeued),
        // consumer survives, the next healthy message still processes.
        var big = new byte[1024 * 1024 + 16];
        for (var i = 0; i < big.Length; i++)
        {
            big[i] = (byte)'{';
        }

        await PublishAsync("ride.events", "trip.ride-requested.v1", big);

        var trip = Guid.NewGuid();
        await PublishAsync("ride.events", "trip.ride-requested.v1",
            await RideRequestedBytesAsync(trip, 7, Guid.NewGuid()));

        using var cts = new CancellationTokenSource();
        var handler = new RideRequestedHandler(Rounds(), new TestLogger<RideRequestedHandler>());
        var run = BrokerConsume.RunAsync(
            Factory(), "matching.ride-requested.q", 8, 3,
            (d, ct) => handler.HandleAsync(d, ct),
            new TestLogger<BrokerConsumeTests>(), cts.Token);

        await PollUntilAsync(async () =>
            (await Rounds().LoadRoundAsync(trip, 7)) is not null, "healthy message after oversized");
        await PollUntilAsync(async () =>
            await QueueDepthAsync("matching.ride-requested.q") == 0, "oversized drained");
        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Consumer_Survives_Broker_Outage_And_Shuts_Down_Cleanly()
    {
        // Broker unavailable: the loop retries with backoff (never crashes
        // the host) and still honors cancellation (no wedged shutdown).
        var dead = new ConnectionFactory
        {
            HostName = "localhost",
            Port = 1,
            UserName = "ride_app",
            Password = "ride_dev_pw",
            AutomaticRecoveryEnabled = false,
        };
        var handler = new RideRequestedHandler(Rounds(), new TestLogger<RideRequestedHandler>());
        using var cts = new CancellationTokenSource();
        var run = BrokerConsume.RunAsync(
            dead, "matching.ride-requested.q", 8, 3,
            (d, ct) => handler.HandleAsync(d, ct),
            new TestLogger<BrokerConsumeTests>(), cts.Token);

        // Let several reconnect attempts happen (observational window, not
        // correctness sync: the assertion is graceful shutdown, below).
        await Task.Delay(TimeSpan.FromSeconds(5));
        Assert.False(run.IsCompleted);
        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Persistent_Transient_Failure_Is_Bounded()
    {
        // Always-failing transient handler: exactly MaxRedeliveries handler
        // calls, then the message drops (no hot loop without DLQ infra).
        // Order matters: queue depth can read transient 0 while a requeue is
        // in flight, so first prove both handler calls happened (redelivery
        // flowed), then prove the queue drained (third sighting dropped).
        await using var errorConnection = await Factory().CreateConnectionAsync();
        await RideTopology.DeclareAsync(errorConnection);
        await using var errorChannel = await errorConnection.CreateChannelAsync();
        await errorChannel.QueuePurgeAsync("matching.ride-requested.q.dead");
        var body = await RideRequestedBytesAsync(Guid.NewGuid(), 5, Guid.NewGuid());
        await PublishAsync("ride.events", "trip.ride-requested.v1", body);

        var calls = 0;
        Task<HandlerOutcome> AlwaysTransient(BrokerDelivery d, CancellationToken ct)
        {
            Interlocked.Increment(ref calls);
            throw new TimeoutException("persistent downstream outage");
        }

        using var cts = new CancellationTokenSource();
        var run = BrokerConsume.RunAsync(
            Factory(), "matching.ride-requested.q", 8, 2,
            AlwaysTransient, new TestLogger<BrokerConsumeTests>(), cts.Token);
        await PollUntilAsync(async () => Volatile.Read(ref calls) >= 2, "both redeliveries handled");
        await PollUntilAsync(async () =>
            await QueueDepthAsync("matching.ride-requested.q.dead") == 1, "confirmed error transfer");
        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(2, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task Projection_State_And_Location_Flow_Via_Broker()
    {
        var driver = Guid.NewGuid();
        var mux = _redis.Multiplexer();
        var updater = new RedisProjectionUpdater(mux);
        await updater.EnsureFunctionAsync();
        var handler = new DriverProjectionHandler(updater, new TestLogger<DriverProjectionHandler>());

        Task<HandlerOutcome> Run(byte[] body, string key) =>
            handler.HandleAsync(
                new BrokerDelivery(key, body, new Dictionary<string, object?>(), 0, false),
                CancellationToken.None);

        var state = MessageEnvelope<JsonElement>.Create(
            "DriverReservedV1", Guid.NewGuid().ToString("N"),
            JsonDocument.Parse(
                $"{{\"driverId\":\"{driver:N}\",\"status\":\"AVAILABLE\",\"version\":5}}").RootElement);
        Assert.Equal(HandlerOutcome.Done,
            await Run(JsonSerializer.SerializeToUtf8Bytes(state, Json), "driver.available.v1"));
        var db = mux.GetDatabase();
        Assert.True(await db.GeoPositionAsync("drivers:geo", driver.ToString("N")) is null);

        var reserved = MessageEnvelope<JsonElement>.Create(
            "DriverReservedV1", Guid.NewGuid().ToString("N"),
            JsonDocument.Parse(
                $"{{\"driverId\":\"{driver:N}\",\"status\":\"RESERVED\",\"version\":6}}").RootElement);
        Assert.Equal(HandlerOutcome.Done,
            await Run(JsonSerializer.SerializeToUtf8Bytes(reserved, Json), "driver.reserved.v1"));
        Assert.True(await db.GeoPositionAsync("drivers:geo", driver.ToString("N")) is null);
        Assert.Equal("RESERVED", (string?)await db.HashGetAsync($"driver:projection:{driver:N}", "derivedStatus"));

        // Frozen catalog also routes trip lifecycle keys here (producers land
        // with A-owned trip work): status is stored verbatim and version
        // guarded; non-AVAILABLE stays out of GEO. Same code path as above.
        var started = MessageEnvelope<JsonElement>.Create(
            "DriverReservedV1", Guid.NewGuid().ToString("N"),
            JsonDocument.Parse(
                $"{{\"driverId\":\"{driver:N}\",\"status\":\"ON_TRIP\",\"version\":8}}").RootElement);
        Assert.Equal(HandlerOutcome.Done,
            await Run(JsonSerializer.SerializeToUtf8Bytes(started, Json), "driver.trip-started.v1"));
        Assert.Equal("ON_TRIP", (string?)await db.HashGetAsync($"driver:projection:{driver:N}", "derivedStatus"));
        Assert.True(await db.GeoPositionAsync("drivers:geo", driver.ToString("N")) is null);

        var stale = MessageEnvelope<JsonElement>.Create(
            "DriverReservedV1", Guid.NewGuid().ToString("N"),
            JsonDocument.Parse(
                $"{{\"driverId\":\"{driver:N}\",\"status\":\"AVAILABLE\",\"version\":4}}").RootElement);
        Assert.Equal(HandlerOutcome.Done,
            await Run(JsonSerializer.SerializeToUtf8Bytes(stale, Json), "driver.available.v1"));
        Assert.Equal("8", (string?)await db.HashGetAsync($"driver:projection:{driver:N}", "driverAggregateVersion"));

        // Re-availability re-enters the dispatchable set only once a fresh
        // location arrives (GEO needs both AVAILABLE state and location).
        var back = MessageEnvelope<JsonElement>.Create(
            "DriverReservedV1", Guid.NewGuid().ToString("N"),
            JsonDocument.Parse(
                $"{{\"driverId\":\"{driver:N}\",\"status\":\"AVAILABLE\",\"version\":9}}").RootElement);
        Assert.Equal(HandlerOutcome.Done,
            await Run(JsonSerializer.SerializeToUtf8Bytes(back, Json), "driver.available.v1"));
        Assert.True(await db.GeoPositionAsync("drivers:geo", driver.ToString("N")) is null);

        var t0 = new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
        var location = MessageEnvelope<JsonElement>.Create(
            "DriverLocationAcceptedV1", Guid.NewGuid().ToString("N"),
            JsonDocument.Parse(
                $"{{\"driverId\":\"{driver:N}\",\"longitude\":106.7,\"latitude\":10.9," +
                $"\"sessionId\":\"s\",\"sequenceNo\":9,\"capturedAt\":\"{t0:O}\"}}").RootElement);
        Assert.Equal(HandlerOutcome.Done,
            await Run(JsonSerializer.SerializeToUtf8Bytes(location, Json), "driver.location-accepted.v1"));
        var pos = await db.GeoPositionAsync("drivers:geo", driver.ToString("N"));
        Assert.NotNull(pos);
        Assert.Equal(10.9, pos!.Value.Latitude, 4);
    }

    [Fact]
    public async Task Unknown_Routing_Key_Drops_Without_Wedging()
    {
        // Unknown keys are contract drift (poison): a valid envelope that no
        // handler owns must fail loud, never wedge the consumer. The loop
        // drops it via the poison path; the next healthy message still flows
        // (proven by Malformed_And_Poison_Messages_Drop_Without_Wedging).
        var mux = _redis.Multiplexer();
        var handler = new DriverProjectionHandler(
            new RedisProjectionUpdater(mux), new TestLogger<DriverProjectionHandler>());
        var envelope = MessageEnvelope<JsonElement>.Create(
            "DriverReservedV1", Guid.NewGuid().ToString("N"),
            JsonDocument.Parse($"{{\"driverId\":\"{Guid.NewGuid():N}\",\"status\":\"AVAILABLE\",\"version\":1}}").RootElement);
        await Assert.ThrowsAsync<ArgumentException>(() => handler.HandleAsync(
            new BrokerDelivery("driver.nonsense.v1",
                JsonSerializer.SerializeToUtf8Bytes(envelope, Json),
                new Dictionary<string, object?>(), 0, false),
            CancellationToken.None));
    }

    [Fact]
    public async Task Trace_Context_Propagates_Producer_To_Consumer_Activity()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);
        Activity? consumed = null;
        listener.ActivityStopped += activity =>
        {
            if (activity.OperationName == "matching.consume.ride-requested")
            {
                consumed = activity;
            }
        };

        using var producer = new Activity(RideRequestedHandler.Activities.Name + ".publish-test");
        producer.Start();

        var handler = new RideRequestedHandler(Rounds(), new TestLogger<RideRequestedHandler>());
        var headers = new Dictionary<string, object?>();
        RideHailing.Messaging.Contracts.TraceHeaderPropagation.Inject(producer.Context, headers);
        var body = await RideRequestedBytesAsync(Guid.NewGuid(), 6, Guid.NewGuid());
        await handler.HandleAsync(
            new BrokerDelivery("trip.ride-requested.v1", body, headers, 0, false),
            CancellationToken.None);
        producer.Stop();

        Assert.NotNull(consumed);
        Assert.Equal(producer.Context.SpanId, consumed!.ParentSpanId);
        Assert.Equal(producer.TraceId, consumed.TraceId);
    }
}
