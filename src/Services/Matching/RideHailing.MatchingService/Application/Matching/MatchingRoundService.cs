// B-W05-05/06/07/08: round coordinator — the deterministic pipeline.
// load-or-create round → discover → eligibility filter → rank → persist
// snapshot + READY attempt ledger → ordered result.
// Authority model: Redis is a hint (re-validated here), Driver reserve (W6)
// is the only assignment authority. This slice never reserves, never sets
// terminal round states (NO_DRIVER needs W9 Outbox): empty candidates yield
// an empty PROCESSING round. Redis failure degrades (empty + Degraded),
// never fabricated candidates.
using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using Npgsql;
using RideHailing.MatchingService.Domain.Matching;
using RideHailing.MatchingService.Infrastructure.Persistence;
using StackExchange.Redis;

namespace RideHailing.MatchingService.Application.Matching;

public sealed record ComputeRoundRequest(
    Guid TripId,
    int MatchingRound,
    double PickupLongitude,
    double PickupLatitude,
    double? SearchRadiusKm = null,
    int? MaxCandidates = null,
    string? RequiredCapability = null);

public sealed record RoundCandidate(
    int Ordinal,
    Guid DriverId,
    Guid ReservationId,
    double DistanceKm);

public sealed record ComputeRoundMetrics(
    int Discovered,
    int Eligible,
    int StaleFiltered,
    double GeoQueryMs);

public sealed record ComputeRoundResult(
    Guid TripId,
    int MatchingRound,
    string Status,
    IReadOnlyList<RoundCandidate> Candidates,
    ComputeRoundMetrics Metrics,
    bool Degraded);

public sealed class MatchingRoundService
{
    private readonly MatchingRepository _repo;
    private readonly CandidateDiscovery _discovery;
    private readonly MatchingMetrics _metrics;
    private readonly TimeProvider _clock;

    public MatchingRoundService(
        MatchingRepository repo,
        CandidateDiscovery discovery,
        MatchingMetrics metrics,
        TimeProvider clock)
    {
        _repo = repo;
        _discovery = discovery;
        _metrics = metrics;
        _clock = clock;
    }

    public async Task<ComputeRoundResult> ComputeAsync(
        ComputeRoundRequest request,
        MatchingPolicy policy,
        CancellationToken ct = default)
    {
        Validate(request, policy);
        var effective = EffectivePolicy(request, policy);

        var round = await LoadOrCreateRoundAsync(request, effective, ct);
        var attempts = await _repo.LoadAttemptsAsync(request.TripId, request.MatchingRound, ct);
        var rejected = attempts
            .Where(a => string.Equals(a.State, "REJECTED", StringComparison.Ordinal))
            .Select(a => a.DriverId)
            .ToHashSet();
        var byDriver = new Dictionary<Guid, MatchingRepository.AttemptRow>();
        foreach (var a in attempts)
        {
            byDriver.TryAdd(a.DriverId, a);
        }
        var blocked = attempts
            .Where(a => !string.Equals(a.State, "READY", StringComparison.Ordinal)
                && !string.Equals(a.State, "REJECTED", StringComparison.Ordinal))
            .Select(a => a.DriverId)
            .ToHashSet();

        var sw = Stopwatch.StartNew();
        IReadOnlyList<EnrichedCandidate> discovered;
        try
        {
            var count = Math.Min(
                effective.MaxCandidates * effective.DiscoveryOversample,
                effective.DiscoveryHardCap);
            discovered = await _discovery.DiscoverAsync(
                request.PickupLongitude, request.PickupLatitude,
                effective.SearchRadiusKm, Math.Max(1, count), ct);
        }
        catch (Exception ex) when (ex is RedisConnectionException
            or RedisTimeoutException
            or SocketException
            or TimeoutException)
        {
            sw.Stop();
            _metrics.GeoQueryDuration(sw.Elapsed);
            return new ComputeRoundResult(
                request.TripId, request.MatchingRound, "PROCESSING",
                Array.Empty<RoundCandidate>(),
                new ComputeRoundMetrics(0, 0, 0, sw.Elapsed.TotalMilliseconds),
                Degraded: true);
        }

        sw.Stop();
        _metrics.GeoQueryDuration(sw.Elapsed);
        _metrics.Discovered(discovered.Count);

        var now = _clock.GetUtcNow();
        var eligible = new List<EligibleCandidate>(discovered.Count);
        var stale = 0;
        foreach (var hit in discovered)
        {
            if (blocked.Contains(hit.DriverId))
            {
                stale++;
                continue;
            }

            var (reason, candidate) = CandidateEligibility.Decide(hit, rejected, effective, now);
            if (candidate is null)
            {
                stale++;
                continue;
            }

            eligible.Add(candidate);
        }

        _metrics.Eligible(eligible.Count);
        _metrics.StaleFiltered(stale);

        var ranked = CandidateRanker.Rank(eligible, effective);
        var hash = CandidateRanker.SnapshotHash(ranked, effective.RankingConfigVersion);
        var json = JsonSerializer.Serialize(ranked.Select(c => new
        {
            ordinal = c.Ordinal,
            driverId = c.DriverId,
            distanceKm = c.DistanceKm,
            version = c.DriverAggregateVersion,
        }));

        var nextOrdinal = round.NextAttemptOrdinal;
        var outputs = new List<RoundCandidate>(ranked.Count);
        foreach (var c in ranked)
        {
            if (!byDriver.TryGetValue(c.DriverId, out var row))
            {
                var reservationId = Guid.NewGuid();
                try
                {
                    await _repo.InsertAttemptAsync(
                        request.TripId, request.MatchingRound, nextOrdinal,
                        c.DriverId, reservationId, ct);
                    row = new MatchingRepository.AttemptRow(
                        request.TripId, request.MatchingRound, nextOrdinal,
                        c.DriverId, reservationId, "READY");
                    nextOrdinal++;
                }
                catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
                {
                    // Lost an insert race with a concurrent recompute of the same
                    // round: reuse the winner's row instead of failing (idempotent
                    // trigger). Bounded single retry; anything else propagates.
                    var current = await _repo.LoadAttemptsAsync(
                        request.TripId, request.MatchingRound, ct);
                    row = current.FirstOrDefault(a => a.DriverId == c.DriverId)
                        ?? throw new InvalidOperationException(
                            "Concurrent attempt insert won by an unknown row.");
                }
            }

            outputs.Add(new RoundCandidate(row.Ordinal, row.DriverId, row.ReservationId, c.DistanceKm));
        }

        await _repo.StoreSnapshotAsync(
            request.TripId, request.MatchingRound, hash, json, ranked.Count, nextOrdinal, ct);

        return new ComputeRoundResult(
            request.TripId, request.MatchingRound, "PROCESSING",
            outputs,
            new ComputeRoundMetrics(
                discovered.Count, eligible.Count, stale,
                sw.Elapsed.TotalMilliseconds),
            Degraded: false);
    }

