// B-W12-06 / SEC-05/07: the REAL Driver location ingress (Kestrel + minimal
// API binding + LocationValidator + in-memory monotonic acceptor) and the REAL
// broker publication path. Invalid input must not change the accepted sample,
// must not publish DriverLocationAcceptedV1, and must not corrupt the derived
// Redis projection that was built from the last accepted event.
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using RabbitMQ.Client;
using RideHailing.DriverService.Api;
using RideHailing.DriverService.Application.Location;
using RideHailing.DriverService.Application.Messaging;
using RideHailing.DriverService.Infrastructure.Faults;
using RideHailing.DriverService.Infrastructure.Persistence;
using RideHailing.MatchingService.Application.Messaging;
using RideHailing.MatchingService.Application.Projection;
using RideHailing.Messaging.Contracts;
using RideHailing.Messaging.Outbox;
using RideHailing.Messaging.Topology;
using StackExchange.Redis;
using Xunit.Abstractions;

namespace RideHailing.MatchingService.IntegrationTests;

[Collection(W12IngressCollection.Name)]
public sealed class DriverLocationIngressTests
{
    private const string Session = "w12-location-session";
    private const string Route = "driver.location-accepted.v1";

    private readonly DriverTestDatabase _drivers;
    private readonly RedisDatabase _redis;
    private readonly RabbitMqDatabase _rabbit;
    private readonly ITestOutputHelper _output;

    public DriverLocationIngressTests(
        DriverTestDatabase drivers, RedisDatabase redis, RabbitMqDatabase rabbit,
        ITestOutputHelper output)
    {
        _drivers = drivers;
        _redis = redis;
        _rabbit = rabbit;
        _output = output;
    }

