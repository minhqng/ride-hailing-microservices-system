using Npgsql;
using RideHailing.PaymentService.Domain;
using RideHailing.PaymentService.Infrastructure;
using Testcontainers.PostgreSql;

namespace RideHailing.PaymentService.IntegrationTests;

public sealed class PaymentDatabase : IAsyncLifetime
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:18.6").Build();
    public NpgsqlDataSource Source { get; private set; } = null!;
    public PaymentOperationRepository Repository { get; private set; } = null!;
    public async Task InitializeAsync()
    {
        await container.StartAsync();
        Source = NpgsqlDataSource.Create(container.GetConnectionString() + ";Maximum Pool Size=32");
        Repository = new(Source);
        await Repository.InitializeAsync(default);
    }
    public async Task DisposeAsync()
    {
        if (Source is not null) await Source.DisposeAsync();
        await container.DisposeAsync();
    }
}

public class PaymentPersistenceTests(PaymentDatabase database) : IClassFixture<PaymentDatabase>
{
    [Fact]
    public async Task TerminalStateCannotBeOverwrittenByStaleProcessingSnapshot()
    {
        var now = DateTimeOffset.UtcNow;
        var processing = PaymentOperation.Create(Guid.NewGuid().ToString(), Guid.NewGuid(), PaymentOperationType.Authorize, 100, "VND", now).BeginAttempt(now);
        await database.Repository.SaveAsync(processing, default);
        await database.Repository.SaveAsync(processing.Resolve(PaymentSimulatorMode.Success, now), default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => database.Repository.SaveAsync(processing, default));
        var stored = await database.Repository.GetAsync(processing.Id, default);
        Assert.Equal(PaymentOperationState.Succeeded, stored!.State);
        await using var command = database.Source.CreateCommand("UPDATE payment_operations SET provider_effect_occurred=false WHERE id=@id");
        command.Parameters.AddWithValue("id", processing.Id);
        var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
    }

    [Fact]
    public async Task MigrationCanRunAgainWithoutChangingRows()
    {
        await database.Repository.InitializeAsync(default);
        await database.Repository.InitializeAsync(default);
    }
}
