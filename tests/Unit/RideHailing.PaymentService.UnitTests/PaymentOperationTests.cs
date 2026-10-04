using RideHailing.PaymentService.Domain;
using RideHailing.PaymentService.Infrastructure;

namespace RideHailing.PaymentService.UnitTests;

public class PaymentOperationTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-28T00:00:00Z");
    private static PaymentOperation New(PaymentOperationType type = PaymentOperationType.Authorize) =>
        PaymentOperation.Create("trip:authorize:v1", Guid.NewGuid(), type, 12000, "VND", Now);

    private static PaymentOperation At(PaymentOperationState state)
    {
        var pending = New();
        if (state == PaymentOperationState.Pending) return pending;
        var processing = pending.BeginAttempt(Now);
        return state switch
        {
            PaymentOperationState.Processing => processing,
            PaymentOperationState.Succeeded => processing.Resolve(PaymentSimulatorMode.Success, Now),
            PaymentOperationState.Declined => processing.Resolve(PaymentSimulatorMode.Decline, Now),
            PaymentOperationState.Unknown => processing.Resolve(PaymentSimulatorMode.TimeoutAfterEffect, Now),
            _ => throw new ArgumentOutOfRangeException(nameof(state))
        };
    }

    [Fact]
    public void AggregateCannotBePubliclyConstructedOrMutated()
    {
        Assert.Empty(typeof(PaymentOperation).GetConstructors());
        foreach (var property in typeof(PaymentOperation).GetProperties())
            Assert.False(property.SetMethod?.IsPublic == true, property.Name);
    }

    [Theory]
    [InlineData(PaymentOperationType.Authorize)]
    [InlineData(PaymentOperationType.Capture)]
    [InlineData(PaymentOperationType.Void)]
    public void AllOperationTypesStartPending(PaymentOperationType type)
    {
        var op = New(type);
        Assert.Equal(type, op.Type);
        Assert.Equal(PaymentOperationState.Pending, op.State);
        Assert.False(op.ProviderEffectOccurred);
        Assert.Null(op.CompletedAt);
    }

    [Theory]
    [InlineData(PaymentSimulatorMode.Success, PaymentOperationState.Succeeded, true)]
    [InlineData(PaymentSimulatorMode.SlowSuccess, PaymentOperationState.Succeeded, true)]
    [InlineData(PaymentSimulatorMode.Decline, PaymentOperationState.Declined, false)]
    [InlineData(PaymentSimulatorMode.TransientFailure, PaymentOperationState.Pending, false)]
    [InlineData(PaymentSimulatorMode.TimeoutBeforeEffect, PaymentOperationState.Unknown, false)]
    [InlineData(PaymentSimulatorMode.TimeoutAfterEffect, PaymentOperationState.Unknown, true)]
    public async Task DeterministicModesPreserveCallerUncertainty(PaymentSimulatorMode mode,
        PaymentOperationState state, bool effect)
    {
        var initial = New();
        var result = await new PaymentSimulator().ExecuteAsync(initial.BeginAttempt(Now), mode, CancellationToken.None);
        Assert.Equal(initial.Id, result.Id);
        Assert.Equal(initial.OperationKey, result.OperationKey);
        Assert.Equal(state, result.State);
        Assert.Equal(effect, result.ProviderEffectOccurred);
        Assert.Equal(effect, result.ProviderEffectId is not null);
        Assert.Equal(1, result.AttemptCount);
        Assert.Equal(state is PaymentOperationState.Succeeded or PaymentOperationState.Declined,
            result.CompletedAt is not null);
    }

    [Theory]
    [InlineData(PaymentOperationState.Processing)]
    [InlineData(PaymentOperationState.Unknown)]
    [InlineData(PaymentOperationState.Succeeded)]
    [InlineData(PaymentOperationState.Declined)]
    public void NonPendingStatesCannotExecuteAnotherAttempt(PaymentOperationState state)
    {
        var operation = At(state);
        Assert.Throws<InvalidOperationException>(() => operation.BeginAttempt(Now));
    }

    [Theory]
    [InlineData(PaymentOperationState.Pending)]
    [InlineData(PaymentOperationState.Unknown)]
    [InlineData(PaymentOperationState.Succeeded)]
    [InlineData(PaymentOperationState.Declined)]
    public void OnlyProcessingCanResolve(PaymentOperationState state) =>
        Assert.Throws<InvalidOperationException>(() => (At(state)).Resolve(PaymentSimulatorMode.Success, Now));

    [Fact]
    public void TransientFailureAllowsAnExplicitNewAttempt()
    {
        var op = New().BeginAttempt(Now).Resolve(PaymentSimulatorMode.TransientFailure, Now);
        Assert.Equal(2, op.BeginAttempt(Now).AttemptCount);
        Assert.False(op.ProviderEffectOccurred);
    }

    [Theory]
    [InlineData(0, "VND")]
    [InlineData(-1, "VND")]
    [InlineData(1, "vnd")]
    [InlineData(1, "VN")]
    [InlineData(1, "123")]
    public void InvalidMoneyIsRejected(long amount, string currency) =>
        Assert.ThrowsAny<ArgumentException>(() => PaymentOperation.Create("key", Guid.NewGuid(), PaymentOperationType.Authorize, amount, currency, Now));

    [Fact]
    public void InvalidIdentityAndEnumsAreRejected()
    {
        Assert.ThrowsAny<ArgumentException>(() => PaymentOperation.Create(" ", Guid.NewGuid(), PaymentOperationType.Authorize, 1, "VND", Now));
        Assert.ThrowsAny<ArgumentException>(() => PaymentOperation.Create("key", Guid.Empty, PaymentOperationType.Authorize, 1, "VND", Now));
        Assert.ThrowsAny<ArgumentException>(() => New((PaymentOperationType)999));
        Assert.ThrowsAny<ArgumentException>(() => New().BeginAttempt(Now).Resolve((PaymentSimulatorMode)999, Now));
    }
}
