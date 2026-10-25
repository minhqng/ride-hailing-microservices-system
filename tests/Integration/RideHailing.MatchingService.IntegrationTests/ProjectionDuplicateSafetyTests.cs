using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RideHailing.MatchingService.Application.Messaging;
using RideHailing.MatchingService.Application.Projection;
using RideHailing.Messaging.Contracts;

namespace RideHailing.MatchingService.IntegrationTests;

[Collection(RedisCollection.Name)]
public sealed class ProjectionDuplicateSafetyTests(RedisDatabase redis)
{
    [Fact]
    public async Task DriverLifecycleProducerEvents_RespectProjectionVersionFence()
    {
        var driver = RideHailing.DriverService.Domain.Drivers.Driver.Create(Guid.NewGuid(), Guid.NewGuid());
        var trip = Guid.NewGuid(); var reservation = Guid.NewGuid();
        driver.GoOnline(driver.Version);
        driver.Reserve(trip, reservation, driver.Version);
        driver.MarkTripStarted(trip, reservation);
        var started = RideHailing.DriverService.Application.Messaging.DriverEventMapper.ForTripStartedEffect(driver, trip, reservation);
        driver.MarkTripCompleted(trip);
        var completed = RideHailing.DriverService.Application.Messaging.DriverEventMapper.ForTripCompletedEffect(driver, trip, reservation);
        var updater = new RedisProjectionUpdater(redis.Multiplexer());
        await updater.EnsureFunctionAsync();
        var handler = new DriverProjectionHandler(updater, NullLogger<DriverProjectionHandler>.Instance);
        var db = redis.Multiplexer().GetDatabase();
        var envelopes = new[] { started, completed }.Select(effect =>
            MessageEnvelope<JsonElement>.Create(effect.MessageType, "lifecycle",
                JsonSerializer.SerializeToElement(effect.Payload))).ToArray();
        async Task Deliver(int index)
        {
            var route = index == 0 ? started.RoutingKey : completed.RoutingKey;
            Assert.Equal(HandlerOutcome.Done, await handler.HandleAsync(new(route,
                JsonSerializer.SerializeToUtf8Bytes(envelopes[index]), new Dictionary<string, object?>(), 1, true), default));
        }
        try
        {
            await Deliver(0);
            Assert.Equal("ON_TRIP", (string?)await db.HashGetAsync(RedisProjectionKeys.ProjectionKey(driver.DriverId), "derivedStatus"));
            await Deliver(1); await Deliver(0); await Deliver(1);
            Assert.Equal("AVAILABLE", (string?)await db.HashGetAsync(RedisProjectionKeys.ProjectionKey(driver.DriverId), "derivedStatus"));
            Assert.Equal("5", (string?)await db.HashGetAsync(RedisProjectionKeys.ProjectionKey(driver.DriverId), "driverAggregateVersion"));
        }
        finally
        {
            foreach (var envelope in envelopes) await db.KeyDeleteAsync(ProjectionReceipt.ForEnvelope(envelope).Key);
            await db.KeyDeleteAsync(RedisProjectionKeys.ProjectionKey(driver.DriverId));
            await db.SortedSetRemoveAsync(RedisProjectionKeys.GeoKey, RedisProjectionKeys.Member(driver.DriverId));
        }
    }
    [Fact]
    public async Task DuplicateAndStaleDeliveriesConverge_ConflictingIdentityFailsBeforeEffect()
    {
        var driver = Guid.NewGuid();
        var reservation = Guid.NewGuid();
        var envelope = MessageEnvelope<JsonElement>.Create("DriverReservedV1", "projection-test",
            JsonSerializer.SerializeToElement(new
            {
                driverId = driver, status = "RESERVED", version = 10,
                reservationId = reservation, tripId = Guid.NewGuid(), matchingRound = 1
            }));
        var receipt = ProjectionReceipt.ForEnvelope(envelope);
        var updater = new RedisProjectionUpdater(redis.Multiplexer());
        await updater.EnsureFunctionAsync();
        var handler = new DriverProjectionHandler(updater, NullLogger<DriverProjectionHandler>.Instance);
        Task<HandlerOutcome> Deliver(MessageEnvelope<JsonElement> e, string route = "driver.reserved.v1") =>
            handler.HandleAsync(new(route, JsonSerializer.SerializeToUtf8Bytes(e), new Dictionary<string, object?>(), 1, true), default);
        var db = redis.Multiplexer().GetDatabase();
        try
        {
            Assert.Equal(HandlerOutcome.Done, await Deliver(envelope));
            // Fresh handler/updater models redelivery after effect-before-ACK.
            handler = new(new RedisProjectionUpdater(redis.Multiplexer()), NullLogger<DriverProjectionHandler>.Instance);
            Assert.Equal(HandlerOutcome.Done, await Deliver(envelope));
            var changed = envelope with { Payload = JsonSerializer.SerializeToElement(new
                { driverId = driver, status = "RESERVED", version = 11, reservationId = Guid.NewGuid() }) };
            Assert.Equal("MESSAGE_ID_CONTENT_CONFLICT",
                (await Assert.ThrowsAsync<ArgumentException>(() => Deliver(changed))).Message);
            foreach (var metadataConflict in new[]
                     { envelope with { SagaId = "other-saga" }, envelope with { OperationKey = "other-operation" } })
                Assert.Equal("MESSAGE_ID_CONTENT_CONFLICT",
                    (await Assert.ThrowsAsync<ArgumentException>(() => Deliver(metadataConflict))).Message);
            using var duplicateProperty = JsonDocument.Parse(envelope.Payload.GetRawText()[..^1] + ",\"version\":11}");
            Assert.Equal("Duplicate JSON property",
                (await Assert.ThrowsAsync<ArgumentException>(() => Deliver(
                    envelope with { Payload = duplicateProperty.RootElement.Clone() }))).Message);
            // Property order is not semantic content.
            using var reordered = JsonDocument.Parse("{" + string.Join(",", envelope.Payload.EnumerateObject().Reverse()
                .Select(p => JsonSerializer.Serialize(p.Name) + ":" + p.Value.GetRawText())) + "}");
            Assert.Equal(HandlerOutcome.Done, await Deliver(envelope with { Payload = reordered.RootElement.Clone() }));
            var stale = MessageEnvelope<JsonElement>.Create("DriverReservationReleasedV1", "projection-test",
                JsonSerializer.SerializeToElement(new { driverId = driver, status = "AVAILABLE", version = 9 }));
            Assert.Equal(HandlerOutcome.Done, await Deliver(stale, "driver.reservation-released.v1"));
            Assert.Equal("RESERVED", (string?)await db.HashGetAsync(RedisProjectionKeys.ProjectionKey(driver), "derivedStatus"));
            Assert.Equal("10", (string?)await db.HashGetAsync(RedisProjectionKeys.ProjectionKey(driver), "driverAggregateVersion"));
            Assert.Equal(receipt.Fingerprint, (string?)await db.StringGetAsync(receipt.Key));
            await db.KeyDeleteAsync(ProjectionReceipt.ForEnvelope(stale).Key);
        }
        finally
        {
            await db.KeyDeleteAsync(new StackExchange.Redis.RedisKey[] { receipt.Key, RedisProjectionKeys.ProjectionKey(driver) });
            await db.SortedSetRemoveAsync(RedisProjectionKeys.GeoKey, RedisProjectionKeys.Member(driver));
        }
    }

    [Fact]
    public async Task InvalidReceiptKeyType_DoesNotPartiallyWriteProjection()
    {
        var driver = Guid.NewGuid();
        var envelope = MessageEnvelope<JsonElement>.Create("DriverAvailableV1", "test",
            JsonSerializer.SerializeToElement(new { driverId = driver, status = "AVAILABLE", version = 1 }));
        var receipt = ProjectionReceipt.ForEnvelope(envelope);
        var db = redis.Multiplexer().GetDatabase();
        await db.HashSetAsync(receipt.Key, "foreign", "type");
        var updater = new RedisProjectionUpdater(redis.Multiplexer());
        await updater.EnsureFunctionAsync();
        try
        {
            var result = await updater.ApplyStateAsync(new(driver, "AVAILABLE", 1), receipt);
            Assert.True(result.Degraded);
            Assert.False(await db.KeyExistsAsync(RedisProjectionKeys.ProjectionKey(driver)));
        }
        finally { await db.KeyDeleteAsync(receipt.Key); }
    }
}
