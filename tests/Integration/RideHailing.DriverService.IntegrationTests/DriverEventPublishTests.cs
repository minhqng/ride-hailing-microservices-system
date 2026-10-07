// B-W08-05: Driver event publication proof on REAL RabbitMQ + REAL
// driver_db. Publishes go through the real publisher with confirms; a raw
// test consumer reads the wire form and asserts envelope contract, stable
// identity, trace propagation, and routing.
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RideHailing.DriverService.Application.Messaging;
using RideHailing.DriverService.Domain.Drivers;
using RideHailing.DriverService.Infrastructure.Persistence;
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

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private ConnectionFactory Factory() => new()
    {
        Uri = new Uri(_rabbit.ConnectionString),
        ClientProvidedName = "w8-test",
        AutomaticRecoveryEnabled = false,
    };

    private DriverEventPublisher Publisher()
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
                Username = userInfo.Length > 0 && userInfo[0].Length > 0 ? Uri.UnescapeDataString(userInfo[0]) : "guest",
                Password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : "guest",
            }),
            new TestLogger<DriverEventPublisher>());
    }

    private static readonly string[] DriverTestKeys =
    [
        "driver.available.v1", "driver.offline.v1", "driver.reserved.v1",
        "driver.reservation-released.v1", "driver.location-accepted.v1",
    ];

    private ConnectionFactory ConsumeFactory() => new()
    {
        Uri = new Uri(_rabbit.ConnectionString),
        ClientProvidedName = "w8-test-consume",
        AutomaticRecoveryEnabled = false,
    };

    private async Task DeclareTestQueueAsync(string queue)
    {
        // Bind BEFORE any publish: a topic exchange drops messages with no
        // matching binding at publish time, so the queue must exist first.
        // RabbitMQ 4.x forbids transient non-exclusive queues (durable=false,
        // exclusive=false), so test queues are durable classic (container is
        // disposed after the run; names are unique per test).
        using var connection = await ConsumeFactory().CreateConnectionAsync();
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
        // Test-exclusive auto-delete queue bound to all Driver events.
        // Queue/bind are idempotent; callers must DeclareTestQueueAsync
        // BEFORE publishing so the binding exists at publish time. Polls
        // because broker routing is async (single BasicGet would flake).
        using var connection = await ConsumeFactory().CreateConnectionAsync();
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
    public async Task Reserve_Win_Publishes_Reserved_With_Stable_Identity()
    {
        var repo = new DriverRepository(_db.ConnectionString);
        var driver = Driver.Create(Guid.NewGuid(), Guid.NewGuid());
        driver.GoOnline(driver.Version);
        Assert.True(await repo.TryInsertAsync(driver));
        var start = (await repo.LoadAsync(driver.DriverId))!;
        var trip = Guid.NewGuid();
        var reservation = Guid.NewGuid();

        var won = await repo.TryReserveAsync(driver.DriverId, trip, reservation, start.Version);
        Assert.Equal(ReserveOutcome.Won, won.Outcome);
        var mapped = DriverEventMapper.ForReserveResult(
            driver.DriverId, trip, reservation, won);
        Assert.NotNull(mapped);
        var queue = "w8-test-reserve-" + Guid.NewGuid().ToString("N");
        await DeclareTestQueueAsync(queue);
        await using var pub = Publisher();
        Assert.True(await pub.PublishStateAsync(
            mapped!.MessageType, mapped.RoutingKey, mapped.Payload,
            mapped.Version, driver.DriverId));

        var (routing, envelope, _) = await ConsumeOneAsync(queue);
        Assert.Equal("driver.reserved.v1", routing);
        Assert.Equal("DriverReservedV1", Prop(envelope, "messageType"));
        Assert.Equal(1, envelope.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(driver.DriverId, Guid.Parse(Prop(envelope, "payload", "driverId")));
        Assert.Equal("RESERVED", Prop(envelope, "payload", "status"));
        Assert.Equal(trip, Guid.Parse(Prop(envelope, "payload", "tripId")));
        Assert.Equal(reservation, Guid.Parse(Prop(envelope, "payload", "reservationId")));
        Assert.Equal("driver", Prop(envelope, "aggregateType"));
        Assert.Equal(driver.DriverId.ToString("N"), Prop(envelope, "aggregateId"));
        Assert.False(string.IsNullOrWhiteSpace(Prop(envelope, "correlationId")));
        var firstId = Prop(envelope, "messageId");

        // Same-effect replay republishes the SAME identity (idempotent producer).
        var replayed = await repo.TryReserveAsync(driver.DriverId, trip, reservation, start.Version + 1);
        Assert.Equal(ReserveOutcome.Replayed, replayed.Outcome);
        var mappedReplay = DriverEventMapper.ForReserveResult(
            driver.DriverId, trip, reservation, replayed);
        Assert.True(await pub.PublishStateAsync(
            mappedReplay!.MessageType, mappedReplay.RoutingKey, mappedReplay.Payload,
            mappedReplay.Version, driver.DriverId));
        var second = await ConsumeOneAsync(queue);
        Assert.Equal(firstId, Prop(second.Envelope, "messageId"));
    }

    [Fact]
    public async Task Release_Offline_Online_Publish_Canonical_Routes()
    {
        var repo = new DriverRepository(_db.ConnectionString);
        var driver = Driver.Create(Guid.NewGuid(), Guid.NewGuid());
        driver.GoOnline(driver.Version);
        Assert.True(await repo.TryInsertAsync(driver));
        var start = (await repo.LoadAsync(driver.DriverId))!;
        var trip = Guid.NewGuid();
        var reservation = Guid.NewGuid();
        Assert.Equal(ReserveOutcome.Won,
            (await repo.TryReserveAsync(driver.DriverId, trip, reservation, start.Version)).Outcome);

        var queue = "w8-test-routes-" + Guid.NewGuid().ToString("N");
        await DeclareTestQueueAsync(queue);
        await using var publisher = Publisher();

        var released = await repo.TryReleaseAsync(driver.DriverId, trip, reservation);
        var mappedRelease = DriverEventMapper.ForReleaseResult(driver.DriverId, trip, reservation, released);
        Assert.True(await publisher.PublishStateAsync(
            mappedRelease!.MessageType, mappedRelease.RoutingKey, mappedRelease.Payload,
            mappedRelease.Version, driver.DriverId));
        var (releasedRoute, releasedEnv, _) = await ConsumeOneAsync(queue);
        Assert.Equal("driver.reservation-released.v1", releasedRoute);
        Assert.Equal("DriverReservationReleasedV1", Prop(releasedEnv, "messageType"));

        var current = (await repo.LoadAsync(driver.DriverId))!;
        var offline = await repo.TryGoOfflineAsync(driver.DriverId, current.Version);
        var mappedOffline = DriverEventMapper.ForOfflineResult(driver.DriverId, offline);
        Assert.True(await publisher.PublishStateAsync(
            mappedOffline!.MessageType, mappedOffline.RoutingKey, mappedOffline.Payload,
            mappedOffline.Version, driver.DriverId));
        var (offlineRoute, offlineEnv, _) = await ConsumeOneAsync(queue);
        Assert.Equal("driver.offline.v1", offlineRoute);
        Assert.Equal("DriverOfflineV1", Prop(offlineEnv, "messageType"));

        current = (await repo.LoadAsync(driver.DriverId))!;
        var online = await repo.TryGoOnlineAsync(driver.DriverId, current.Version);
        var mappedOnline = DriverEventMapper.ForOnlineResult(driver.DriverId, online);
        Assert.True(await publisher.PublishStateAsync(
            mappedOnline!.MessageType, mappedOnline.RoutingKey, mappedOnline.Payload,
            mappedOnline.Version, driver.DriverId));
        var (onlineRoute, onlineEnv, _) = await ConsumeOneAsync(queue);
        Assert.Equal("driver.available.v1", onlineRoute);
        Assert.Equal("DriverAvailableV1", Prop(onlineEnv, "messageType"));
    }

    [Fact]
    public async Task Location_Accept_Publishes_Sample_Fact()
    {
        var payload = new DriverLocationEventPayload(
            Guid.NewGuid(), 106.7, 10.9, "sess", 12,
            new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
        var queue = "w8-test-loc-" + Guid.NewGuid().ToString("N");
        await DeclareTestQueueAsync(queue);
        await using var locPub = Publisher();
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

        using var producer = new Activity("w8-test-publish");
        producer.Start();
        var payload = new DriverLocationEventPayload(
            Guid.NewGuid(), 106.7, 10.9, "sess", 1, DateTimeOffset.UtcNow);
        var queue = "w8-test-trace-" + Guid.NewGuid().ToString("N");
        await DeclareTestQueueAsync(queue);
        await using var tracePub = Publisher();
        Assert.True(await tracePub.PublishLocationAsync(payload));
        producer.Stop();

        var (_, _, headers) = await ConsumeOneAsync(queue);
        Assert.True(RideHailing.Messaging.Contracts.TraceHeaderPropagation.TryExtract(headers, out var ctx));
        Assert.Equal(producer.TraceId, ctx.TraceId);
    }
}
