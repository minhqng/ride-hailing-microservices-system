// Staleness through the REAL DriverProjectionHandler
// and REAL Redis projection function, not a pure comparator. Each test uses a
// unique Driver id and cleans its hash, GEO member and receipt keys.
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RideHailing.MatchingService.Application.Messaging;
using RideHailing.MatchingService.Application.Projection;
using RideHailing.Messaging.Contracts;
using StackExchange.Redis;
using Xunit.Abstractions;

namespace RideHailing.MatchingService.IntegrationTests;

[Collection(RedisCollection.Name)]
public sealed class DriverProjectionStalenessHandlerTests
{
    private readonly RedisDatabase _redis;
    private readonly ITestOutputHelper _output;

    public DriverProjectionStalenessHandlerTests(RedisDatabase redis, ITestOutputHelper output)
    {
        _redis = redis;
        _output = output;
    }

    private static MessageEnvelope<JsonElement> Envelope(string type, object payload) =>
        MessageEnvelope<JsonElement>.Create(
            type, "REL12/13", JsonSerializer.SerializeToElement(payload));

    private static Task<HandlerOutcome> DeliverAsync(
        DriverProjectionHandler handler, string route, MessageEnvelope<JsonElement> envelope) =>
        handler.HandleAsync(new BrokerDelivery(
            route,
            JsonSerializer.SerializeToUtf8Bytes(envelope),
            new Dictionary<string, object?>(),
            0,
            false), default);

    private IDatabase Db() => _redis.Multiplexer().GetDatabase();

    private async Task<string?> HashAsync(Guid driver, string field) =>
        (string?)await Db().HashGetAsync(RedisProjectionKeys.ProjectionKey(driver), field);

    private async Task<GeoPosition?> GeoAsync(Guid driver) =>
        await Db().GeoPositionAsync(RedisProjectionKeys.GeoKey, RedisProjectionKeys.Member(driver));

    private async Task CleanupAsync(Guid driver, IEnumerable<MessageEnvelope<JsonElement>> envelopes)
    {
        var db = Db();
        foreach (var envelope in envelopes)
        {
            await db.KeyDeleteAsync(ProjectionReceipt.ForEnvelope(envelope).Key);
        }

        await db.KeyDeleteAsync(RedisProjectionKeys.ProjectionKey(driver));
        await db.SortedSetRemoveAsync(RedisProjectionKeys.GeoKey, RedisProjectionKeys.Member(driver));
    }

