using Npgsql;

namespace RideHailing.MatchingService.Infrastructure.Persistence;

/// <summary>
/// One migration execution across replicas. The baseline is applied
/// inside a transaction guarded by a stable session-independent advisory key;
/// PostgreSQL serializes concurrent starts and transactional DDL rolls back
/// on a crash. Keys: Payment 754301, Driver 754302, Matching 754303.
/// </summary>
public static class MatchingSchemaBootstrap
{
    public static async Task EnsureBaselineAsync(
        string connectionString, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var transaction = await conn.BeginTransactionAsync(ct);
        await using (var gate = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(754303)", conn, transaction))
        {
            await gate.ExecuteNonQueryAsync(ct);
        }

        await using var cmd = new NpgsqlCommand(MatchingSqlBaseline.Load(), conn, transaction);
        await cmd.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
    }
}
