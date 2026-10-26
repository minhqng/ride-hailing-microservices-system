using System.Text.Json;
using RideHailing.PaymentService.Application.Payment;
using RideHailing.PaymentService.Domain;
using RideHailing.PaymentService.Infrastructure;
using RideHailing.PaymentService.Integration.Payment;

namespace RideHailing.PaymentService.UnitTests;

public class PaymentMessageContractsTests
{
    [Fact]
    public void AuthorizeAndEventsPreserveOperationKey()
    {
        var command = new AuthorizePaymentV1
        {
            TripId = Guid.NewGuid(),
            OperationKey = "trip-1:authorize:v1",
            AmountMinor = 100,
            Currency = "VND"
        };
        var json = JsonSerializer.Serialize(command);
        var roundtrip = JsonSerializer.Deserialize<AuthorizePaymentV1>(json)!;
        Assert.Equal(command.OperationKey, roundtrip.OperationKey);
        Assert.Equal(command.TripId, roundtrip.TripId);
    }

    [Fact]
    public async Task SuccessfulVoidMapsToTheDedicatedVoidedEvent()
    {
        var pending = PaymentOperation.Create("trip-1:void:v1", Guid.NewGuid(),
            PaymentOperationType.Void, 1, "VND", DateTimeOffset.UtcNow);
        var succeeded = await new PaymentSimulator().ExecuteAsync(
            pending.BeginAttempt(DateTimeOffset.UtcNow), PaymentSimulatorMode.Success, CancellationToken.None);

        var outcome = Assert.IsType<PaymentAuthorizationVoidedV1>(PaymentCommandHandlers.ToVoidResult(succeeded));
        Assert.Equal(pending.TripId, outcome.TripId);
        Assert.Equal(pending.OperationKey, outcome.OperationKey);
        Assert.Equal(succeeded.ProviderEffectId, outcome.ProviderEffectId);
    }
}
