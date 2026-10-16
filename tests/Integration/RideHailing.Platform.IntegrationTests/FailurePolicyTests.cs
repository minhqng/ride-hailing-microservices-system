using System.Text.Json;
using RideHailing.Messaging.Contracts;
using RideHailing.Messaging.Reliability;

namespace RideHailing.Platform.IntegrationTests;

public class FailurePolicyTests
{
    [Theory]
    [InlineData(FailureKind.Validation, 0, RecoveryAction.Reject)]
    [InlineData(FailureKind.Validation, 3, RecoveryAction.Reject)]
    [InlineData(FailureKind.DomainConflict, 0, RecoveryAction.Reject)]
    [InlineData(FailureKind.DomainConflict, 3, RecoveryAction.Reject)]
    [InlineData(FailureKind.ResourcePressure, 2, RecoveryAction.Retry)]
    [InlineData(FailureKind.ResourcePressure, 3, RecoveryAction.DeadLetter)]
    [InlineData(FailureKind.Configuration, 0, RecoveryAction.DeadLetter)]
    [InlineData(FailureKind.Configuration, 3, RecoveryAction.DeadLetter)]
    public void FailureKind_Maps_To_Canonical_RecoveryAction(FailureKind kind, int attempt, RecoveryAction expected)
    {
        // Canonical taxonomy pins (Doc 05C §26, Doc 02 §19-20):
        // validation/domain conflicts reject without retry, resource pressure
        // retries within budget then dead-letters, configuration never retries.
        Assert.Equal(expected, FailurePolicy.Decide(kind, new(attempt)));
    }

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
