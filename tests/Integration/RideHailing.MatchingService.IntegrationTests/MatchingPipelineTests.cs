// B-W05 pipeline proofs on REAL PG + REAL Redis (no fake-green).
// Seeds the W4 projection via the real updater (the same contract the W8
// consumer will use), then proves round persistence, stable identities,
// eligibility, determinism, boundedness, degraded behavior and metrics.
// Uses the shared PG collection + a private Redis container per class.
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Npgsql;
using RideHailing.MatchingService.Application.Matching;
using RideHailing.MatchingService.Application.Projection;
using RideHailing.MatchingService.Domain.Matching;
using RideHailing.MatchingService.Infrastructure.Persistence;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace RideHailing.MatchingService.IntegrationTests;

[Collection(MatchingPostgresCollection.Name)]
public sealed class MatchingPipelineTests : IAsyncLifetime
{
    static MatchingPipelineTests()
    {
        Environment.SetEnvironmentVariable("TESTCONTAINERS_RYUK_DISABLED", "true");
    }

    private static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    private readonly MatchingPostgresDatabase _pg;
    private RedisContainer? _redis;
    private ConnectionMultiplexer? _mux;

    public MatchingPipelineTests(MatchingPostgresDatabase pg)
    {
        _pg = pg;
    }

    public async Task InitializeAsync()
    {
        _redis = new RedisBuilder("redis:8.10.2").WithAutoRemove(true).Build();
        await _redis.StartAsync();
        var cs = RedisDatabase.FixContainerHost(_redis.GetConnectionString());
        _mux = (ConnectionMultiplexer)await RedisProjectionUpdater.ConnectAsync(cs);
        await new RedisProjectionUpdater(_mux).EnsureFunctionAsync();
    }

