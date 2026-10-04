// B-W04-05/06/07: projection correctness on REAL Redis (DSI-006/007, FUN-03, REL-12/13 early).
// Shares the collection RedisDatabase (one disposable container per run).
// Function is ensured once per test (idempotent FUNCTION LOAD REPLACE).
using RideHailing.MatchingService.Application.Projection;
using StackExchange.Redis;
using Xunit.Abstractions;

namespace RideHailing.MatchingService.IntegrationTests;

[Collection(RedisCollection.Name)]
public class RedisProjectionTests
{
    private readonly ITestOutputHelper _out;
    private readonly RedisDatabase _db;

    public RedisProjectionTests(ITestOutputHelper output, RedisDatabase db)
    {
        _out = output;
        _db = db;
    }

    private bool Ready() => _db.Ready();

    private void LogReady() =>
        _out.WriteLine($"INFRA: redis via {_db.Source} — real assertions will run.");

    private RedisProjectionUpdater Updater() => new(_db.Multiplexer());

    private async Task<RedisProjectionUpdater> ReadyUpdaterAsync()
    {
        var updater = Updater();
        await updater.EnsureFunctionAsync();
        return updater;
    }

    private IDatabase Db() => _db.Multiplexer().GetDatabase();

    private async Task CleanupAsync(Guid driverId)
    {
        try
        {
            var db = Db();
            await db.KeyDeleteAsync(RedisProjectionKeys.ProjectionKey(driverId));
            await db.SortedSetRemoveAsync(RedisProjectionKeys.GeoKey, RedisProjectionKeys.Member(driverId));
        }
        catch
        {
            // best-effort
        }
    }

    private async Task<string?> HGetAsync(Guid driverId, string field)
    {
        return await Db().HashGetAsync(
            RedisProjectionKeys.ProjectionKey(driverId), field);
    }

    private async Task<bool> IsGeoMemberAsync(Guid driverId)
    {
        var res = await Db().ExecuteAsync("GEOSEARCH",
            RedisProjectionKeys.GeoKey, "FROMLONLAT", "106.6", "10.8",
            "BYRADIUS", "50", "km");
        var members = (RedisResult[])res!;
        var want = RedisProjectionKeys.Member(driverId);
        return members.Any(m => string.Equals((string?)m, want, StringComparison.Ordinal));
    }

    // DSI-007: v10 RESERVED then delayed v9 AVAILABLE -> v10 remains, not in GEO (REL-12 early).
    [Fact]
    public async Task StateVersion_Stale_Event_Cannot_Regress()
    {
        if (!Ready())
        {
            Assert.Fail("Real Redis proof required but unavailable: " + _db.SkipReason);
        }

        LogReady();
        var updater = await ReadyUpdaterAsync();

        var id = Guid.NewGuid();
        try
        {
            var r1 = await updater.ApplyStateAsync(new(id, "RESERVED", 10));
            Assert.Equal(SectionOutcome.Applied, r1.State);

            var r2 = await updater.ApplyStateAsync(new(id, "AVAILABLE", 9));
            Assert.Equal(SectionOutcome.Stale, r2.State);

            Assert.Equal("10", await HGetAsync(id, "driverAggregateVersion"));
            Assert.Equal("RESERVED", await HGetAsync(id, "derivedStatus"));
            Assert.False(await IsGeoMemberAsync(id));
        }
        finally
        {
            await CleanupAsync(id);
        }
    }

    // Review correction #2: a state-only update carries no location fields,
    // so v11 AVAILABLE cannot clobber the seq100 location.
    [Fact]
    public async Task StateOnly_Update_Does_Not_Touch_Location()
    {
        if (!Ready())
        {
            Assert.Fail("Real Redis proof required but unavailable: " + _db.SkipReason);
        }

        LogReady();
        var updater = await ReadyUpdaterAsync();

        var id = Guid.NewGuid();
        try
        {
            var t0 = new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
            var loc = await updater.ApplyLocationAsync(
                new(id, 106.6, 10.8, "s1", 100, t0));
            Assert.Equal(SectionOutcome.Applied, loc.Location);

            var st = await updater.ApplyStateAsync(new(id, "AVAILABLE", 11));
            Assert.Equal(SectionOutcome.Applied, st.State);
            Assert.Equal(SectionOutcome.Skipped, st.Location);

            Assert.Equal("100", await HGetAsync(id, "sequenceNo"));
            Assert.Equal("s1", await HGetAsync(id, "locationSessionId"));
            Assert.Equal("11", await HGetAsync(id, "driverAggregateVersion"));
        }
        finally
        {
            await CleanupAsync(id);
        }
    }

