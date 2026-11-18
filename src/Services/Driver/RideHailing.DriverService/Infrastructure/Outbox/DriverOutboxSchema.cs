// Applies the shared embedded Outbox DDL to the
// Driver-owned database. There is deliberately no Driver copy of the table
// definition: NpgsqlOutboxStore.EnsureSchemaAsync is the single schema source,
// so the Driver Outbox cannot drift from the shared relay contract.
using Npgsql;
using RideHailing.Messaging.Outbox;

namespace RideHailing.DriverService.Infrastructure.Outbox;

public static class DriverOutboxSchema
{
    public static Task EnsureAsync(NpgsqlDataSource dataSource, CancellationToken ct = default) =>
        new NpgsqlOutboxStore(dataSource).EnsureSchemaAsync(ct);

    public static async Task EnsureAsync(string connectionString, CancellationToken ct = default)
    {
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await EnsureAsync(dataSource, ct);
    }
}
