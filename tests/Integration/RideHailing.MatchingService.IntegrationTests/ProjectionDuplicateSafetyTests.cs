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
