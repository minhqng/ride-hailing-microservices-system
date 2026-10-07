// B-W08: broker decision/unit proof (pure logic, no infrastructure).
// Covers failure classification, redelivery bounds, payload mapping, and
// options validation for the Matching consumption side.
using System.Net.Sockets;
using RideHailing.MatchingService.Application.Messaging;

namespace RideHailing.MatchingService.UnitTests;

public class BrokerDecisionsTests
{
    [Fact]
    public void Transient_Failures_Requeue()
    {
        Assert.Equal(ConsumeDisposition.Requeue, BrokerDecisions.ClassifyFailure(new TimeoutException()));
        Assert.Equal(ConsumeDisposition.Requeue, BrokerDecisions.ClassifyFailure(new SocketException()));
        Assert.Equal(ConsumeDisposition.Requeue, BrokerDecisions.ClassifyFailure(new OperationCanceledException()));
    }

    [Fact]
    public void Permanent_And_Unknown_Failures_Drop()
    {
        Assert.Equal(ConsumeDisposition.Drop, BrokerDecisions.ClassifyFailure(new ArgumentException("bad payload")));
        Assert.Equal(ConsumeDisposition.Drop, BrokerDecisions.ClassifyFailure(new InvalidOperationException("bug")));
    }

    [Fact]
    public void Delivery_Count_Reads_Quorum_Acquired_Header()
    {
        // RabbitMQ 4.x quorum: redelivery carries x-acquired-count (Int64);
        // sightings = value + 1. Legacy x-delivery-count honored identically.
        Assert.Equal(1, BrokerDecisions.DeliveryCount(null, false));
        Assert.Equal(1, BrokerDecisions.DeliveryCount(
            new Dictionary<string, object?> { ["x-acquired-count"] = 0L }, false));
        Assert.Equal(2, BrokerDecisions.DeliveryCount(
            new Dictionary<string, object?> { ["x-acquired-count"] = 1L }, true));
        Assert.Equal(3, BrokerDecisions.DeliveryCount(
            new Dictionary<string, object?> { ["x-acquired-count"] = 2 }, true));
        Assert.Equal(3, BrokerDecisions.DeliveryCount(
            new Dictionary<string, object?> { ["x-delivery-count"] = 2L }, true));
        Assert.Equal(1, BrokerDecisions.DeliveryCount(
            new Dictionary<string, object?> { ["x-acquired-count"] = "garbage" }, false));
    }

    [Fact]
    public void Headerless_Redelivery_Fails_Closed()
    {
        // No acquisition evidence (classic queue): the count always exceeds
        // any valid budget, so the loop drops instead of hot-looping.
        Assert.Equal(int.MaxValue, BrokerDecisions.DeliveryCount(null, true));
        Assert.True(BrokerDecisions.ExceedsBudget(BrokerDecisions.DeliveryCount(null, true), 10));
    }

    [Fact]
    public void Budget_Bound_Respected()
    {
        Assert.False(BrokerDecisions.ExceedsBudget(3, 3));
        Assert.True(BrokerDecisions.ExceedsBudget(4, 3));
    }

    [Fact]
    public void Oversized_Bodies_Rejected_At_Boundary()
    {
        Assert.False(BrokerDecisions.BodyTooLarge(BrokerDecisions.MaxBodyBytes));
        Assert.True(BrokerDecisions.BodyTooLarge(BrokerDecisions.MaxBodyBytes + 1));
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
                $"{{\"tripId\":\"{trip:N}\",\"matchingRound\":2,\"inputMessageId\":\"{msg:N}\",\"futureField\":1}}").RootElement);
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