    [Fact]
    public async Task Invalid_Location_Data_Is_Rejected_Without_Mutation_Publication_Or_Projection_Corruption()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var ct = deadline.Token;
        var driverId = Guid.NewGuid();
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-2);
        var queue = "w12-location-" + Guid.NewGuid().ToString("N");
        var db = _redis.Multiplexer().GetDatabase();
        var updater = new RedisProjectionUpdater(_redis.Multiplexer());
        await updater.EnsureFunctionAsync();
        var projection = new DriverProjectionHandler(
            updater, Microsoft.Extensions.Logging.Abstractions.NullLogger<DriverProjectionHandler>.Instance);

        await DeclareQueueAsync(queue, ct);
        await CleanupRedisAsync(db, driverId);

        await using var host = BuildHost();
        await host.StartAsync(ct);
        using var http = Client(host);
        var acceptor = host.Services.GetRequiredService<LocationAcceptor>();

        try
        {
            // Accepted baseline: seq 10.
            var valid = await http.PostAsJsonAsync($"drivers/{driverId:N}/location", new
            {
                deviceSessionId = Session,
                sequenceNo = 10,
                capturedAt = t0,
                latitude = 10.8,
                longitude = 106.6,
            }, ct);
            Assert.Equal(HttpStatusCode.Accepted, valid.StatusCode);
            var latest = acceptor.GetLatest(driverId, Session);
            Assert.NotNull(latest);
            Assert.Equal(10, latest!.SequenceNo);

            var published = await ConsumeOneAsync(queue, ct);
            Assert.NotNull(published);
            Assert.Equal(Route, published!.Value.Route);
            var acceptedEnvelope = BrokerEnvelope.Parse(published.Value.Body);
            Assert.Equal(driverId, acceptedEnvelope.Payload.GetProperty("driverId").GetGuid());

            // Build the real projection from a DriverAvailable state event and
            // the published location event: seq 10 is authoritative and the
            // driver is GEO-indexable.
            var stateEnvelope = MessageEnvelope<JsonElement>.Create(
                "DriverAvailableV1", "w12-location",
                JsonSerializer.SerializeToElement(new
                {
                    driverId,
                    status = "AVAILABLE",
                    version = 1,
                }));
            Assert.Equal(HandlerOutcome.Done, await DeliverAsync(
                projection, "driver.available.v1", stateEnvelope));
            Assert.Equal(HandlerOutcome.Done, await DeliverAsync(
                projection, Route, acceptedEnvelope));
            Assert.Equal("10", await db.HashGetAsync(
                RedisProjectionKeys.ProjectionKey(driverId), "sequenceNo"));
            var geoBefore = await db.GeoPositionAsync(
                RedisProjectionKeys.GeoKey, RedisProjectionKeys.Member(driverId));
            Assert.NotNull(geoBefore);

            // Coordinate bounds.
            await AssertRejectedAsync(http, driverId, t0, HttpStatusCode.BadRequest, "INVALID_COORDINATE",
                latitude: 91);
            await AssertRejectedAsync(http, driverId, t0, HttpStatusCode.BadRequest, "INVALID_COORDINATE",
                latitude: -91);
            await AssertRejectedAsync(http, driverId, t0, HttpStatusCode.BadRequest, "INVALID_COORDINATE",
                longitude: 181);
            await AssertRejectedAsync(http, driverId, t0, HttpStatusCode.BadRequest, "INVALID_COORDINATE",
                longitude: -181);

            // Non-finite values: JSON has no NaN/Infinity literal, and an
            // overflow literal (1e400) is rejected by binding or validation.
            using (var overflow = await http.PostAsync(
                $"drivers/{driverId:N}/location",
                Json("""{"deviceSessionId":"w12-location-session","sequenceNo":11,"capturedAt":"2026-10-26T12:00:00Z","latitude":1e400,"longitude":106.6}"""),
                ct))
            {
                Assert.Equal(HttpStatusCode.BadRequest, overflow.StatusCode);
            }

            using (var nan = await http.PostAsync(
                $"drivers/{driverId:N}/location",
                Json("""{"deviceSessionId":"w12-location-session","sequenceNo":11,"capturedAt":"2026-10-26T12:00:00Z","latitude":"NaN","longitude":106.6}"""),
                ct))
            {
                Assert.Equal(HttpStatusCode.BadRequest, nan.StatusCode);
            }

            // Malformed route identity / body identity / session / sequence.
            using (var malformedId = await http.PostAsJsonAsync(
                "drivers/not-a-uuid/location", LocationBody(t0), ct))
            {
                Assert.Equal(HttpStatusCode.NotFound, malformedId.StatusCode);
            }

            using (var missingSession = await http.PostAsync(
                $"drivers/{driverId:N}/location",
                Json("""{"sequenceNo":11,"capturedAt":"2026-10-26T12:00:00Z","latitude":10.8,"longitude":106.6}"""),
                ct))
            {
                Assert.Equal(HttpStatusCode.BadRequest, missingSession.StatusCode);
            }

            await AssertRejectedAsync(http, driverId, t0, HttpStatusCode.BadRequest, "INVALID_LOCATION",
                deviceSessionId: "");
            await AssertRejectedAsync(http, driverId, t0, HttpStatusCode.BadRequest, "INVALID_LOCATION",
                sequenceNo: -1);
            await AssertRejectedAsync(http, driverId, t0, HttpStatusCode.BadRequest, "INVALID_LOCATION",
                deviceSessionId: null);
            await AssertRejectedAsync(http, driverId, t0, HttpStatusCode.BadRequest, "INVALID_LOCATION",
                capturedAt: default(DateTimeOffset));
            await AssertRejectedAsync(http, driverId, t0, HttpStatusCode.BadRequest, "INVALID_LOCATION",
                capturedAt: DateTimeOffset.UtcNow.AddHours(1));
            await AssertRejectedAsync(http, driverId, t0, HttpStatusCode.BadRequest, "INVALID_LOCATION",
                capturedAt: DateTimeOffset.UtcNow.AddHours(-25));

            // Stale sample: 409, never 400, and no accepted mutation/event.
            var staleA = await http.PostAsJsonAsync($"drivers/{driverId:N}/location",
                LocationBody(t0.AddSeconds(1), sequenceNo: 9), ct);
            Assert.Equal(HttpStatusCode.Conflict, staleA.StatusCode);
            Assert.Equal("STALE_LOCATION", await CodeAsync(staleA));
            var staleB = await http.PostAsJsonAsync($"drivers/{driverId:N}/location",
                LocationBody(t0.AddSeconds(-1), sequenceNo: 10), ct);
            Assert.Equal(HttpStatusCode.Conflict, staleB.StatusCode);

            // Malformed JSON and oversized body.
            using (var malformed = await http.PostAsync(
                $"drivers/{driverId:N}/location", Json("""{"deviceSessionId":"""), ct))
            {
                Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
            }

            using (var oversized = await http.PostAsync(
                $"drivers/{driverId:N}/location",
                Json(JsonSerializer.Serialize(new
                {
                    deviceSessionId = new string('x', 70 * 1024),
                    sequenceNo = 12,
                    capturedAt = t0,
                    latitude = 10.8,
                    longitude = 106.6,
                })),
                ct))
            {
                Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
            }

            // No invalid request mutated the accepted sample, published a new
            // event, or changed the projected sample/GEO membership.
            var afterInvalid = acceptor.GetLatest(driverId, Session)!;
            Assert.Equal(10, afterInvalid.SequenceNo);
            Assert.Equal(10.8, afterInvalid.Latitude);
            Assert.Equal(106.6, afterInvalid.Longitude);
            Assert.Null(await TryConsumeAsync(queue, TimeSpan.FromSeconds(1), ct));
            Assert.Equal("10", await db.HashGetAsync(
                RedisProjectionKeys.ProjectionKey(driverId), "sequenceNo"));
            Assert.Equal(10.8, (double)await db.HashGetAsync(
                RedisProjectionKeys.ProjectionKey(driverId), "latitude"));
            var geoAfter = await db.GeoPositionAsync(
                RedisProjectionKeys.GeoKey, RedisProjectionKeys.Member(driverId));
            Assert.Equal(geoBefore!.Value.Longitude, geoAfter!.Value.Longitude, 6);
            Assert.Equal(geoBefore.Value.Latitude, geoAfter.Value.Latitude, 6);

            // W3/W4 preservation: latitude 86 is a VALID domain coordinate but
            // outside Redis GEO indexability; it must be accepted, stored in
            // the hash, and removed from GEO, not rejected as invalid data.
            using var polar = await http.PostAsJsonAsync($"drivers/{driverId:N}/location", new
            {
                deviceSessionId = Session,
                sequenceNo = 11,
                capturedAt = t0.AddSeconds(2),
                latitude = 86.0,
                longitude = 106.6,
            }, ct);
            Assert.Equal(HttpStatusCode.Accepted, polar.StatusCode);
            var polarMessage = await ConsumeOneAsync(queue, ct);
            Assert.NotNull(polarMessage);
            var polarEnvelope = BrokerEnvelope.Parse(polarMessage!.Value.Body);
            Assert.Equal(HandlerOutcome.Done, await DeliverAsync(projection, Route, polarEnvelope));
            Assert.Equal("11", await db.HashGetAsync(
                RedisProjectionKeys.ProjectionKey(driverId), "sequenceNo"));
            Assert.Equal(86.0, (double)await db.HashGetAsync(
                RedisProjectionKeys.ProjectionKey(driverId), "latitude"));
            Assert.Null(await db.GeoPositionAsync(
                RedisProjectionKeys.GeoKey, RedisProjectionKeys.Member(driverId)));

            _output.WriteLine(
                $"driver={driverId} acceptedSeq=10 invalidCases=18 " +
                "noPublication=true projectionSeq=10 geoStable=true " +
                "polarLatitude=86 accepted=true geoMemberRemoved=true");

            await CleanupRedisAsync(db, driverId, stateEnvelope, acceptedEnvelope, polarEnvelope);
        }
        finally
        {
            await CleanupRedisAsync(db, driverId);
            await host.StopAsync(CancellationToken.None);
            await DeleteQueueAsync(queue);
        }
    }

    private async Task AssertRejectedAsync(
        HttpClient http, Guid driverId, DateTimeOffset t0,
        HttpStatusCode expectedStatus, string? expectedCode,
        double latitude = 10.8, double longitude = 106.6,
        string? deviceSessionId = Session, long sequenceNo = 11,
        DateTimeOffset? capturedAt = null)
    {
        var body = LocationBody(
            capturedAt ?? t0, sequenceNo, deviceSessionId, latitude, longitude);
        using var response = await http.PostAsJsonAsync(
            $"drivers/{driverId:N}/location", body);
        Assert.Equal(expectedStatus, response.StatusCode);
        if (expectedCode is not null)
        {
            Assert.Equal(expectedCode, await CodeAsync(response));
        }
    }

    private static object LocationBody(
        DateTimeOffset capturedAt,
        long sequenceNo = 10,
        string? deviceSessionId = Session,
        double latitude = 10.8,
        double longitude = 106.6) => new
    {
        deviceSessionId,
        sequenceNo,
        capturedAt,
        latitude,
        longitude,
    };

    private static async Task<string?> CodeAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    private static StringContent Json(string body) =>
        new(body, Encoding.UTF8, "application/json");

    private static HttpClient Client(WebApplication host)
    {
        var address = host.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        return new HttpClient { BaseAddress = new Uri(address + "/") };
    }

    private WebApplication BuildHost()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        DriverIngressLimits.Apply(builder);

        var outbox = new NpgsqlOutboxStore(_drivers.DataSource);
        builder.Services.AddSingleton(_drivers.DataSource);
        builder.Services.AddSingleton(outbox);
        builder.Services.AddSingleton<IOutboxStore>(outbox);
        builder.Services.AddSingleton(new DriverRepository(_drivers.DataSource, outbox));
        builder.Services.AddSingleton(DriverFaultPoint.Disabled());
        builder.Services.AddOptions<LocationIngressOptions>();
        builder.Services.AddSingleton<IOptions<DriverBrokerOptions>>(
            Options.Create(BrokerOptions()));
        builder.Services.AddSingleton<LocationAcceptor>();
        builder.Services.AddSingleton<DriverEventPublisher>();

        var app = builder.Build();
        DriverEndpoints.Map(app);
        return app;
    }

    private DriverBrokerOptions BrokerOptions()
    {
        var uri = new Uri(_rabbit.ConnectionString);
        var userInfo = uri.UserInfo.Split(':', 2);
        return new DriverBrokerOptions
        {
            Host = uri.Host,
            Port = uri.Port == -1 ? 5672 : uri.Port,
            Username = userInfo.Length > 0 && userInfo[0].Length > 0
                ? Uri.UnescapeDataString(userInfo[0]) : "guest",
            Password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : "guest",
            PublishingEnabled = true,
            CommandsEnabled = false,
        };
    }

    private ConnectionFactory Factory(string name)
    {
        var uri = new Uri(_rabbit.ConnectionString);
        var userInfo = uri.UserInfo.Split(':', 2);
        return new ConnectionFactory
        {
            HostName = uri.Host,
            Port = uri.Port == -1 ? 5672 : uri.Port,
            UserName = userInfo.Length > 0 && userInfo[0].Length > 0
                ? Uri.UnescapeDataString(userInfo[0]) : "guest",
            Password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : "guest",
            ClientProvidedName = name,
        };
    }

    private async Task DeclareQueueAsync(string queue, CancellationToken ct)
    {
        await using var connection = await Factory("w12-location-declare").CreateConnectionAsync(ct);
        await RideTopology.DeclareAsync(connection, ct);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: ct);
        await channel.QueueDeclareAsync(queue, durable: true, exclusive: false,
            autoDelete: false, cancellationToken: ct);
        await channel.QueueBindAsync(queue, "ride.events", Route, cancellationToken: ct);
    }

    private async Task DeleteQueueAsync(string queue)
    {
        try
        {
            await using var connection = await Factory("w12-location-delete").CreateConnectionAsync();
            await using var channel = await connection.CreateChannelAsync();
            await channel.QueueDeleteAsync(queue);
        }
        catch
        {
            // container disposal below is authoritative
        }
    }

    private async Task<(string Route, byte[] Body)?> ConsumeOneAsync(
        string queue, CancellationToken ct) =>
        await TryConsumeAsync(queue, TimeSpan.FromSeconds(20), ct);

    private async Task<(string Route, byte[] Body)?> TryConsumeAsync(
        string queue, TimeSpan timeout, CancellationToken ct)
    {
        await using var connection = await Factory("w12-location-consume").CreateConnectionAsync(ct);
        await RideTopology.DeclareAsync(connection, ct);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: ct);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        wait.CancelAfter(timeout);
        try
        {
            while (!wait.IsCancellationRequested)
            {
                var result = await channel.BasicGetAsync(queue, autoAck: true, wait.Token);
                if (result is not null)
                {
                    return (result.RoutingKey, result.Body.ToArray());
                }

                await Task.Delay(50, wait.Token);
            }
        }
        catch (OperationCanceledException) when (wait.IsCancellationRequested)
        {
        }

        return null;
    }

    private static Task<HandlerOutcome> DeliverAsync(
        DriverProjectionHandler handler,
        string route,
        MessageEnvelope<JsonElement> envelope) =>
        handler.HandleAsync(new BrokerDelivery(
            route,
            JsonSerializer.SerializeToUtf8Bytes(envelope),
            new Dictionary<string, object?>(),
            0,
            false), default);

    private Task CleanupRedisAsync(IDatabase db, Guid driverId, params MessageEnvelope<JsonElement>[] envelopes)
    {
        foreach (var envelope in envelopes)
        {
            db.KeyDelete(ProjectionReceipt.ForEnvelope(envelope).Key);
        }

        db.KeyDelete(RedisProjectionKeys.ProjectionKey(driverId));
        db.SortedSetRemove(RedisProjectionKeys.GeoKey, RedisProjectionKeys.Member(driverId));
        return Task.CompletedTask;
    }
}
