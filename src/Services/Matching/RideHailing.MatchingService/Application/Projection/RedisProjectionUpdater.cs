// Matching-owned projection updater (StackExchange.Redis, no EF).
// Best-effort degraded behavior: connection failure -> Degraded result,
// never throws to the caller. The Driver API path never blocks on Redis.
using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using RideHailing.MatchingService.Application.Matching;
using StackExchange.Redis;

namespace RideHailing.MatchingService.Application.Projection;

public sealed class RedisProjectionUpdater
{
    public const string FunctionName = "driver_projection_update";

    private readonly IConnectionMultiplexer _mux;
    private readonly string _functionName;
    private readonly MatchingMetrics? _metrics;

    public RedisProjectionUpdater(
        IConnectionMultiplexer mux, string? functionName = null, MatchingMetrics? metrics = null)
    {
        _mux = mux;
        _functionName = functionName ?? FunctionName;
        _metrics = metrics;
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

    public Task<ProjectionResult> ApplyStateAsync(DriverStateProjectionUpdate update, ProjectionReceipt? receipt = null)
    {
        if (string.IsNullOrWhiteSpace(update.DerivedStatus))
        {
            throw new ArgumentException("DerivedStatus must not be empty.", nameof(update));
        }

        return CallAsync(update.DriverId,
            update.DerivedStatus, update.AggregateVersion.ToString(),
            "", "", "", "", "", ProjectionSection.State, receipt);
    }

    public Task<ProjectionResult> ApplyLocationAsync(LocationProjectionUpdate update, ProjectionReceipt? receipt = null)
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
            update.CapturedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            ProjectionSection.Location, receipt);
    }

    private async Task<ProjectionResult> CallAsync(
        Guid driverId,
        string status, string version,
        string lon, string lat, string session, string seq, string capturedAt,
        ProjectionSection section, ProjectionReceipt? receipt)
    {
        try
        {
            return await InvokeAsync(driverId, status, version, lon, lat, session, seq, capturedAt, section, receipt)
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
                return await InvokeAsync(driverId, status, version, lon, lat, session, seq, capturedAt, section, receipt)
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
        string lon, string lat, string session, string seq, string capturedAt,
        ProjectionSection section, ProjectionReceipt? receipt)
    {
        var db = _mux.GetDatabase();
        var operation = section == ProjectionSection.State ? "state" : "location";
        var elapsed = Stopwatch.StartNew();
        RedisResult raw;
        try
        {
            raw = await db.ExecuteAsync("FCALL", _functionName, 3,
                RedisProjectionKeys.ProjectionKey(driverId),
                RedisProjectionKeys.GeoKey,
                receipt?.Key ?? "driver:projection:receipt:unused",
                RedisProjectionKeys.Member(driverId),
                status, version, lon, lat, session, seq, capturedAt, receipt?.Fingerprint ?? "");
        }
        catch (Exception ex)
        {
            // Transport/dependency failure is degraded/retry, never stale.
            _metrics?.ProjectionFailed(operation, elapsed.Elapsed, IsDegradation(ex));
            throw;
        }

        // Explicit protocol validation: a foreign library registered under our
        // function name may return a non-array or a short array. That is a
        // clean Degraded (no proven effect), detected here rather than via
        // accidental InvalidCast/IndexOutOfRange, so those exception types
        // retain their fail-loud meaning for genuine programming bugs.
        if (raw.Resp2Type != ResultType.Array)
        {
            var malformed = ProjectionResult.DegradedResult();
            _metrics?.ProjectionObserved(operation, malformed, elapsed.Elapsed);
            return malformed;
        }

        var res = (RedisResult[])raw!;
        if (res.Length != 3)
        {
            var malformed = ProjectionResult.DegradedResult();
            _metrics?.ProjectionObserved(operation, malformed, elapsed.Elapsed);
            return malformed;
        }

        if (string.Equals((string?)res[0], "content-conflict", StringComparison.Ordinal))
        {
            _metrics?.ProjectionConflict(operation, elapsed.Elapsed);
            throw new ArgumentException("MESSAGE_ID_CONTENT_CONFLICT");
        }

        var result = new ProjectionResult(
            Parse((string?)res[0]),
            Parse((string?)res[1]),
            string.Equals((string?)res[2], "added", StringComparison.Ordinal));
        _metrics?.ProjectionObserved(operation, result, elapsed.Elapsed);
        return result;
    }

    private enum ProjectionSection
    {
        State,
        Location,
    }

    // Missing-function detection: Redis 8 answers FCALL against an unloaded
    // library with "ERR Function not found"; older servers answer NOSCRIPT.
    private static bool IsFunctionMissing(RedisServerException ex) =>
        ex.Message.Contains("NOSCRIPT", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("Function not found", StringComparison.OrdinalIgnoreCase);

    // Degradation covers transport/dependency failure only. Reply-shape
    // violations are handled by explicit validation in InvokeAsync above;
    // InvalidCast/IndexOutOfRange are deliberately NOT degraded so a genuine
    // marshalling bug in our own code fails loud instead of masquerading as
    // routine dependency degradation.
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
