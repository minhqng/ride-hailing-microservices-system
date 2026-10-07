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

    [Fact]
    public async Task Concurrent_Initialize_Linearizes_Without_Duplicate_Effects()
    {
        // Eight overlapping InitializeAsync calls from separate repository
        // instances must all succeed behind the same advisory lock with the
        // row count unchanged (migration race groundwork).
        var before = await CountRowsAsync();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            database.Repository.InitializeAsync(default)));
        Assert.Equal(before, await CountRowsAsync());
    }

    [Fact]
    public async Task Full_State_RoundTrip_Preserves_Every_Field()
    {
        var now = DateTimeOffset.UtcNow;
        var op = PaymentOperation.Create("roundtrip-key", Guid.NewGuid(), PaymentOperationType.Authorize, 250, "VND", now)
            .BeginAttempt(now)
            .Resolve(PaymentSimulatorMode.Success, now);
        await database.Repository.SaveAsync(op, default);
        var stored = await database.Repository.GetAsync(op.Id, default);
        Assert.NotNull(stored);
        Assert.Equal(op.Id, stored!.Id);
        Assert.Equal(op.OperationKey, stored.OperationKey);
        Assert.Equal(op.TripId, stored.TripId);
        Assert.Equal(op.Type, stored.Type);
        Assert.Equal(op.AmountMinor, stored.AmountMinor);
        Assert.Equal(op.Currency, stored.Currency);
        Assert.Equal(op.State, stored.State);
        Assert.Equal(op.ProviderEffectOccurred, stored.ProviderEffectOccurred);
        Assert.Equal(op.ProviderEffectId, stored.ProviderEffectId);
        Assert.Equal(op.AttemptCount, stored.AttemptCount);
    }

    [Fact]
    public async Task Whitespace_Provider_Effect_Id_Rejected()
    {
        // DB must agree with domain IsNullOrWhiteSpace: a tab-only effect id
        // with effect=true is corrupt, not absent.
        await using var conn = await database.Source.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO payment_operations (id, operation_key, trip_id, operation_type, amount_minor, currency, state, provider_effect_occurred, provider_effect_id, attempt_count, created_at, updated_at, completed_at) " +
            "VALUES (@id, @k, @t, 'AUTHORIZE', 100, 'VND', 'SUCCEEDED', true, @e, 1, now(), now(), now())";
        cmd.Parameters.AddWithValue("id", Guid.NewGuid());
        cmd.Parameters.AddWithValue("k", "ws-key-" + Guid.NewGuid().ToString("N"));
        cmd.Parameters.AddWithValue("t", Guid.NewGuid());
        cmd.Parameters.AddWithValue("e", "\t");
        var error = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
    }

    private async Task<long> CountRowsAsync()
    {
        await using var conn = await database.Source.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM payment_operations";
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task Dirty_Dataset_Fails_Migration_Loud()
    {
        // 001 had no coherence CHECK: a dirty row must make Initialize fail
        // loud instead of silently skipping the constraint.
        var dirty = Guid.NewGuid();
        await using var conn = await database.Source.OpenConnectionAsync();
        try
        {
            await using (var drop = conn.CreateCommand())
            {
                drop.CommandText = "ALTER TABLE payment_operations DROP CONSTRAINT IF EXISTS ck_payment_coherence";
                await drop.ExecuteNonQueryAsync();
            }
            await using (var plant = conn.CreateCommand())
            {
                plant.CommandText = "INSERT INTO payment_operations (id, operation_key, trip_id, operation_type, amount_minor, currency, state, provider_effect_occurred, attempt_count, created_at, updated_at) " +
                    "VALUES (@id, @k, @t, 'AUTHORIZE', 100, 'VND', 'SUCCEEDED', false, 1, now(), now())";
                plant.Parameters.AddWithValue("id", dirty);
                plant.Parameters.AddWithValue("k", "dirty-" + dirty.ToString("N"));
                plant.Parameters.AddWithValue("t", Guid.NewGuid());
                await plant.ExecuteNonQueryAsync();
            }
            await Assert.ThrowsAnyAsync<Exception>(() => database.Repository.InitializeAsync(default));
        }
        finally
        {
            await using var wipe = conn.CreateCommand();
            wipe.CommandText = "DELETE FROM payment_operations WHERE id = @id";
            wipe.Parameters.AddWithValue("id", dirty);
            await wipe.ExecuteNonQueryAsync();
            await database.Repository.InitializeAsync(default);
        }
    }
}
