// B-W04-02/03/04: Matching-owned projection updater (StackExchange.Redis, no EF).
// Best-effort degraded behavior (B-W04-08): connection failure -> Degraded result,
// never throws to the caller. The Driver API path never blocks on Redis.
using System.Globalization;
using System.Net.Sockets;
using StackExchange.Redis;

namespace RideHailing.MatchingService.Application.Projection;

public sealed class RedisProjectionUpdater
{
    public const string FunctionName = "driver_projection_update";

    private readonly IConnectionMultiplexer _mux;
    private readonly string _functionName;

    public RedisProjectionUpdater(IConnectionMultiplexer mux, string? functionName = null)
    {
        _mux = mux;
        _functionName = functionName ?? FunctionName;
    }

    public static async Task<IConnectionMultiplexer> ConnectAsync(string configuration)
    {
        return await ConnectionMultiplexer.ConnectAsync(new ConfigurationOptions
        {
            EndPoints = { configuration },
            ConnectTimeout = 3000,
            SyncTimeout = 5000,
            AbortOnConnectFail = true,
        });
    }

    // Idempotent: FUNCTION LOAD REPLACE succeeds whether or not the lib exists.
    public async Task EnsureFunctionAsync()
    {
        var lua = RedisFunctionSource.Load();
        var db = _mux.GetDatabase();
        await db.ExecuteAsync("FUNCTION", "LOAD", "REPLACE", lua);
    }

    public Task<ProjectionResult> ApplyStateAsync(DriverStateProjectionUpdate update)
    {
        if (string.IsNullOrWhiteSpace(update.DerivedStatus))
        {
            throw new ArgumentException("DerivedStatus must not be empty.", nameof(update));
        }

        return CallAsync(update.DriverId,
            update.DerivedStatus, update.AggregateVersion.ToString(),
            "", "", "", "", "");
    }

    public Task<ProjectionResult> ApplyLocationAsync(LocationProjectionUpdate update)
    {
        if (string.IsNullOrWhiteSpace(update.SessionId))
        {
            throw new ArgumentException("SessionId must not be empty.", nameof(update));
        }

        if (update.SequenceNo < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(update), "SequenceNo must be >= 0.");
        }

        return CallAsync(update.DriverId,
            "", "",
            update.Longitude.ToString("R", CultureInfo.InvariantCulture),
            update.Latitude.ToString("R", CultureInfo.InvariantCulture),
            update.SessionId,
            update.SequenceNo.ToString(CultureInfo.InvariantCulture),
            update.CapturedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
    }

    private async Task<ProjectionResult> CallAsync(
        Guid driverId,
        string status, string version,
        string lon, string lat, string session, string seq, string capturedAt)
    {
        try
        {
            return await InvokeAsync(driverId, status, version, lon, lat, session, seq, capturedAt)
                .ConfigureAwait(false);
        }
        catch (RedisServerException ex) when (IsFunctionMissing(ex))
        {
            // Function library missing (Redis restart / FUNCTION FLUSH after a
            // successful startup load): reload once and retry the single call.
            // A reload failure degrades like any other connection problem.
            try
            {
                await EnsureFunctionAsync().ConfigureAwait(false);
            }
            catch (Exception reload) when (IsDegradation(reload))
            {
                return ProjectionResult.DegradedResult();
            }

            try
            {
                return await InvokeAsync(driverId, status, version, lon, lat, session, seq, capturedAt)
                    .ConfigureAwait(false);
            }
            catch (Exception retry) when (IsDegradation(retry))
            {
                return ProjectionResult.DegradedResult();
            }
        }
        catch (Exception ex) when (IsDegradation(ex))
        {
            return ProjectionResult.DegradedResult();
        }
    }

    private async Task<ProjectionResult> InvokeAsync(
        Guid driverId,
        string status, string version,
        string lon, string lat, string session, string seq, string capturedAt)
    {
        var db = _mux.GetDatabase();
        var res = (RedisResult[])(await db.ExecuteAsync("FCALL", _functionName, 2,
            RedisProjectionKeys.ProjectionKey(driverId),
            RedisProjectionKeys.GeoKey,
            RedisProjectionKeys.Member(driverId),
            status, version, lon, lat, session, seq, capturedAt))!;
        return new ProjectionResult(
            Parse((string?)res[0]),
            Parse((string?)res[1]),
            string.Equals((string?)res[2], "added", StringComparison.Ordinal));
    }

    // Missing-function detection: Redis 8 answers FCALL against an unloaded
    // library with "ERR Function not found"; older servers answer NOSCRIPT.
    private static bool IsFunctionMissing(RedisServerException ex) =>
        ex.Message.Contains("NOSCRIPT", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("Function not found", StringComparison.OrdinalIgnoreCase);

    private static bool IsDegradation(Exception ex) => ex is RedisConnectionException
        or RedisTimeoutException
        or ObjectDisposedException
        or RedisServerException
        or IOException
        or SocketException
        or TimeoutException;

    private static SectionOutcome Parse(string? s) => s switch
    {
        "applied" => SectionOutcome.Applied,
        "stale" => SectionOutcome.Stale,
        "stale-session" => SectionOutcome.StaleSession,
        "skipped" => SectionOutcome.Skipped,
        // 'keytype-error' and anything unrecognized: clean failure, no partial write.
        _ => SectionOutcome.Degraded,
    };
}
