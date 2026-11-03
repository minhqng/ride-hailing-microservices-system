using Npgsql;
using RideHailing.MatchingService.Infrastructure.Persistence;

namespace RideHailing.MatchingService.IntegrationTests;

/// <summary>AUD-10: concurrent replica startup applies one serialized baseline.</summary>
[Collection(MatchingPostgresCollection.Name)]
public sealed class MatchingMigrationConcurrencyTests(MatchingPostgresDatabase db)
{
    [Fact]
    public async Task ConcurrentStartup_SerializesBaseline_AndPreservesExistingRows()
    {
        var database = "matching_concurrent_" + Guid.NewGuid().ToString("N");
        await using var admin = await db.DataSource.OpenConnectionAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE {database}", admin))
            await create.ExecuteNonQueryAsync();
        try
        {
            var connectionString = new NpgsqlConnectionStringBuilder(db.ConnectionString)
                { Database = database, Pooling = false }.ToString();

            await Task.WhenAll(
                MatchingSchemaBootstrap.EnsureBaselineAsync(connectionString),
                MatchingSchemaBootstrap.EnsureBaselineAsync(connectionString));

            await using (var seedConn = await OpenAsync(connectionString))
            {
                await using var seed = new NpgsqlCommand(
                    "CREATE TABLE concurrency_marker(id int PRIMARY KEY, note text NOT NULL);" +
                    "INSERT INTO concurrency_marker VALUES (1, 'preserved');", seedConn);
                await seed.ExecuteNonQueryAsync();
            }

            await Task.WhenAll(
                MatchingSchemaBootstrap.EnsureBaselineAsync(connectionString),
                MatchingSchemaBootstrap.EnsureBaselineAsync(connectionString));

            await using var conn = await OpenAsync(connectionString);
            await using var check = new NpgsqlCommand("""
                SELECT (SELECT count(*) FROM concurrency_marker WHERE note = 'preserved'),
                       (SELECT count(*) FROM matching_rounds),
                       (SELECT count(*) FROM matching_attempts);
                """, conn);
            await using var reader = await check.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(1, reader.GetInt64(0));
            Assert.Equal(0, reader.GetInt64(1));
            Assert.Equal(0, reader.GetInt64(2));
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE {database}", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static async Task<NpgsqlConnection> OpenAsync(string connectionString)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        return connection;
    }
}
