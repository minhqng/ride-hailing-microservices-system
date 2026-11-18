// Projection metrics observed at the REAL FCALL reply, through the
// REAL DriverProjectionHandler and Redis function. Counters are asserted via
// MeterListener; Redis unavailable must degrade, never masquerade as stale.
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RideHailing.MatchingService.Application.Matching;
using RideHailing.MatchingService.Application.Messaging;
using RideHailing.MatchingService.Application.Projection;
using RideHailing.Messaging.Contracts;
using StackExchange.Redis;

namespace RideHailing.MatchingService.IntegrationTests;

[Collection(RedisCollection.Name)]
public sealed class ProjectionMetricsTests
{
    private readonly RedisDatabase _redis;

    public ProjectionMetricsTests(RedisDatabase redis)
    {
        _redis = redis;
    }

    private IDatabase Db() => _redis.Multiplexer().GetDatabase();

    private async Task CleanupAsync(Guid driver)
    {
        var db = Db();
        await db.KeyDeleteAsync(RedisProjectionKeys.ProjectionKey(driver));
        await db.SortedSetRemoveAsync(RedisProjectionKeys.GeoKey, RedisProjectionKeys.Member(driver));
    }

    [Fact]
    public async Task Stale_State_And_Location_Reject_Once_Through_The_Real_Handler()
    {
        using var capture = new MeterCapture();
        var metrics = new MatchingMetrics();
        var updater = new RedisProjectionUpdater(_redis.Multiplexer(), null, metrics);
        await updater.EnsureFunctionAsync();
        var handler = new DriverProjectionHandler(
            updater, NullLogger<DriverProjectionHandler>.Instance);
        var driver = Guid.NewGuid();
        var t0 = new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

        try
        {
            var reserved10 = Envelope("DriverReservedV1", new
            {
                driverId = driver, status = "RESERVED", version = 10,
            });
            var available9 = Envelope("DriverAvailableV1", new
            {
                driverId = driver, status = "AVAILABLE", version = 9,
            });
            var seq20 = Envelope("DriverLocationAcceptedV1", new
            {
                driverId = driver, longitude = 106.6, latitude = 10.8,
                sessionId = "session-1", sequenceNo = 20, capturedAt = t0,
            });
            var seq19 = Envelope("DriverLocationAcceptedV1", new
            {
                driverId = driver, longitude = 106.7, latitude = 10.9,
                sessionId = "session-1", sequenceNo = 19, capturedAt = t0.AddSeconds(1),
            });

            Assert.Equal(HandlerOutcome.Done, await DeliverAsync(handler, "driver.reserved.v1", reserved10));
            Assert.Equal(0, capture.Counter("matching.projection.stale_state_reject"));

            // v10 RESERVED followed by v9 AVAILABLE: rejected once, at the
            // updater. The handler must not add a second increment.
            Assert.Equal(HandlerOutcome.Done, await DeliverAsync(handler, "driver.available.v1", available9));
            Assert.Equal(1, capture.Counter("matching.projection.stale_state_reject"));

            Assert.Equal(HandlerOutcome.Done, await DeliverAsync(handler, "driver.location-accepted.v1", seq20));
            Assert.Equal(0, capture.Counter("matching.projection.stale_location_reject"));

            Assert.Equal(HandlerOutcome.Done, await DeliverAsync(handler, "driver.location-accepted.v1", seq19));
            Assert.Equal(1, capture.Counter("matching.projection.stale_location_reject"));

            Assert.Equal("10", await Db().HashGetAsync(
                RedisProjectionKeys.ProjectionKey(driver), "driverAggregateVersion"));
            Assert.Equal("20", await Db().HashGetAsync(
                RedisProjectionKeys.ProjectionKey(driver), "sequenceNo"));

            var latency = capture.DoubleHistogram("matching.projection.latency");
            Assert.Equal(4, latency.Count);
            Assert.All(latency, value => Assert.True(value >= 0));
        }
        finally
        {
            await CleanupAsync(driver);
        }
    }

    [Fact]
    public async Task Exact_Semantic_Replay_Follows_The_Frozen_Contract_And_Foreign_Session_Is_Stale()
    {
        using var capture = new MeterCapture();
        var metrics = new MatchingMetrics();
        var updater = new RedisProjectionUpdater(_redis.Multiplexer(), null, metrics);
        await updater.EnsureFunctionAsync();
        var driver = Guid.NewGuid();
        var t0 = new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

        try
        {
            var v10 = new DriverStateProjectionUpdate(driver, "RESERVED", 10);
            Assert.Equal(SectionOutcome.Applied, (await updater.ApplyStateAsync(v10)).State);
            // Frozen contract: state applies only when version > projected, so
            // an exact v10 replay is classified stale (not a regression, but the
            // contract says stale). This is the documented exception to "replay
            // is not automatically stale".
            Assert.Equal(SectionOutcome.Stale, (await updater.ApplyStateAsync(v10)).State);
            Assert.Equal(1, capture.Counter("matching.projection.stale_state_reject"));

            var location = new LocationProjectionUpdate(
                driver, 106.6, 10.8, "session-a", 20, t0);
            Assert.Equal(SectionOutcome.Applied, (await updater.ApplyLocationAsync(location)).Location);
            Assert.Equal(SectionOutcome.Stale, (await updater.ApplyLocationAsync(location)).Location);
            Assert.Equal(1, capture.Counter("matching.projection.stale_location_reject"));

            var foreignSession = new LocationProjectionUpdate(
                driver, 106.7, 10.9, "session-b", 21, t0.AddSeconds(1));
            Assert.Equal(SectionOutcome.StaleSession,
                (await updater.ApplyLocationAsync(foreignSession)).Location);
            Assert.Equal(2, capture.Counter("matching.projection.stale_location_reject"));
        }
        finally
        {
            await CleanupAsync(driver);
        }
    }

