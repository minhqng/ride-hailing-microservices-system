// Pre-W06 hardening proof: a stored candidate snapshot is the round's frozen
// logical sequence (Doc 02 §12.7: on redelivery resume; do not create a new
// candidate sequence; do not generate new reservation IDs). Redis is a hint;
// Driver reserve is the only assignment authority. Recomputing the same
// (tripId, matchingRound) after Redis churn must replay the original order
// and identities, never mint replacement reservation IDs, never orphan
// READY attempt rows from the snapshot. An EMPTY stored snapshot means no
// decision was ever made, so late arrivals may still complete it.
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
public sealed class MatchingSnapshotPreservationTests : IAsyncLifetime
{
    static MatchingSnapshotPreservationTests()
    {
        Environment.SetEnvironmentVariable("TESTCONTAINERS_RYUK_DISABLED", "true");
    }

    private static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    private readonly MatchingPostgresDatabase _pg;
    private RedisContainer? _redis;
    private ConnectionMultiplexer? _mux;

    public MatchingSnapshotPreservationTests(MatchingPostgresDatabase pg)
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

    private async Task SeedAsync(Guid driver, double lon, double lat)
    {
        var t = Now.AddMinutes(-1);
        var updater = new RedisProjectionUpdater(_mux!);
        await updater.ApplyStateAsync(new(driver, "AVAILABLE", 1));
        await updater.ApplyLocationAsync(new(driver, lon, lat, "s", 1, t));
    }

    private static ComputeRoundRequest Req(Guid trip) => new(trip, 1, 106.6, 10.8);

    [Fact]
    public async Task Redis_Churn_Does_Not_Rewrite_Stored_Candidate_Sequence()
    {
        var trip = Guid.NewGuid();
        var d1 = Guid.NewGuid();
        var d2 = Guid.NewGuid();
        var d3 = Guid.NewGuid();
        await SeedAsync(d1, 106.6, 10.804);
        await SeedAsync(d2, 106.6, 10.818);

        var svc = Service();
        var policy = MatchingPolicy.FrozenW5Baseline();
        var first = await svc.ComputeAsync(Req(trip), policy);
        Assert.Equal(new[] { d1, d2 }, first.Candidates.Select(c => c.DriverId));

        var firstHash = await ReadSnapshotHashAsync(trip);
        Assert.False(string.IsNullOrWhiteSpace(firstHash));

        // Redis churn: D1 disappears (stale hint), D3 appears.
        var db = _mux!.GetDatabase();
        Assert.True(await db.SortedSetRemoveAsync(
            RedisProjectionKeys.GeoKey, RedisProjectionKeys.Member(d1)));
        await SeedAsync(d3, 106.6, 10.806);

        var second = await svc.ComputeAsync(Req(trip), policy);

        // Original logical sequence preserved bit-for-bit: order, ordinals,
        // reservation identities. D3 is deferred to a later round, never
        // minted into this one on redelivery.
        Assert.Equal(
            first.Candidates.Select(c => c.DriverId),
            second.Candidates.Select(c => c.DriverId));
        Assert.Equal(
            first.Candidates.Select(c => c.ReservationId),
            second.Candidates.Select(c => c.ReservationId));
        Assert.Equal(
            first.Candidates.Select(c => c.Ordinal),
            second.Candidates.Select(c => c.Ordinal));

        // No snapshot rewrite (fixed clock ⇒ identical hash) and no new
        // ledger rows: redelivery generates no reservation IDs.
        Assert.Equal(firstHash, await ReadSnapshotHashAsync(trip));
        await using var conn = new NpgsqlConnection(_pg.ConnectionString);
        await conn.OpenAsync();
        await using var count = new NpgsqlCommand(
            "SELECT COUNT(*) FROM matching_attempts WHERE trip_id = @t AND matching_round = 1", conn);
        count.Parameters.AddWithValue("t", trip);
        Assert.Equal(2L, await count.ExecuteScalarAsync());
        await using var d3row = new NpgsqlCommand(
            "SELECT COUNT(*) FROM matching_attempts WHERE trip_id = @t AND matching_round = 1 AND driver_id = @d", conn);
        d3row.Parameters.AddWithValue("t", trip);
        d3row.Parameters.AddWithValue("d", d3);
        Assert.Equal(0L, await d3row.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Empty_Stored_Snapshot_Allows_Late_Arrival_To_Complete_Round()
    {
        // An empty stored snapshot decided nothing (no identities minted), so
        // a later recompute may still complete the round's initial decision.
        var trip = Guid.NewGuid();
        var d3 = Guid.NewGuid();

        var svc = Service();
        var policy = MatchingPolicy.FrozenW5Baseline();
        var first = await svc.ComputeAsync(Req(trip), policy);
        Assert.Empty(first.Candidates);

        await SeedAsync(d3, 106.6, 10.806);
        var second = await svc.ComputeAsync(Req(trip), policy);
        Assert.Equal(new[] { d3 }, second.Candidates.Select(c => c.DriverId));
    }

    private async Task<string?> ReadSnapshotHashAsync(Guid trip)
    {
        await using var conn = new NpgsqlConnection(_pg.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT candidate_snapshot_hash FROM matching_rounds WHERE trip_id = @t AND matching_round = 1", conn);
        cmd.Parameters.AddWithValue("t", trip);
        var value = await cmd.ExecuteScalarAsync();
        return value is DBNull ? null : (string?)value;
    }
}
