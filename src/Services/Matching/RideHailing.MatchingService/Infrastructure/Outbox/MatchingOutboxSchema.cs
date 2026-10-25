// B-W09-03/05: apply C's canonical embedded Outbox DDL to the
// Matching-owned database. There is deliberately no local copy of the table
// definition: NpgsqlOutboxStore.EnsureSchemaAsync is the single schema
// source, so Matching cannot drift from the shared relay/dedup contract.
using Npgsql;
using RideHailing.Messaging.Outbox;

namespace RideHailing.MatchingService.Infrastructure.Outbox;

public static class MatchingOutboxSchema
{
    public static Task EnsureAsync(NpgsqlDataSource dataSource, CancellationToken ct = default) =>
        new NpgsqlOutboxStore(dataSource).EnsureSchemaAsync(ct);

    public static async Task EnsureAsync(string connectionString, CancellationToken ct = default)
    {
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await EnsureAsync(dataSource, ct);
    }
}
