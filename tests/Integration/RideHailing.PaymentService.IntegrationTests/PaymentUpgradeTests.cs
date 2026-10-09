using Npgsql;
using RideHailing.PaymentService.Domain;

namespace RideHailing.PaymentService.IntegrationTests;

public class PaymentUpgradeTests(PaymentDatabase database) : IClassFixture<PaymentDatabase>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyWhitespaceConstraintIsUpgradedAndDirtyHistoryIsPreserved(bool invalidEffect)
    {
        var valid = PaymentOperation.Create(Guid.NewGuid().ToString(), Guid.NewGuid(),
            PaymentOperationType.Authorize, 100, "VND", DateTimeOffset.UtcNow);
        await database.Repository.SaveAsync(valid, default);
        await using var connection = await database.Source.OpenConnectionAsync();
        await using (var legacy = new NpgsqlCommand("""
            ALTER TABLE payment_operations DROP CONSTRAINT ck_payment_coherence;
            ALTER TABLE payment_operations DROP CONSTRAINT ck_payment_nonblank_identity;
            ALTER TABLE payment_operations ADD CONSTRAINT ck_payment_coherence CHECK (
                btrim(operation_key) <> '' AND
                (NOT provider_effect_occurred OR (provider_effect_id IS NOT NULL AND btrim(provider_effect_id) <> ''))
            );
            """, connection))
            await legacy.ExecuteNonQueryAsync();
        var dirtyId = Guid.NewGuid();
        await using var insert = new NpgsqlCommand("""
            INSERT INTO payment_operations (id, operation_key, trip_id, operation_type, amount_minor,
                currency, state, provider_effect_occurred, provider_effect_id, attempt_count,
                created_at, updated_at, completed_at)
            VALUES (@id, @key, @trip, 'AUTHORIZE', 100, 'VND', @state, @effect, @reference, 1,
                now(), now(), CASE WHEN @effect THEN now() ELSE NULL END)
            """, connection);
        insert.Parameters.AddWithValue("id", dirtyId);
        insert.Parameters.AddWithValue("key", invalidEffect ? "upgrade-" + dirtyId : "\t\n");
        insert.Parameters.AddWithValue("trip", Guid.NewGuid());
        insert.Parameters.AddWithValue("state", invalidEffect ? "SUCCEEDED" : "PENDING");
        insert.Parameters.AddWithValue("effect", invalidEffect);
        insert.Parameters.AddWithValue("reference", NpgsqlTypes.NpgsqlDbType.Text,
            invalidEffect ? "\t\n" : DBNull.Value);
        try
        {
            Assert.Equal(1, await insert.ExecuteNonQueryAsync()); // Accepted by the historical constraint.
            var error = await Assert.ThrowsAsync<PostgresException>(() => database.Repository.InitializeAsync(default));
            Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
            Assert.Equal("ck_payment_nonblank_identity", error.ConstraintName);
            await using var count = new NpgsqlCommand("SELECT count(*) FROM payment_operations WHERE id=@id", connection);
            count.Parameters.AddWithValue("id", dirtyId);
            Assert.Equal(1L, (long)(await count.ExecuteScalarAsync())!); // No destructive cleanup.
            Assert.Equal(valid.Id, (await database.Repository.GetAsync(valid.Id, default))!.Id);
            await using var removeFixture = new NpgsqlCommand("DELETE FROM payment_operations WHERE id=@id", connection);
            removeFixture.Parameters.AddWithValue("id", dirtyId);
            await removeFixture.ExecuteNonQueryAsync();
            await database.Repository.InitializeAsync(default);
            await database.Repository.InitializeAsync(default);
            var rejected = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.CheckViolation, rejected.SqlState);
        }
        finally
        {
            await using var clean = new NpgsqlCommand("DELETE FROM payment_operations WHERE id=@id; ALTER TABLE payment_operations DROP CONSTRAINT ck_payment_coherence", connection);
            clean.Parameters.AddWithValue("id", dirtyId);
            await clean.ExecuteNonQueryAsync();
            await database.Repository.InitializeAsync(default);
        }
    }
}
