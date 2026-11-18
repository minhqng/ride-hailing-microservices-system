// Frozen Matching terminal-result contract. The identity is a
// deterministic RFC 9562 UUIDv8 derived from (tripId, matchingRound), so every
// retry, duplicate request, restart and relay republish reuses exactly one
// logical result identity. The wire payload stays the minimal shape the
// existing Trip consumer already requires.
using System.Globalization;
using System.Text.Json;
using RideHailing.MatchingService.Application.Messaging;

namespace RideHailing.MatchingService.UnitTests;

public sealed class MatchingTerminalContractTests
{
    [Fact]
    public void NoDriverIdentity_IsDeterministicRfc9562UuidV8()
    {
        var trip = Guid.NewGuid();

        var first = MatchingResultMessageIds.ForNoDriver(trip, 3);
        var second = MatchingResultMessageIds.ForNoDriver(trip, 3);

        Assert.Equal(first, second);
        Assert.Equal(first.ToLowerInvariant(), first);
        Assert.Equal(first, Guid.Parse(first).ToString("D"));

        var bytes = Guid.Parse(first).ToByteArray();
        Assert.Equal(0x80, bytes[7] & 0xF0);
        Assert.Equal(0x80, bytes[8] & 0xC0);
    }

    [Fact]
    public void NoDriverIdentity_IsStableAcrossProcessCulture()
    {
        var trip = Guid.NewGuid();
        var expected = MatchingResultMessageIds.ForNoDriver(trip, 7);

        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            Assert.Equal(expected, MatchingResultMessageIds.ForNoDriver(trip, 7));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void NoDriverIdentity_IsDomainSeparatedPerTripAndRound()
    {
        var trip = Guid.NewGuid();

        var roundOne = MatchingResultMessageIds.ForNoDriver(trip, 1);
        var roundTwo = MatchingResultMessageIds.ForNoDriver(trip, 2);
        var otherTrip = MatchingResultMessageIds.ForNoDriver(Guid.NewGuid(), 1);

        Assert.NotEqual(roundOne, roundTwo);
        Assert.NotEqual(roundOne, otherTrip);
        Assert.NotEqual(roundTwo, otherTrip);
    }

    [Fact]
    public void NoDriverIdentity_RejectsInvalidInput()
    {
        Assert.Throws<ArgumentException>(
            () => MatchingResultMessageIds.ForNoDriver(Guid.Empty, 1));
        Assert.Throws<ArgumentException>(
            () => MatchingResultMessageIds.ForNoDriver(Guid.NewGuid(), 0));
    }

    [Fact]
    public void Payload_ContainsOnlyTheFrozenTripFields()
    {
        var trip = Guid.NewGuid();
        var payload = new MatchingFailedPayload(trip, 2);

        var json = JsonSerializer.SerializeToElement(payload);
        Assert.Equal(
            new[] { "matchingRound", "tripId" },
            json.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(trip, json.GetProperty("tripId").GetGuid());
        Assert.Equal(2, json.GetProperty("matchingRound").GetInt32());
    }

    [Fact]
    public void EventTypeAndRoute_MatchTheFrozenTripConsumerContract()
    {
        Assert.Equal("MatchingFailedV1", MatchingEventTypes.MatchingFailed);
        Assert.Equal("ride.events", MatchingEventRoutes.EventsExchange);
        Assert.Equal("matching.failed.v1", MatchingEventRoutes.MatchingFailed);
    }
}
