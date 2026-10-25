using Npgsql;

namespace RideHailing.DriverService.IntegrationTests;

[Collection(DriverPostgresCollection.Name)]
public sealed class DriverLifecycleMigrationTests(DriverPostgresDatabase db)
{
    [Fact]
    public async Task UpgradePreservesLegacyRowsAndCoherence_AndIsRepeatable()
    {
        var database = "lifecycle_upgrade_" + Guid.NewGuid().ToString("N");
        await using var admin = await db.DataSource.OpenConnectionAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE {database}", admin))
            await create.ExecuteNonQueryAsync();
        try
        {
        var connectionString = new NpgsqlConnectionStringBuilder(db.ConnectionString)
            { Database = database, Pooling = false }.ToString();
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        foreach (var migration in new[] { "001_driver_baseline.sql", "002_driver_coherence.sql",
            "003_driver_identity_nonempty.sql", "003_driver_reserve_identity_nonempty.sql", "004_driver_reservation_round.sql" })
        {
            await using var cmd = new NpgsqlCommand(DriverPostgresDatabase.LoadMigrationScript(migration), conn, tx);
            await cmd.ExecuteNonQueryAsync();
        }
        var driver = Guid.NewGuid();
        var trip = Guid.NewGuid();
        await using (var seed = new NpgsqlCommand("""
            INSERT INTO drivers(driver_id, user_id, status, version, active_trip_id)
            VALUES (@driver, @user, 'ON_TRIP', 4, @trip);
            """, conn, tx))
        {
            seed.Parameters.AddWithValue("driver", driver);
            seed.Parameters.AddWithValue("user", Guid.NewGuid());
            seed.Parameters.AddWithValue("trip", trip);
            await seed.ExecuteNonQueryAsync();
        }
        for (var i = 0; i < 2; i++)
        {
            await using var migrate = new NpgsqlCommand(DriverPostgresDatabase.LoadMigrationScript("005_driver_lifecycle.sql"), conn, tx);
            await migrate.ExecuteNonQueryAsync();
        }
        await using (var check = new NpgsqlCommand("""
            SELECT status, version, active_trip_id, reservation_id,
                (SELECT count(*) FROM driver_lifecycle_effects),
                (SELECT count(*) FROM driver_command_receipts)
            FROM drivers WHERE driver_id = @driver;
            """, conn, tx))
        {
            check.Parameters.AddWithValue("driver", driver);
            await using var reader = await check.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("ON_TRIP", reader.GetString(0));
            Assert.Equal(4, reader.GetInt32(1));
            Assert.Equal(trip, reader.GetGuid(2));
            Assert.True(reader.IsDBNull(3));
            Assert.Equal(0, reader.GetInt64(4));
            Assert.Equal(0, reader.GetInt64(5));
        }
        await using (var invalid = new NpgsqlCommand("UPDATE drivers SET status = 'AVAILABLE' WHERE driver_id = @id", conn, tx))
        {
            invalid.Parameters.AddWithValue("id", driver);
            Assert.Equal(PostgresErrorCodes.CheckViolation,
                (await Assert.ThrowsAsync<PostgresException>(() => invalid.ExecuteNonQueryAsync())).SqlState);
        }
        await tx.RollbackAsync();
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE {database}", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
