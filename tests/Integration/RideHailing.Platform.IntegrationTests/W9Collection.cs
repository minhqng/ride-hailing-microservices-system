using Npgsql;
using RideHailing.Messaging.Dedup;
using RideHailing.Messaging.Outbox;

namespace RideHailing.Platform.IntegrationTests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class W9Collection : ICollectionFixture<RabbitBackboneFixture>
{
    public const string Name = "W9 outbox and dedup on real infrastructure";
}

internal static class W9TestSupport
{
    public static async Task EnsureSchemaAsync(NpgsqlDataSource source, CancellationToken token = default)
    {
        await new ProcessedMessageStore(source).EnsureSchemaAsync(token);
        await new NpgsqlOutboxStore(source).EnsureSchemaAsync(token);
        await using var command = source.CreateCommand("""
            CREATE TABLE IF NOT EXISTS w9_effects (
                consumer_scope text        NOT NULL,
                message_id     text        NOT NULL,
                correlation_id text        NOT NULL,
                created_at     timestamptz NOT NULL,
                CONSTRAINT w9_effects_pk PRIMARY KEY (consumer_scope, message_id)
            );
            """);
        await command.ExecuteNonQueryAsync(token);
    }

    public static async Task TruncateOutboxAsync(NpgsqlDataSource source, CancellationToken token = default)
    {
        await using var command = source.CreateCommand("TRUNCATE TABLE outbox_messages;");
        await command.ExecuteNonQueryAsync(token);
    }

    public static async Task<long> ScalarAsync(NpgsqlDataSource source, string sql,
        CancellationToken token = default, params (string Name, object? Value)[] parameters)
    {
        await using var command = source.CreateCommand(sql);
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return Convert.ToInt64(await command.ExecuteScalarAsync(token));
    }

    public static async Task EventuallyAsync(Func<Task<bool>> predicate, string what,
        TimeSpan? timeout = null)
    {
        using var cancellation = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(30));
        try
        {
            while (!await predicate()) await Task.Delay(50, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException("Timed out waiting for " + what);
        }
    }
}
