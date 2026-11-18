// Cross-process per-round lease.
//
// A session-level PostgreSQL advisory lock gives exactly one worker instance
// the right to drive one round at a time. The lock lives on a dedicated
// connection: a crashed process drops it automatically, so recovery needs no
// lease-expiry bookkeeping and cannot leak ownership. The durable ledger
// guards remain authoritative; the lease only removes avoidable concurrent
// discovery/dispatch.
using Npgsql;

namespace RideHailing.MatchingService.Infrastructure.Persistence;

public sealed class MatchingRoundLease : IAsyncDisposable
{
    private readonly NpgsqlConnection _connection;
    private readonly long _key;
    private bool _released;

    private MatchingRoundLease(NpgsqlConnection connection, long key)
    {
        _connection = connection;
        _key = key;
    }

    public static async Task<MatchingRoundLease?> TryAcquireAsync(
        string connectionString, Guid tripId, int round, CancellationToken ct = default)
    {
        var connection = new NpgsqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(ct);
            const string Sql = """
                SELECT hashtextextended(@key, 0),
                       pg_try_advisory_lock(hashtextextended(@key, 0));
                """;
            await using var cmd = new NpgsqlCommand(Sql, connection);
            cmd.Parameters.AddWithValue("key", MatchingAdvisoryKeys.Round(tripId, round));
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
            {
                await connection.DisposeAsync();
                return null;
            }

            var key = reader.GetInt64(0);
            if (!reader.GetBoolean(1))
            {
                await connection.DisposeAsync();
                return null;
            }

            return new MatchingRoundLease(connection, key);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_released)
        {
            return;
        }

        _released = true;
        try
        {
            await using var cmd = new NpgsqlCommand("SELECT pg_advisory_unlock(@key);", _connection);
            cmd.Parameters.AddWithValue("key", _key);
            await cmd.ExecuteNonQueryAsync();
        }
        catch
        {
            // Connection teardown releases the session lock regardless.
        }
        finally
        {
            await _connection.DisposeAsync();
        }
    }
}
