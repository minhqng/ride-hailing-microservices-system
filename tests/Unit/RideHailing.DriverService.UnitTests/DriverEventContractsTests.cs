// B-W08-05: Driver event identity/options proof (pure logic, no broker).
using RideHailing.DriverService.Application.Messaging;

namespace RideHailing.DriverService.UnitTests;

public class DriverEventContractsTests
{
    [Fact]
    public void Driver_Message_Ids_Are_Deterministic_Per_Effect()
    {
        var driver = Guid.NewGuid();
        var r = Guid.NewGuid();
        var a = DriverEventMessageIds.ForDriverEffect(driver, r, 4, "RESERVED");
        var b = DriverEventMessageIds.ForDriverEffect(driver, r, 4, "RESERVED");
        var c = DriverEventMessageIds.ForDriverEffect(driver, Guid.NewGuid(), 4, "RESERVED");
        var d = DriverEventMessageIds.ForDriverEffect(driver, r, 5, "RESERVED");
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.NotEqual(a, d);
        Assert.True(Guid.TryParseExact(a, "N", out _));
    }

    [Fact]
    public void Location_Message_Ids_Are_Deterministic_Per_Sample()
    {
        var driver = Guid.NewGuid();
        Assert.Equal(
            DriverEventMessageIds.ForLocationSample(driver, "s", 7),
            DriverEventMessageIds.ForLocationSample(driver, "s", 7));
        Assert.NotEqual(
            DriverEventMessageIds.ForLocationSample(driver, "s", 7),
            DriverEventMessageIds.ForLocationSample(driver, "s", 8));
    }

    [Fact]
    public void Broker_Options_Reject_Bad_Policy()
    {
        Assert.NotNull(new DriverBrokerOptions { Host = "" }.Validate());
        Assert.NotNull(new DriverBrokerOptions { PublishTimeoutSeconds = 0 }.Validate());
        Assert.NotNull(new DriverBrokerOptions { PublishTimeoutSeconds = 61 }.Validate());
        Assert.Null(new DriverBrokerOptions().Validate());
    }

    [Fact]
    public void Payload_Wire_Form_Is_CamelCase_Under_Default_Serializer()
    {
        // The publisher serializes with default options; the Matching
        // consumer reads camelCase. Attributes (not ambient options) pin the
        // wire form, so this fails without a broker if the contract drifts.
        var stateBytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            new DriverStateEventPayload(Guid.NewGuid(), "RESERVED", 4, Guid.NewGuid(), Guid.NewGuid(), null));
        using var state = System.Text.Json.JsonDocument.Parse(stateBytes);
        foreach (var name in new[] { "driverId", "status", "version", "tripId", "reservationId" })
        {
            Assert.True(state.RootElement.TryGetProperty(name, out _), $"missing payload field '{name}'");
        }

        var locBytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            new DriverLocationEventPayload(Guid.NewGuid(), 1, 2, "s", 7, System.DateTimeOffset.UtcNow));
        using var loc = System.Text.Json.JsonDocument.Parse(locBytes);
        foreach (var name in new[] { "driverId", "longitude", "latitude", "sessionId", "sequenceNo", "capturedAt" })
        {
            Assert.True(loc.RootElement.TryGetProperty(name, out _), $"missing payload field '{name}'");
        }
    }

    [Fact]
    public void MatchingRound_Is_Present_With_CamelCase_When_Recorded()
    {
        // Trip's gate reads the FIELD PRESENCE (TryGetProperty) before the
        // value: a recorded generation must reach the wire as an actual
        // property, not as a null it would classify as malformed.
        var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            new DriverStateEventPayload(Guid.NewGuid(), "RESERVED", 4, Guid.NewGuid(), Guid.NewGuid(), 3));
        using var doc = System.Text.Json.JsonDocument.Parse(bytes);
        Assert.True(doc.RootElement.TryGetProperty("matchingRound", out var round));
        Assert.Equal(3, round.GetInt32());
    }

    [Fact]
    public void MatchingRound_Is_Omitted_When_Provenance_Unknown()
    {
        // An unknown generation omits the property entirely: writing an
        // explicit null would flip Trip's review path into a poison
        // classification, so the omission IS the wire form for "no provenance".
        var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            new DriverStateEventPayload(Guid.NewGuid(), "RESERVED", 4, Guid.NewGuid(), Guid.NewGuid(), null));
        using var doc = System.Text.Json.JsonDocument.Parse(bytes);
        Assert.False(doc.RootElement.TryGetProperty("matchingRound", out _),
            "matchingRound must be omitted, not null, when genesis is unknown");
    }
}