    [Fact]
    public async Task Redis_Unavailable_Is_Degraded_And_Never_Stale()
    {
        using var capture = new MeterCapture();
        var metrics = new MatchingMetrics();
        var options = new ConfigurationOptions
        {
            EndPoints = { "127.0.0.1:6399" },
            ConnectTimeout = 500,
            SyncTimeout = 500,
            ConnectRetry = 1,
            AbortOnConnectFail = false,
        };
        await using var dead = await ConnectionMultiplexer.ConnectAsync(options);
        var updater = new RedisProjectionUpdater(dead, null, metrics);
        var handler = new DriverProjectionHandler(
            updater, NullLogger<DriverProjectionHandler>.Instance);

        var result = await updater.ApplyStateAsync(
            new DriverStateProjectionUpdate(Guid.NewGuid(), "AVAILABLE", 11));

        Assert.True(result.Degraded);
        var envelope = Envelope("DriverAvailableV1", new
        {
            driverId = Guid.NewGuid(), status = "AVAILABLE", version = 11,
        });
        Assert.Equal(HandlerOutcome.Retry,
            await DeliverAsync(handler, "driver.available.v1", envelope));
        Assert.Equal(0, capture.Counter("matching.projection.stale_state_reject"));
        Assert.Equal(0, capture.Counter("matching.projection.stale_location_reject"));
        var latency = capture.DoubleHistogram("matching.projection.latency");
        Assert.Equal(2, latency.Count);
    }

    [Fact]
    public async Task Content_Conflict_Fcall_Is_Recorded_Before_The_Handler_Throws()
    {
        using var capture = new MeterCapture();
        var metrics = new MatchingMetrics();
        var updater = new RedisProjectionUpdater(_redis.Multiplexer(), null, metrics);
        await updater.EnsureFunctionAsync();
        var handler = new DriverProjectionHandler(
            updater, NullLogger<DriverProjectionHandler>.Instance);
        var driver = Guid.NewGuid();
        var messageId = "content-conflict-" + Guid.NewGuid().ToString("N");

        var first = EnvelopeWithId("DriverAvailableV1", new
        {
            driverId = driver, status = "AVAILABLE", version = 1,
        }, messageId);
        var conflict = EnvelopeWithId("DriverAvailableV1", new
        {
            driverId = driver, status = "AVAILABLE", version = 2,
        }, messageId);

        try
        {
            Assert.Equal(HandlerOutcome.Done,
                await DeliverAsync(handler, "driver.available.v1", first));
            await Assert.ThrowsAsync<ArgumentException>(() =>
                DeliverAsync(handler, "driver.available.v1", conflict));
            Assert.Equal(2, capture.DoubleHistogram("matching.projection.latency").Count);
        }
        finally
        {
            await Db().KeyDeleteAsync(ProjectionReceipt.ForEnvelope(first).Key);
            await CleanupAsync(driver);
        }
    }

    private static MessageEnvelope<JsonElement> Envelope(string type, object payload) =>
        MessageEnvelope<JsonElement>.Create(
            type, "projection", JsonSerializer.SerializeToElement(payload));

    private static MessageEnvelope<JsonElement> EnvelopeWithId(
        string type, object payload, string messageId) =>
        MessageEnvelope<JsonElement>.Create(
            type, "projection", JsonSerializer.SerializeToElement(payload),
            messageId: messageId);

    private static Task<HandlerOutcome> DeliverAsync(
        DriverProjectionHandler handler, string route, MessageEnvelope<JsonElement> envelope) =>
        handler.HandleAsync(new BrokerDelivery(
            route,
            JsonSerializer.SerializeToUtf8Bytes(envelope),
            new Dictionary<string, object?>(),
            0,
            false), default);

    private sealed class MeterCapture : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly ConcurrentDictionary<(string Name, string Tags), long> _counters = new();
        private readonly ConcurrentQueue<(string Name, double Value)> _histograms = new();

        public MeterCapture()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == MatchingMetrics.MeterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
                _counters.AddOrUpdate(
                    (instrument.Name, TagString(tags)), value, (_, current) => current + value));
            _listener.SetMeasurementEventCallback<double>((instrument, value, _, _) =>
                _histograms.Enqueue((instrument.Name, value)));
            _listener.Start();
        }

        public long Counter(string name) =>
            _counters.Where(pair => pair.Key.Name == name).Sum(pair => pair.Value);

        public IReadOnlyList<double> DoubleHistogram(string name) =>
            _histograms.Where(x => x.Name == name).Select(x => x.Value).ToArray();

        public void Dispose() => _listener.Dispose();

        private static string TagString(ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var values = new List<string>(tags.Length);
            foreach (var tag in tags)
            {
                values.Add($"{tag.Key}={tag.Value}");
            }

            return string.Join(",", values);
        }
    }
}