    private static void Validate(ComputeRoundRequest request, MatchingPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(policy);
        if (request.TripId == Guid.Empty)
        {
            throw new ArgumentException("tripId must not be empty.", nameof(request));
        }

        if (request.MatchingRound < 1)
        {
            throw new ArgumentException("matchingRound must be >= 1.", nameof(request));
        }

        if (!double.IsFinite(request.PickupLatitude) || !double.IsFinite(request.PickupLongitude)
            || request.PickupLatitude is < -90 or > 90 || request.PickupLongitude is < -180 or > 180)
        {
            throw new ArgumentException("pickup coordinates out of range.", nameof(request));
        }

        if (!double.IsFinite(request.SearchRadiusKm ?? policy.SearchRadiusKm)
            || (request.SearchRadiusKm ?? policy.SearchRadiusKm) <= 0)
        {
            throw new ArgumentException("search radius must be a finite value > 0.", nameof(request));
        }

        if ((request.MaxCandidates ?? policy.MaxCandidates) < 0)
        {
            throw new ArgumentException("max candidates must be >= 0.", nameof(request));
        }

        if (policy.DiscoveryOversample < 1 || policy.DiscoveryHardCap < 1)
        {
            throw new ArgumentException("discovery oversample/hard-cap must be >= 1.", nameof(policy));
        }
    }

    private static MatchingPolicy EffectivePolicy(ComputeRoundRequest request, MatchingPolicy policy) => new()
    {
        SearchRadiusKm = request.SearchRadiusKm ?? policy.SearchRadiusKm,
        MaxCandidates = request.MaxCandidates ?? policy.MaxCandidates,
        DiscoveryOversample = policy.DiscoveryOversample,
        DiscoveryHardCap = policy.DiscoveryHardCap,
        LocationFreshnessWindow = policy.LocationFreshnessWindow,
        FreshnessWeightKmPerSecond = policy.FreshnessWeightKmPerSecond,
        RequiredCapability = string.IsNullOrWhiteSpace(request.RequiredCapability)
            ? (string.IsNullOrWhiteSpace(policy.RequiredCapability) ? null : policy.RequiredCapability)
            : request.RequiredCapability,
        RankingConfigVersion = policy.RankingConfigVersion,
    };

    private async Task<MatchingRepository.RoundRow> LoadOrCreateRoundAsync(
        ComputeRoundRequest request, MatchingPolicy effective, CancellationToken ct)
    {
        var existing = await _repo.LoadRoundAsync(request.TripId, request.MatchingRound, ct);
        if (existing is not null)
        {
            // W5 owns PROCESSING rounds only: terminal states and cancellation
            // belong to reserve/cancel flows (W6+). Recomputing onto them would
            // silently advance a settled round — fail loud instead.
            if (!string.Equals(existing.Status, "PROCESSING", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Matching round is {existing.Status}, not PROCESSING.");
            }

            if (existing.CancelRequested)
            {
                throw new InvalidOperationException(
                    "Matching round cancellation was requested; W5 starts no new attempts on it.");
            }

            return existing;
        }

        try
        {
            await _repo.InsertRoundAsync(
                request.TripId, request.MatchingRound, Guid.NewGuid(),
                effective.RankingConfigVersion, ct);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            // Lost a concurrent create race: reuse the winner (idempotent trigger).
        }

        var round = await _repo.LoadRoundAsync(request.TripId, request.MatchingRound, ct);
        return round ?? throw new InvalidOperationException("Matching round disappeared after create.");
    }
}
