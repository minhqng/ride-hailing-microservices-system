using System.Diagnostics;
using Npgsql;
using RideHailing.PaymentService.Domain;
using RideHailing.PaymentService.Infrastructure;
using Xunit.Abstractions;

namespace RideHailing.PaymentService.IntegrationTests;

public class PaymentConcurrencyTests(PaymentDatabase database, ITestOutputHelper output) : IClassFixture<PaymentDatabase>
{
    [Theory]
    [InlineData(10, PaymentOperationType.Authorize)]
    [InlineData(50, PaymentOperationType.Authorize)]
    [InlineData(100, PaymentOperationType.Authorize)]
    [InlineData(10, PaymentOperationType.Capture)]
    [InlineData(50, PaymentOperationType.Capture)]
    [InlineData(100, PaymentOperationType.Capture)]
    [InlineData(10, PaymentOperationType.Void)]
    [InlineData(50, PaymentOperationType.Void)]
    [InlineData(100, PaymentOperationType.Void)]
    public async Task SynchronizedCallersConvergeToOneIndependentProviderEffect(int contenders, PaymentOperationType type)
    {
        var key = Guid.NewGuid().ToString();
        var trip = Guid.NewGuid();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var provider = new ProviderLedger(database.ConnectionString);
        var tasks = Enumerable.Range(0, contenders).Select(async i =>
        {
            var request = PaymentOperation.Create(key, trip, type, 100, "VND", DateTimeOffset.UtcNow);
            // Separate application objects; synchronization is in PostgreSQL, not a process mutex.
            var service = new PaymentExecution(database.Source, new PaymentSimulator(provider));
            await start.Task;
            var watch = Stopwatch.StartNew();
            var result = await service.ExecuteAsync(request, PaymentSimulatorMode.SlowSuccess, default);
            return (result, elapsed: watch.Elapsed.TotalMilliseconds);
        }).ToArray();
        start.SetResult();
        var results = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(60));
        foreach (var item in results) Assert.Equal(results[0].result, item.result);
        await using var query = database.Source.CreateCommand("""
            SELECT (SELECT count(*) FROM payment_operations WHERE operation_key=@key),
                   (SELECT count(*) FROM provider_effects WHERE operation_key=@key)
            """);
        query.Parameters.AddWithValue("key", key);
        await using var rows = await query.ExecuteReaderAsync();
        Assert.True(await rows.ReadAsync());
        Assert.Equal(1, rows.GetInt64(0));
        Assert.Equal(1, rows.GetInt64(1));
        var timings = results.Select(r => r.elapsed).Order().ToArray();
        output.WriteLine($"CON-03 type={type}; N={contenders}; key={key}; rows=1; providerEffects=1; canonical callers={results.Length}; p50={timings[(int)Math.Ceiling(contenders * .50)-1]:F2}ms; p95={timings[(int)Math.Ceiling(contenders * .95)-1]:F2}ms");
    }
}