    // DSI-006 at projection level: seq20 then seq19 -> seq20 remains (REL-13 early).
    [Fact]
    public async Task Location_Stale_Sequence_Cannot_Regress()
    {
        if (!Ready())
        {
            Assert.Fail("Real Redis proof required but unavailable: " + _db.SkipReason);
        }

        LogReady();
        var updater = await ReadyUpdaterAsync();

        var id = Guid.NewGuid();
        try
        {
            var t0 = new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
            var r1 = await updater.ApplyLocationAsync(new(id, 106.6, 10.8, "s", 20, t0));
            Assert.Equal(SectionOutcome.Applied, r1.Location);

            var r2 = await updater.ApplyLocationAsync(new(id, 106.7, 10.9, "s", 19, t0.AddSeconds(1)));
            Assert.Equal(SectionOutcome.Stale, r2.Location);

            Assert.Equal("20", await HGetAsync(id, "sequenceNo"));
            Assert.Equal("10.8", await HGetAsync(id, "latitude"));
        }
        finally
        {
            await CleanupAsync(id);
        }
    }

    // Frozen W4 session policy: foreign session is never auto-accepted (review correction #1).
    // S1 seq100, then S2 seq1 -> S2 rejected, S1 remains current.
    [Fact]
    public async Task Location_Foreign_Session_Is_Rejected()
    {
        if (!Ready())
        {
            Assert.Fail("Real Redis proof required but unavailable: " + _db.SkipReason);
        }

        LogReady();
        var updater = await ReadyUpdaterAsync();

        var id = Guid.NewGuid();
        try
        {
            var t0 = new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
            var r1 = await updater.ApplyLocationAsync(new(id, 106.6, 10.8, "S1", 100, t0));
            Assert.Equal(SectionOutcome.Applied, r1.Location);

            var r2 = await updater.ApplyLocationAsync(new(id, 106.0, 10.0, "S2", 1, t0.AddMinutes(1)));
            Assert.Equal(SectionOutcome.StaleSession, r2.Location);

            Assert.Equal("S1", await HGetAsync(id, "locationSessionId"));
            Assert.Equal("100", await HGetAsync(id, "sequenceNo"));
        }
        finally
        {
            await CleanupAsync(id);
        }
    }

    // Same session + same seq + newer capturedAt -> applied (tie-break branch).
    [Fact]
    public async Task Location_Same_Sequence_Newer_Timestamp_Wins()
    {
        if (!Ready())
        {
            Assert.Fail("Real Redis proof required but unavailable: " + _db.SkipReason);
        }

        LogReady();
        var updater = await ReadyUpdaterAsync();

        var id = Guid.NewGuid();
        try
        {
            var t0 = new DateTimeOffset(2026, 9, 30, 10, 0, 0, TimeSpan.Zero);
            var r1 = await updater.ApplyLocationAsync(new(id, 106.6, 10.8, "s", 5, t0));
            Assert.Equal(SectionOutcome.Applied, r1.Location);

            var r2 = await updater.ApplyLocationAsync(new(id, 106.7, 10.9, "s", 5, t0.AddSeconds(10)));
            Assert.Equal(SectionOutcome.Applied, r2.Location);
            Assert.Equal("10.9", await HGetAsync(id, "latitude"));
        }
        finally
        {
            await CleanupAsync(id);
        }
    }

    // FUN-03: AVAILABLE + fresh location -> discoverable via GEOSEARCH.
    [Fact]
    public async Task Available_Driver_Is_Discoverable_In_Geo()
    {
        if (!Ready())
        {
            Assert.Fail("Real Redis proof required but unavailable: " + _db.SkipReason);
        }

        LogReady();
        var updater = await ReadyUpdaterAsync();

        var id = Guid.NewGuid();
        try
        {
            var t0 = new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
            await updater.ApplyStateAsync(new(id, "AVAILABLE", 1));
            var loc = await updater.ApplyLocationAsync(new(id, 106.6, 10.8, "s1", 1, t0));
            Assert.True(loc.GeoMember);
            Assert.True(await IsGeoMemberAsync(id));
        }
        finally
        {
            await CleanupAsync(id);
        }
    }

    // Chốt ① (W3): polar latitude accepted by authority but non-indexable in GEO.
    [Fact]
    public async Task Polar_Latitude_Applied_But_Not_Indexed()
    {
        if (!Ready())
        {
            Assert.Fail("Real Redis proof required but unavailable: " + _db.SkipReason);
        }

        LogReady();
        var updater = await ReadyUpdaterAsync();

        var id = Guid.NewGuid();
        try
        {
            var t0 = new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
            await updater.ApplyStateAsync(new(id, "AVAILABLE", 1));
            var loc = await updater.ApplyLocationAsync(new(id, 106.6, 86.0, "s1", 1, t0));
            Assert.Equal(SectionOutcome.Applied, loc.Location);
            Assert.False(loc.GeoMember);
            Assert.Equal("86", await HGetAsync(id, "latitude"));
        }
        finally
        {
            await CleanupAsync(id);
        }
    }

