// Shared lazy Redis multiplexer for the pipeline (W4 updater keeps its own
// short-lived connection for startup load). First use connects; failures
// propagate so the caller degrades explicitly. Disposed with the host.
using RideHailing.MatchingService.Application.Projection;
using StackExchange.Redis;

namespace RideHailing.MatchingService.Application.Matching;

public sealed class SharedRedisMultiplexer : IAsyncDisposable
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ConnectionMultiplexer? _mux;

    public SharedRedisMultiplexer(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<IDatabase> GetDatabaseAsync(CancellationToken ct = default)
    {
        if (_mux is not null)
        {
            return _mux.GetDatabase();
        }

        await _gate.WaitAsync(ct);
        try
        {
            _mux ??= (ConnectionMultiplexer)await RedisProjectionUpdater.ConnectAsync(_connectionString);
            return _mux.GetDatabase();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_mux is not null)
        {
            await _mux.DisposeAsync();
        }

        _gate.Dispose();
    }
}
