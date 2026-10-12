// B-W05-05/06/07/08: round coordinator — the deterministic pipeline.
// load-or-create round → frozen-sequence replay if decided → discover →
// eligibility filter → rank → persist snapshot + READY attempt ledger →
// ordered result.
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
    double PickupLatitude);

public sealed record RoundCandidate(
    int Ordinal,
    Guid DriverId,
    Guid ReservationId,
    double DistanceKm);

public sealed record ComputeRoundMetrics(
    int Discovered,
    int Eligible,
    int StaleFiltered,
    int Excluded,
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
    private static readonly JsonSerializerOptions SnapshotJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

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

        var round = await LoadOrCreateRoundAsync(request, policy, ct);
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
        if (blocked.Count > 0)
        {
            // DSI-017 at the ledger: an unresolved attempt exists, so no new
            // candidate may be offered for this round until it reconciles.
            // W5 never creates such rows itself; this guards foreign/W6 rows.
            throw new InvalidOperationException(
                "Matching round has unresolved attempts; reconcile before requesting new candidates.");
        }

        if (round.SnapshotHash is not null && round.SnapshotJson is not null)
        {
            var stored = JsonSerializer.Deserialize<List<StoredSnapshotEntry>>(
                round.SnapshotJson, SnapshotJsonOptions);
            if (stored is null)
            {
                throw new InvalidOperationException(
                    "Matching round has an unreadable candidate snapshot.");
            }

            if (stored.Count > 0)
            {
                // Frozen-sequence replay (Doc 02 §12.7): the round's logical
                // candidate order and attempt identities were decided by the
                // first compute. Redelivery replays them verbatim — Redis is
                // a hint re-checked only by the reserve/reconcile path against
                // Driver authority, never by minting a replacement sequence
                // here. Conclusively REJECTED drivers are advanced past
                // (§12.5); anything else stays put even if the Redis hint no
                // longer lists it, so no READY identity is ever orphaned from
                // its snapshot. No discovery runs, nothing is stored.
                var replay = stored
                    .Where(e => !rejected.Contains(e.DriverId))
                    .Select(e => new RoundCandidate(e.Ordinal, e.DriverId, e.ReservationId, e.DistanceKm))
                    .ToList();
                return new ComputeRoundResult(
                    request.TripId, request.MatchingRound, "PROCESSING",
                    replay,
                    new ComputeRoundMetrics(0, 0, 0, 0, 0),
                    Degraded: false);
            }

            // An empty stored snapshot decided nothing (no identities minted),
            // so late arrivals may still complete the round's initial decision
            // below. The first non-empty result freezes the sequence.
        }

        var sw = Stopwatch.StartNew();
        IReadOnlyList<EnrichedCandidate> discovered;
        try
        {
            // Overflow-safe: (long) product first, then clamp to the hard cap.
            var count = Math.Min(
                (long)policy.MaxCandidates * policy.DiscoveryOversample,
                policy.DiscoveryHardCap);
            discovered = await _discovery.DiscoverAsync(
                request.PickupLongitude, request.PickupLatitude,
                policy.SearchRadiusKm, (int)Math.Max(1L, count), ct);
        }
        catch (Exception ex) when (ex is RedisException
            or SocketException
            or TimeoutException)
        {
            sw.Stop();
            _metrics.GeoQueryDuration(sw.Elapsed);
            _metrics.Degraded();
            return new ComputeRoundResult(
                request.TripId, request.MatchingRound, "PROCESSING",
                Array.Empty<RoundCandidate>(),
                new ComputeRoundMetrics(0, 0, 0, 0, sw.Elapsed.TotalMilliseconds),
                Degraded: true);
        }

        sw.Stop();
        _metrics.GeoQueryDuration(sw.Elapsed);
        _metrics.Discovered(discovered.Count);

        var now = _clock.GetUtcNow();
        var eligible = new List<EligibleCandidate>(discovered.Count);
        var stale = 0;
        var excluded = 0;
        foreach (var hit in discovered)
        {
            var (reason, candidate) = CandidateEligibility.Decide(hit, rejected, policy, now);
            if (candidate is null)
            {
                if (reason == EligibilityReason.StaleLocation
                    || reason == EligibilityReason.MissingLocation)
                {
                    stale++;
                }
                else
                {
                    excluded++;
                }

                continue;
            }

            eligible.Add(candidate);
        }

        _metrics.Eligible(eligible.Count);
        _metrics.StaleFiltered(stale);
        _metrics.Excluded(excluded);

        var ranked = CandidateRanker.Rank(eligible, policy);

        var nextOrdinal = round.NextAttemptOrdinal;
        if (attempts.Count > 0)
        {
            // Crash-recovery determinism: the persisted row counter may predate
            // attempt rows when a previous compute died before storing its
            // snapshot. Ordinals must continue past existing rows, never reuse.
            nextOrdinal = Math.Max(nextOrdinal, attempts.Max(a => a.Ordinal) + 1);
        }

        var outputs = new List<RoundCandidate>(ranked.Count);
        var snapshots = new List<SnapshotEntry>(ranked.Count);
        foreach (var c in ranked)
        {
            if (!byDriver.TryGetValue(c.DriverId, out var row))
            {
                row = await InsertConvergentAsync(
                    request.TripId, request.MatchingRound, c.DriverId,
                    byDriver, nextOrdinal, ct);
                nextOrdinal = row.Ordinal + 1;
                byDriver[c.DriverId] = row;
            }

            outputs.Add(new RoundCandidate(row.Ordinal, row.DriverId, row.ReservationId, c.DistanceKm));
            snapshots.Add(new SnapshotEntry(
                row.Ordinal, c.DriverId, row.ReservationId, c.DistanceKm,
                c.Score, c.DriverAggregateVersion, c.CapturedAt));
        }

        var hash = CandidateRanker.SnapshotHash(
            snapshots, policy.RankingConfigVersion, now);
        var json = JsonSerializer.Serialize(snapshots.Select(s => new
        {
            ordinal = s.Ordinal,
            driverId = s.DriverId,
            reservationId = s.ReservationId,
            distanceKm = s.DistanceKm,
            score = s.Score,
            version = s.Version,
            capturedAt = s.CapturedAt,
        }));

        await _repo.StoreSnapshotAsync(
            request.TripId, request.MatchingRound, hash, json, ranked.Count, nextOrdinal, ct);

        return new ComputeRoundResult(
            request.TripId, request.MatchingRound, "PROCESSING",
            outputs,
            new ComputeRoundMetrics(
                discovered.Count, eligible.Count, stale, excluded,
                sw.Elapsed.TotalMilliseconds),
            Degraded: false);
    }

    /// <summary>
    /// Stored snapshot entry for frozen-sequence replay. Extra persisted
    /// fields (score, version, capturedAt) are ignored on replay; order,
    /// identity and distance are the replay contract.
    /// </summary>
    private sealed record StoredSnapshotEntry(
        int Ordinal,
        Guid DriverId,
        Guid ReservationId,
        double DistanceKm);

    /// <summary>
    /// Inserts one READY attempt, converging with concurrent duplicate computes:
    /// first try the next free ordinal; on uniqueness loss reload and reuse the
    /// winner's row for the same driver, else continue past the max ordinal.
    /// Bounded: at most 3 attempts, then fail loud (never silent, never sleep).
    /// </summary>
    private async Task<MatchingRepository.AttemptRow> InsertConvergentAsync(
        Guid tripId, int round, Guid driverId,
        Dictionary<Guid, MatchingRepository.AttemptRow> byDriver,
        int firstOrdinal, CancellationToken ct)
    {
        var ordinal = firstOrdinal;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var reservationId = Guid.NewGuid();
            try
            {
                await _repo.InsertAttemptAsync(
                    tripId, round, ordinal, driverId, reservationId, ct);
                return new MatchingRepository.AttemptRow(
                    tripId, round, ordinal, driverId, reservationId, "READY");
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                var current = await _repo.LoadAttemptsAsync(tripId, round, ct);
                var winner = current.FirstOrDefault(a => a.DriverId == driverId);
                if (winner is not null)
                {
                    return winner;
                }

                ordinal = current.Count == 0
                    ? ordinal + 1
                    : current.Max(a => a.Ordinal) + 1;
            }
        }

        throw new InvalidOperationException(
            "Attempt ledger did not converge after bounded retries.");
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

        if (!double.IsFinite(policy.SearchRadiusKm) || policy.SearchRadiusKm <= 0)
        {
            throw new ArgumentException("search radius must be a finite value > 0.", nameof(policy));
        }

        if (policy.MaxCandidates < 0)
        {
            throw new ArgumentException("max candidates must be >= 0.", nameof(policy));
        }

        if (policy.DiscoveryOversample < 1 || policy.DiscoveryHardCap < 1)
        {
            throw new ArgumentException("discovery oversample/hard-cap must be >= 1.", nameof(policy));
        }
    }

    private async Task<MatchingRepository.RoundRow> LoadOrCreateRoundAsync(
        ComputeRoundRequest request, MatchingPolicy policy, CancellationToken ct)
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
                policy.RankingConfigVersion, ct);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            // Lost a concurrent create race: reuse the winner (idempotent trigger).
        }

        var round = await _repo.LoadRoundAsync(request.TripId, request.MatchingRound, ct);
        return round ?? throw new InvalidOperationException("Matching round disappeared after create.");
    }
}