    public async Task DisposeAsync()
    {
        if (_mux is not null)
        {
            await _mux.DisposeAsync();
        }

        if (_redis is not null)
        {
            await _redis.DisposeAsync();
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private readonly DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private MatchingRoundService Service()
    {
        var mux = _mux!;
        return new MatchingRoundService(
            new MatchingRepository(_pg.ConnectionString),
            new CandidateDiscovery(_ => Task.FromResult(mux.GetDatabase())),
            new MatchingMetrics(),
            new FixedTimeProvider(Now));
    }

    private async Task SeedAsync(Guid driver, string status, int version,
        double lon, double lat, string session, long seq, DateTimeOffset capturedAt)
    {
        var updater = new RedisProjectionUpdater(_mux!);
        await updater.EnsureFunctionAsync();
        await updater.ApplyStateAsync(new(driver, status, version));
        await updater.ApplyLocationAsync(new(driver, lon, lat, session, seq, capturedAt));
    }

    private static ComputeRoundRequest Req(Guid trip, double lon = 106.6, double lat = 10.8) =>
        new(trip, 1, lon, lat);

    [Fact]
    public async Task Happy_Path_Deterministic_D1_D2_D3()
    {
        var trip = Guid.NewGuid();
        var d1 = Guid.NewGuid();
        var d2 = Guid.NewGuid();
        var d3 = Guid.NewGuid();
        var t = Now.AddMinutes(-1);
        // Deliberately seeded out of order: pipeline must still rank D1,D2,D3.
        await SeedAsync(d3, "AVAILABLE", 1, 106.6, 10.836, "s", 1, t);
        await SeedAsync(d1, "AVAILABLE", 1, 106.6, 10.804, "s", 1, t);
        await SeedAsync(d2, "AVAILABLE", 1, 106.6, 10.818, "s", 1, t);

        var r = await Service().ComputeAsync(Req(trip), MatchingPolicy.FrozenW5Baseline());

        Assert.False(r.Degraded);
        Assert.Equal("PROCESSING", r.Status);
        Assert.Equal(new[] { d1, d2, d3 }, r.Candidates.Select(c => c.DriverId));
        Assert.Equal(new[] { 0, 1, 2 }, r.Candidates.Select(c => c.Ordinal));
        Assert.Equal(3, r.Metrics.Discovered);
        Assert.Equal(3, r.Metrics.Eligible);
        Assert.Equal(0, r.Metrics.StaleFiltered);

        // Persisted snapshot round-trips the exact rank order (not just membership).
        await using var conn = new NpgsqlConnection(_pg.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT candidate_snapshot_hash, candidate_count, candidate_snapshot_json " +
            "FROM matching_rounds WHERE trip_id = @t AND matching_round = 1", conn);
        cmd.Parameters.AddWithValue("t", trip);
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.False(string.IsNullOrWhiteSpace(reader.GetString(0)));
        Assert.Equal(3, reader.GetInt32(1));
        using var doc = JsonDocument.Parse(reader.GetString(2));
        Assert.Equal(
            new[] { d1, d2, d3 }.Select(x => x.ToString()),
            doc.RootElement.EnumerateArray().Select(e => e.GetProperty("driverId").GetString()));
    }

    [Fact]
    public async Task Recompute_Reuses_Round_And_Stable_Identities()
    {
        var trip = Guid.NewGuid();
        var d1 = Guid.NewGuid();
        var t = Now.AddMinutes(-1);
        await SeedAsync(d1, "AVAILABLE", 1, 106.6, 10.804, "s", 1, t);

        var svc = Service();
        var first = await svc.ComputeAsync(Req(trip), MatchingPolicy.FrozenW5Baseline());
        var second = await svc.ComputeAsync(Req(trip), MatchingPolicy.FrozenW5Baseline());

        Assert.Equal(
            first.Candidates.Select(c => c.ReservationId),
            second.Candidates.Select(c => c.ReservationId));
        Assert.Equal(
            first.Candidates.Select(c => c.Ordinal),
            second.Candidates.Select(c => c.Ordinal));

        await using var conn = new NpgsqlConnection(_pg.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT COUNT(*) FROM matching_rounds WHERE trip_id = @t", conn);
        cmd.Parameters.AddWithValue("t", trip);
        Assert.Equal(1L, await cmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Concurrent_Duplicate_Compute_Converges()
    {
        var trip = Guid.NewGuid();
        var d1 = Guid.NewGuid();
        var t = Now.AddMinutes(-1);
        await SeedAsync(d1, "AVAILABLE", 1, 106.6, 10.804, "s", 1, t);

        var svc = Service();
        var policy = MatchingPolicy.FrozenW5Baseline();
        var results = await Task.WhenAll(
            svc.ComputeAsync(Req(trip), policy),
            svc.ComputeAsync(Req(trip), policy));

        Assert.Equal(
            results[0].Candidates.Select(c => c.ReservationId),
            results[1].Candidates.Select(c => c.ReservationId));

        await using var conn = new NpgsqlConnection(_pg.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT COUNT(*) FROM matching_attempts WHERE trip_id = @t AND matching_round = 1", conn);
        cmd.Parameters.AddWithValue("t", trip);
        Assert.Equal(1L, await cmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Stale_Reserved_And_Far_Candidates_Filtered()
    {
        // GEO holds AVAILABLE drivers only (W4 projection): RESERVED never
        // reaches discovery, stale-location is filtered by eligibility.
        var trip = Guid.NewGuid();
        var fresh = Guid.NewGuid();
        var reserved = Guid.NewGuid();
        var stale = Guid.NewGuid();
        var far = Guid.NewGuid();
        var t = Now.AddMinutes(-1);
        await SeedAsync(fresh, "AVAILABLE", 1, 106.6, 10.804, "s", 1, t);
        await SeedAsync(reserved, "RESERVED", 5, 106.6, 10.805, "s", 1, t);
        await SeedAsync(stale, "AVAILABLE", 1, 106.6, 10.806, "s", 1, Now.AddMinutes(-30));
        await SeedAsync(far, "AVAILABLE", 1, 107.6, 11.8, "s", 1, t);

        var r = await Service().ComputeAsync(Req(trip), MatchingPolicy.FrozenW5Baseline());

        Assert.Equal(new[] { fresh }, r.Candidates.Select(c => c.DriverId));
        Assert.Equal(2, r.Metrics.Discovered);
        Assert.Equal(1, r.Metrics.Eligible);
        Assert.Equal(1, r.Metrics.StaleFiltered);
    }

    [Fact]
    public async Task Rejected_Driver_Stays_Excluded()
    {
        var trip = Guid.NewGuid();
        var d1 = Guid.NewGuid();
        var t = Now.AddMinutes(-1);
        await SeedAsync(d1, "AVAILABLE", 1, 106.6, 10.804, "s", 1, t);

        var svc = Service();
        var first = await svc.ComputeAsync(Req(trip), MatchingPolicy.FrozenW5Baseline());
        Assert.Single(first.Candidates);

        // Simulate a conclusive rejection recorded by a later reserve flow.
        await using var conn = new NpgsqlConnection(_pg.ConnectionString);
        await conn.OpenAsync();
        await using var mark = new NpgsqlCommand(
            "UPDATE matching_attempts SET state = 'REJECTED' " +
            "WHERE trip_id = @t AND matching_round = 1 AND driver_id = @d", conn);
        mark.Parameters.AddWithValue("t", trip);
        mark.Parameters.AddWithValue("d", d1);
        Assert.Equal(1, await mark.ExecuteNonQueryAsync());

        var second = await svc.ComputeAsync(Req(trip), MatchingPolicy.FrozenW5Baseline());
        Assert.Empty(second.Candidates);
    }

    [Fact]
    public async Task No_Candidates_Stays_Processing_Empty()
    {
        var r = await Service().ComputeAsync(
            Req(Guid.NewGuid(), 100.0, 0.0), MatchingPolicy.FrozenW5Baseline());

        Assert.False(r.Degraded);
        Assert.Empty(r.Candidates);
        Assert.Equal("PROCESSING", r.Status);
        Assert.Equal(0, r.Metrics.Eligible);
    }

    [Fact]
    public async Task Bounded_Snapshot_Caps_Candidates()
    {
        var trip = Guid.NewGuid();
        var t = Now.AddMinutes(-1);
        for (var i = 0; i < 25; i++)
        {
            await SeedAsync(Guid.NewGuid(), "AVAILABLE", 1,
                106.6, 10.8 + (i + 1) * 0.001, "s", 1, t);
        }

        var policy = new MatchingPolicy { MaxCandidates = 20 };
        var r = await Service().ComputeAsync(Req(trip), policy);

        Assert.Equal(20, r.Candidates.Count);
        // Eligible is counted pre-bound (25 pass filter); snapshot keeps top 20.
        Assert.Equal(25, r.Metrics.Eligible);
        Assert.Equal(25, r.Metrics.Discovered);
    }

    [Fact]
    public async Task Metrics_Recorded_On_Instruments()
    {
        using var listener = new MeterListener();
        var counters = new ConcurrentDictionary<string, long>();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == MatchingMetrics.MeterName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>(
            (instrument, value, tags, state) =>
                counters.AddOrUpdate(instrument.Name, value, (_, v) => v + value));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, state) => { });
        listener.Start();

        var trip = Guid.NewGuid();
        var t = Now.AddMinutes(-1);
        await SeedAsync(Guid.NewGuid(), "AVAILABLE", 1, 106.6, 10.804, "s", 1, t);
        await SeedAsync(Guid.NewGuid(), "AVAILABLE", 1, 106.6, 10.805, "s", 1, Now.AddMinutes(-30));
        await Service().ComputeAsync(Req(trip), MatchingPolicy.FrozenW5Baseline());

        Assert.True(counters.GetValueOrDefault("matching.candidates.discovered") >= 2);
        Assert.True(counters.GetValueOrDefault("matching.candidates.eligible") >= 1);
        Assert.True(counters.GetValueOrDefault("matching.candidates.stale_filtered") >= 1);
    }

    [Fact]
    public async Task Redis_Outage_Degrades_Without_Fabrication()
    {
        var dead = new SharedRedisMultiplexer("localhost:6399");
        var svc = new MatchingRoundService(
            new MatchingRepository(_pg.ConnectionString),
            new CandidateDiscovery(_ => dead.GetDatabaseAsync()),
            new MatchingMetrics(),
            new FixedTimeProvider(Now));

        var trip = Guid.NewGuid();
        var r = await svc.ComputeAsync(Req(trip), MatchingPolicy.FrozenW5Baseline());

        Assert.True(r.Degraded);
        Assert.Empty(r.Candidates);
        await dead.DisposeAsync();
    }

    [Fact]
    public async Task Foreign_Geo_Member_Never_Becomes_Candidate()
    {
        var trip = Guid.NewGuid();
        var t = Now.AddMinutes(-1);
        var legit = Guid.NewGuid();
        await SeedAsync(legit, "AVAILABLE", 1, 106.6, 10.804, "s", 1, t);

        var mux = _mux!;
        var db = mux.GetDatabase();
        await db.GeoAddAsync(RedisProjectionKeys.GeoKey, 106.6, 10.804, "not-a-guid");

        var r = await Service().ComputeAsync(Req(trip), MatchingPolicy.FrozenW5Baseline());

        Assert.Equal(new[] { legit }, r.Candidates.Select(c => c.DriverId));
        await db.SortedSetRemoveAsync(RedisProjectionKeys.GeoKey, "not-a-guid");
    }

    [Fact]
    public async Task Terminal_Or_Cancelled_Round_Refuses_Recompute()
    {
        async Task<Guid> SeedRoundWith(string status, bool cancel)
        {
            var trip = Guid.NewGuid();
            await using var conn = new NpgsqlConnection(_pg.ConnectionString);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "INSERT INTO matching_rounds (trip_id, matching_round, input_message_id, status, cancel_requested) " +
                "VALUES (@t, 1, @m, @s, @c)", conn);
            cmd.Parameters.AddWithValue("t", trip);
            cmd.Parameters.AddWithValue("m", Guid.NewGuid());
            cmd.Parameters.AddWithValue("s", status);
            cmd.Parameters.AddWithValue("c", cancel);
            await cmd.ExecuteNonQueryAsync();
            return trip;
        }

        var svc = Service();
        var cancelledTrip = await SeedRoundWith("PROCESSING", true);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ComputeAsync(Req(cancelledTrip), MatchingPolicy.FrozenW5Baseline()));

        var reservedTrip = await SeedRoundWith("RESERVED", false);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ComputeAsync(Req(reservedTrip), MatchingPolicy.FrozenW5Baseline()));
    }

    [Fact]
    public async Task Empty_Capability_Means_No_Requirement()
    {
        var trip = Guid.NewGuid();
        var d1 = Guid.NewGuid();
        await SeedAsync(d1, "AVAILABLE", 1, 106.6, 10.804, "s", 1, Now.AddMinutes(-1));

        var req = Req(trip) with { RequiredCapability = "" };
        var r = await Service().ComputeAsync(req, MatchingPolicy.FrozenW5Baseline());

        Assert.Equal(new[] { d1 }, r.Candidates.Select(c => c.DriverId));
    }
}