    // State change evicts from GEO: AVAILABLE member -> RESERVED removes it.
    [Fact]
    public async Task Reserved_Driver_Removed_From_Geo()
    {
        if (!Ready())
        {
            Assert.Fail("Real Redis proof required but unavailable: " + _db.SkipReason);
        }

        LogReady();
        var updater = await ReadyUpdaterAsync();

        var id = Guid.NewGuid();
        try
        {
            var t0 = new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
            await updater.ApplyStateAsync(new(id, "AVAILABLE", 1));
            await updater.ApplyLocationAsync(new(id, 106.6, 10.8, "s1", 1, t0));
            Assert.True(await IsGeoMemberAsync(id));

            var r = await updater.ApplyStateAsync(new(id, "RESERVED", 2));
            Assert.Equal(SectionOutcome.Applied, r.State);
            Assert.False(r.GeoMember);
            Assert.False(await IsGeoMemberAsync(id));
        }
        finally
        {
            await CleanupAsync(id);
        }
    }

    // REL-14 early (B-W04-08): projection path degrades instead of throwing.
    // Deterministic faultpoint: FCALL against an unknown function name makes
    // the server fail immediately (RedisServerException), which must surface
    // as Degraded — no network timing involved.
    [Fact]
    public async Task Updater_Returns_Degraded_On_Server_Error()
    {
        if (!Ready())
        {
            Assert.Fail("Real Redis proof required but unavailable: " + _db.SkipReason);
        }

        LogReady();

        var updater = new RedisProjectionUpdater(_db.Multiplexer(), "definitely_not_a_loaded_function");
        var r = await updater.ApplyStateAsync(new(Guid.NewGuid(), "AVAILABLE", 1));
        Assert.True(r.Degraded);
        Assert.Equal(SectionOutcome.Degraded, r.State);
    }

    // Function recovery: after FUNCTION FLUSH the library is gone, but the
    // next projection call reloads it once and applies (no restart needed).
    [Fact]
    public async Task Updater_Recovers_After_Function_Flush()
    {
        if (!Ready())
        {
            Assert.Fail("Real Redis proof required but unavailable: " + _db.SkipReason);
        }

        LogReady();
        var updater = await ReadyUpdaterAsync();

        var id = Guid.NewGuid();
        try
        {
            await Db().ExecuteAsync("FUNCTION", "FLUSH");

            var t0 = new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
            var r1 = await updater.ApplyStateAsync(new(id, "AVAILABLE", 3));
            Assert.False(r1.Degraded);
            Assert.Equal(SectionOutcome.Applied, r1.State);
            Assert.Equal("3", await HGetAsync(id, "driverAggregateVersion"));

            var r2 = await updater.ApplyLocationAsync(new(id, 106.6, 10.8, "s1", 1, t0));
            Assert.Equal(SectionOutcome.Applied, r2.Location);
            Assert.True(r2.GeoMember);
            Assert.True(await IsGeoMemberAsync(id));
        }
        finally
        {
            await CleanupAsync(id);
            await ReadyUpdaterAsync();
        }
    }

    // Corrupt-hash rule: a hash with unparseable ordering fields (foreign
    // writer) heals — the valid new sample overwrites garbage instead of
    // erroring mid-function after a state write.
    [Fact]
    public async Task Corrupt_Hash_Heals_On_Valid_Sample()
    {
        if (!Ready())
        {
            Assert.Fail("Real Redis proof required but unavailable: " + _db.SkipReason);
        }

        LogReady();
        var updater = await ReadyUpdaterAsync();

        var id = Guid.NewGuid();
        try
        {
            var db = Db();
            var key = RedisProjectionKeys.ProjectionKey(id);
            await db.HashSetAsync(key, new[]
            {
                new HashEntry("derivedStatus", "RESERVED"),
                new HashEntry("driverAggregateVersion", "not-a-number"),
                new HashEntry("locationSessionId", "s1"),
                new HashEntry("sequenceNo", "garbage"),
            });

            var t0 = new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
            var r = await updater.ApplyLocationAsync(new(id, 106.6, 10.8, "s1", 5, t0));
            Assert.False(r.Degraded);
            Assert.Equal(SectionOutcome.Applied, r.Location);
            Assert.Equal("5", await HGetAsync(id, "sequenceNo"));

            var rs = await updater.ApplyStateAsync(new(id, "AVAILABLE", 4));
            Assert.Equal(SectionOutcome.Applied, rs.State);
            Assert.Equal("4", await HGetAsync(id, "driverAggregateVersion"));
        }
        finally
        {
            await CleanupAsync(id);
        }
    }

    // Missing coordinates never crash the geo section: unknown coords ZREM.
    [Fact]
    public async Task State_Only_With_No_Coords_Is_Not_Geo_Member()
    {
        if (!Ready())
        {
            Assert.Fail("Real Redis proof required but unavailable: " + _db.SkipReason);
        }

        LogReady();
        var updater = await ReadyUpdaterAsync();

        var id = Guid.NewGuid();
        try
        {
            var r = await updater.ApplyStateAsync(new(id, "AVAILABLE", 1));
            Assert.Equal(SectionOutcome.Applied, r.State);
            Assert.Equal(SectionOutcome.Skipped, r.Location);
            Assert.False(r.GeoMember);
            Assert.False(await IsGeoMemberAsync(id));
        }
        finally
        {
            await CleanupAsync(id);
        }
    }
}
