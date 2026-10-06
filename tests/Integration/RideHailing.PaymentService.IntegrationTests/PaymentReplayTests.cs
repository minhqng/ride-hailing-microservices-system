using Npgsql;
using RideHailing.PaymentService.Domain;
using RideHailing.PaymentService.Infrastructure;

namespace RideHailing.PaymentService.IntegrationTests;

public class PaymentReplayTests(PaymentDatabase database) : IClassFixture<PaymentDatabase>
{
    [Theory]
    [InlineData(PaymentOperationType.Authorize)]
    [InlineData(PaymentOperationType.Capture)]
    [InlineData(PaymentOperationType.Void)]
    public async Task SameKeyReplaysAfterNewServiceInstanceAndConflictsNeverOverwrite(PaymentOperationType type)
    {
        var key = Guid.NewGuid().ToString();
        var trip = Guid.NewGuid();
        PaymentOperation Request(long amount = 100) => PaymentOperation.Create(key, trip, type, amount, "VND", DateTimeOffset.UtcNow);
        var result = await new PaymentExecution(database.Source, new()).ExecuteAsync(Request(), PaymentSimulatorMode.Success, default);
        var restarted = new PaymentExecution(database.Source, new());
        Assert.Equal(result, await restarted.ExecuteAsync(Request(), PaymentSimulatorMode.Decline, default));
        await Assert.ThrowsAsync<PaymentConflictException>(() => restarted.ExecuteAsync(Request(101), PaymentSimulatorMode.Success, default));
        Assert.Equal(result, await database.Repository.GetAsync(result.Id, default));
        await using var query = database.Source.CreateCommand("SELECT count(*), min(request_fingerprint) FROM payment_operations WHERE operation_key=@key");
        query.Parameters.AddWithValue("key", key);
        await using var rows = await query.ExecuteReaderAsync();
        Assert.True(await rows.ReadAsync());
        Assert.Equal(1, rows.GetInt64(0));
        Assert.Equal(PaymentExecution.Fingerprint(Request()), rows.GetString(1));
    }
}
