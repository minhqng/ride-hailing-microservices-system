// B-W08: broker decision/unit proof (pure logic, no infrastructure).
// Covers failure classification, redelivery bounds, payload mapping, and
// options validation for the Matching consumption side.
using RideHailing.MatchingService.Application.Messaging;

namespace RideHailing.MatchingService.UnitTests;

public class BrokerContractTests
{
    [Fact]
    public void Canonical_Trip_Payload_Uses_Envelope_Id_And_Rejects_Conflicting_Legacy_Id()
    {
        var trip = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var payload = System.Text.Json.JsonSerializer.SerializeToElement(new { tripId = trip, matchingRound = 1 });
        var envelope = RideHailing.Messaging.Contracts.MessageEnvelope<System.Text.Json.JsonElement>.Create(
            "RideRequestedV1", "correlation", payload, messageId: messageId.ToString("N"));
        Assert.Equal(messageId, RideRequestedHandler.ReadPayload(envelope).InputMessageId);
        var conflict = envelope with { Payload = System.Text.Json.JsonSerializer.SerializeToElement(
            new { tripId = trip, matchingRound = 1, inputMessageId = Guid.NewGuid() }) };
        Assert.Throws<ArgumentException>(() => RideRequestedHandler.ReadPayload(conflict));
    }

    [Fact]
    public void RideRequested_Payload_Requires_Identity_Fields()
    {
        var trip = Guid.NewGuid();
        var msg = Guid.NewGuid();
        var good = RideHailing.Messaging.Contracts.MessageEnvelope<System.Text.Json.JsonElement>.Create(
            "RideRequestedV1",
            Guid.NewGuid().ToString("N"),
            System.Text.Json.JsonDocument.Parse(
                $"{{\"tripId\":\"{trip:N}\",\"matchingRound\":2,\"inputMessageId\":\"{msg:N}\",\"futureField\":1}}").RootElement, messageId: msg.ToString("N"));
        var (t, round, m) = RideRequestedHandler.ReadPayload(good);
        Assert.Equal(trip, t);
        Assert.Equal(2, round);
        Assert.Equal(msg, m);
    }

    [Fact]
    public void RideRequested_Payload_Rejects_Bad_Shapes()
    {
        static System.Text.Json.JsonElement Obj(string json) =>
            System.Text.Json.JsonDocument.Parse(json).RootElement;
        static RideHailing.Messaging.Contracts.MessageEnvelope<System.Text.Json.JsonElement> Env(
            System.Text.Json.JsonElement payload) =>
            RideHailing.Messaging.Contracts.MessageEnvelope<System.Text.Json.JsonElement>.Create(
                "RideRequestedV1", Guid.NewGuid().ToString("N"), payload);

        Assert.Throws<ArgumentException>(() => RideRequestedHandler.ReadPayload(
            Env(Obj("{\"tripId\":\"00000000-0000-0000-0000-000000000000\",\"matchingRound\":1,\"inputMessageId\":\"" + Guid.NewGuid().ToString("N") + "\"}"))));
        Assert.Throws<ArgumentException>(() => RideRequestedHandler.ReadPayload(
            Env(Obj("{\"tripId\":\"" + Guid.NewGuid().ToString("N") + "\",\"matchingRound\":0,\"inputMessageId\":\"" + Guid.NewGuid().ToString("N") + "\"}"))));
        Assert.Throws<ArgumentException>(() => RideRequestedHandler.ReadPayload(
            Env(Obj("{\"tripId\":\"" + Guid.NewGuid().ToString("N") + "\"}"))));
        Assert.Throws<ArgumentException>(() => RideRequestedHandler.ReadPayload(
            Env(System.Text.Json.JsonDocument.Parse("[1,2]").RootElement)));
    }

    [Fact]
    public void Broker_Options_Reject_Bad_Policy()
    {
        Assert.NotNull(new RabbitMqOptions { Host = "" }.Validate());
        Assert.NotNull(new RabbitMqOptions { Prefetch = 0 }.Validate());
        Assert.NotNull(new RabbitMqOptions { Prefetch = 257 }.Validate());
        Assert.NotNull(new RabbitMqOptions { MaxRedeliveries = 0 }.Validate());
        Assert.NotNull(new RabbitMqOptions { RideRequestedQueue = "" }.Validate());
        Assert.Null(new RabbitMqOptions().Validate());
    }
}
