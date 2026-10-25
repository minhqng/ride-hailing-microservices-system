using System.Text.Json;
using RideHailing.DriverService.Application.Messaging;
using RideHailing.Messaging.Contracts;

namespace RideHailing.DriverService.UnitTests;

public sealed class DriverCommandTests
{
    private static MessageEnvelope<JsonElement> Envelope(string json) =>
        MessageEnvelope<JsonElement>.Create(DriverCommand.Start, "trip", JsonSerializer.Deserialize<JsonElement>(json));

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"driverId\":\"00000000-0000-0000-0000-000000000000\"}")]
    public void InvalidPayloadFailsBeforePersistence(string json) =>
        Assert.Throws<ArgumentException>(() => DriverCommand.Parse(Envelope(json)));

    [Fact]
    public void FingerprintIsOrderIndependentAndDetectsSemanticChanges()
    {
        var driver = Guid.NewGuid(); var trip = Guid.NewGuid(); var reservation = Guid.NewGuid();
        var original = Envelope($$"""{"driverId":"{{driver}}","tripId":"{{trip}}","reservationId":"{{reservation}}"}""");
        var reordered = Envelope($$"""{"reservationId":"{{reservation}}","tripId":"{{trip}}","driverId":"{{driver}}"}""");
        Assert.Equal(DriverCommand.Parse(original).Fingerprint, DriverCommand.Parse(reordered).Fingerprint);
        Assert.NotEqual(DriverCommand.Parse(original).Fingerprint,
            DriverCommand.Parse(original with { OperationKey = "other" }).Fingerprint);
        Assert.Throws<ArgumentException>(() => DriverCommand.Parse(original with
            { Payload = JsonSerializer.Deserialize<JsonElement>(original.Payload.GetRawText()[..^1] + ",\"driverId\":\"" + driver + "\"}") }));
    }
}