    [Fact]
    public async Task StaleStateEvent_CannotRegressVersionOrReenterGeo()
    {
        var driver = Guid.NewGuid();
        var updater = new RedisProjectionUpdater(_redis.Multiplexer());
        await updater.EnsureFunctionAsync();
        var handler = new DriverProjectionHandler(
            updater, NullLogger<DriverProjectionHandler>.Instance);
        var available9 = Envelope("DriverAvailableV1", new
        {
            driverId = driver, status = "AVAILABLE", version = 9,
        });
        var location = Envelope("DriverLocationAcceptedV1", new
        {
            driverId = driver, longitude = 106.6, latitude = 10.8,
            sessionId = "session-1", sequenceNo = 1, capturedAt = DateTimeOffset.UtcNow,
        });
        var reserved10 = Envelope("DriverReservedV1", new
        {
            driverId = driver, status = "RESERVED", version = 10,
        });
        var staleAvailable9 = Envelope("DriverAvailableV1", new
        {
            driverId = driver, status = "AVAILABLE", version = 9,
        });
        var envelopes = new[] { available9, location, reserved10, staleAvailable9 };
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            Assert.Equal(HandlerOutcome.Done,
                await DeliverAsync(handler, "driver.available.v1", available9));
            Assert.Equal(HandlerOutcome.Done,
                await DeliverAsync(handler, "driver.location-accepted.v1", location));
            Assert.NotNull(await GeoAsync(driver));

            Assert.Equal(HandlerOutcome.Done,
                await DeliverAsync(handler, "driver.reserved.v1", reserved10));
            Assert.Equal("RESERVED", await HashAsync(driver, "derivedStatus"));
            Assert.Equal("10", await HashAsync(driver, "driverAggregateVersion"));
            Assert.Null(await GeoAsync(driver));

            Assert.Equal(HandlerOutcome.Done,
                await DeliverAsync(handler, "driver.available.v1", staleAvailable9));
            Assert.Equal("RESERVED", await HashAsync(driver, "derivedStatus"));
            Assert.Equal("10", await HashAsync(driver, "driverAggregateVersion"));
            Assert.Null(await GeoAsync(driver));

            var staleOutcome = await updater.ApplyStateAsync(
                new DriverStateProjectionUpdate(driver, "AVAILABLE", 9));
            Assert.Equal(SectionOutcome.Stale, staleOutcome.State);
            Assert.Equal("10", await HashAsync(driver, "driverAggregateVersion"));

            _output.WriteLine(
                $"REL-12 driver={driver} appliedVersion=10 staleVersion=9 " +
                $"finalStatus=RESERVED finalVersion=10 geo=absent " +
                $"staleOutcome={staleOutcome.State} elapsedMs={elapsed.ElapsedMilliseconds}");
        }
        finally
        {
            await CleanupAsync(driver, envelopes);
        }
    }

    [Fact]
    public async Task StaleLocation_CannotRegressSequenceTimestampOrCoordinates()
    {
        var driver = Guid.NewGuid();
        var t0 = new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
        var updater = new RedisProjectionUpdater(_redis.Multiplexer());
        await updater.EnsureFunctionAsync();
        var handler = new DriverProjectionHandler(
            updater, NullLogger<DriverProjectionHandler>.Instance);

        var state = Envelope("DriverAvailableV1", new
        {
            driverId = driver, status = "AVAILABLE", version = 1,
        });
        var seq20 = Envelope("DriverLocationAcceptedV1", new
        {
            driverId = driver, longitude = 106.6, latitude = 10.8,
            sessionId = "s", sequenceNo = 20, capturedAt = t0,
        });
        var staleSeq19 = Envelope("DriverLocationAcceptedV1", new
        {
            driverId = driver, longitude = 106.7, latitude = 10.9,
            sessionId = "s", sequenceNo = 19, capturedAt = t0.AddSeconds(1),
        });
        var foreignSession = Envelope("DriverLocationAcceptedV1", new
        {
            driverId = driver, longitude = 106.8, latitude = 11.0,
            sessionId = "foreign", sequenceNo = 1, capturedAt = t0.AddSeconds(2),
        });
        var sameSeqNewer = Envelope("DriverLocationAcceptedV1", new
        {
            driverId = driver, longitude = 106.65, latitude = 10.85,
            sessionId = "s", sequenceNo = 20, capturedAt = t0.AddSeconds(10),
        });
        var sameSeqOlder = Envelope("DriverLocationAcceptedV1", new
        {
            driverId = driver, longitude = 106.9, latitude = 10.95,
            sessionId = "s", sequenceNo = 20, capturedAt = t0.AddSeconds(5),
        });
        var envelopes = new[]
        {
            state, seq20, staleSeq19, foreignSession, sameSeqNewer, sameSeqOlder,
        };
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            Assert.Equal(HandlerOutcome.Done, await DeliverAsync(handler, "driver.available.v1", state));
            Assert.Equal(HandlerOutcome.Done, await DeliverAsync(handler, "driver.location-accepted.v1", seq20));
            Assert.Equal("20", await HashAsync(driver, "sequenceNo"));
            Assert.Equal(10.8, (await GeoAsync(driver))!.Value.Latitude, 4);

            Assert.Equal(HandlerOutcome.Done, await DeliverAsync(handler, "driver.location-accepted.v1", staleSeq19));
            Assert.Equal("20", await HashAsync(driver, "sequenceNo"));
            Assert.Equal("10.8", await HashAsync(driver, "latitude"));
            Assert.Equal("106.6", await HashAsync(driver, "longitude"));
            Assert.Equal(t0.UtcDateTime.ToString("O"), await HashAsync(driver, "capturedAt"));
            Assert.Equal(10.8, (await GeoAsync(driver))!.Value.Latitude, 4);

            Assert.Equal(HandlerOutcome.Done, await DeliverAsync(handler, "driver.location-accepted.v1", foreignSession));
            Assert.Equal("s", await HashAsync(driver, "locationSessionId"));
            Assert.Equal("20", await HashAsync(driver, "sequenceNo"));
            Assert.Equal(10.8, (await GeoAsync(driver))!.Value.Latitude, 4);

            Assert.Equal(HandlerOutcome.Done, await DeliverAsync(handler, "driver.location-accepted.v1", sameSeqNewer));
            Assert.Equal("10.85", await HashAsync(driver, "latitude"));
            Assert.Equal(10.85, (await GeoAsync(driver))!.Value.Latitude, 4);

            Assert.Equal(HandlerOutcome.Done, await DeliverAsync(handler, "driver.location-accepted.v1", sameSeqOlder));
            Assert.Equal("10.85", await HashAsync(driver, "latitude"));
            Assert.Equal(10.85, (await GeoAsync(driver))!.Value.Latitude, 4);

            // Exact duplicate of the currently authoritative sample: receipt
            // convergence must leave the projection byte-identical.
            var beforeDuplicate = await HashAsync(driver, "capturedAt");
            Assert.Equal(HandlerOutcome.Done, await DeliverAsync(handler, "driver.location-accepted.v1", sameSeqNewer));
            Assert.Equal(beforeDuplicate, await HashAsync(driver, "capturedAt"));
            Assert.Equal(10.85, (await GeoAsync(driver))!.Value.Latitude, 4);
            var duplicateReceipt = ProjectionReceipt.ForEnvelope(sameSeqNewer);
            Assert.Equal(duplicateReceipt.Fingerprint,
                (string?)await Db().StringGetAsync(duplicateReceipt.Key));

            var staleOutcome = await updater.ApplyLocationAsync(
                new LocationProjectionUpdate(driver, 106.7, 10.9, "s", 19, t0.AddSeconds(1)));
            Assert.Equal(SectionOutcome.Stale, staleOutcome.Location);

            _output.WriteLine(
                $"REL-13 driver={driver} authoritativeSeq=20 staleSeq=19 " +
                $"foreignSession=rejected sameSeqNewer=applied sameSeqOlder=stale " +
                $"duplicate=converged lat=10.85 staleOutcome={staleOutcome.Location} " +
                $"elapsedMs={elapsed.ElapsedMilliseconds}");
        }
        finally
        {
            await CleanupAsync(driver, envelopes);
        }
    }
}
