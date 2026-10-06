using System.Text.Json;
using RideHailing.Messaging.Contracts;
using RideHailing.Messaging.Reliability;

namespace RideHailing.Platform.IntegrationTests;

public class FailurePolicyTests
{
    [Fact]
    public void UnsupportedSchemaIsPoisonAndCannotLoop()
    {
        var envelope = MessageEnvelope<JsonElement>.Create("ExampleV1", "workflow", JsonSerializer.SerializeToElement(new { ok = true })) with { SchemaVersion = 999 };
        Assert.Throws<ArgumentException>(envelope.Validate);
        Assert.Equal(RecoveryAction.DeadLetter, FailurePolicy.Decide(FailureKind.Poison, new(0)));
        Assert.Equal(RecoveryAction.Retry, FailurePolicy.Decide(FailureKind.Transient, new(2)));
        Assert.Equal(RecoveryAction.DeadLetter, FailurePolicy.Decide(FailureKind.Transient, new(3)));
        Assert.Equal(RecoveryAction.Reconcile, FailurePolicy.Decide(FailureKind.UnknownEffect, new(3)));
        Assert.Equal(RecoveryAction.Reject, FailurePolicy.Decide(FailureKind.Authorization, new(0)));
        Assert.Throws<ArgumentException>(() => FailurePolicy.Decide(FailureKind.Transient, new(-1)));
        Assert.Throws<ArgumentException>(() => MessageEnvelope<JsonElement>.Create("ExampleV1", "workflow", default));
    }
}
