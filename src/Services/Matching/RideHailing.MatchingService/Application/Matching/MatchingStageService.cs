// Stage-aware candidate discovery for the production worker.
//
// The broker only records durable intake. This service decides one stage at a
// time: replay a frozen stage verbatim, or discover and freeze it atomically
// with its READY attempt identities. Redis remains a derived hint; Driver
// reserve remains the only assignment authority. A Redis failure is a
// dependency outcome that must be retried, never evidence of no candidates.
using System.Net.Sockets;
using RideHailing.MatchingService.Domain.Matching;
using RideHailing.MatchingService.Infrastructure.Persistence;
using StackExchange.Redis;

namespace RideHailing.MatchingService.Application.Matching;

public enum StageWorkOutcome
{
    Ready,
    RoundGone,
    NotWritable,
    DependencyUnavailable,
}

public sealed record StageWorkResult(
    StageWorkOutcome Outcome,
    IReadOnlyList<StageCandidate> Candidates,
    bool StageEvaluated,
    string? Reason = null);

public sealed class MatchingStageService
{
    private readonly MatchingAttemptRepository _rounds;
    private readonly MatchingWorkRepository _work;
    private readonly CandidateDiscovery _discovery;
    private readonly MatchingMetrics _metrics;
    private readonly TimeProvider _clock;

    public MatchingStageService(
        MatchingAttemptRepository rounds,
        MatchingWorkRepository work,
        CandidateDiscovery discovery,
        MatchingMetrics metrics,
        TimeProvider clock)
    {
        _rounds = rounds;
        _work = work;
        _discovery = discovery;
        _metrics = metrics;
        _clock = clock;
    }

    public async Task<StageWorkResult> EnsureCurrentStageAsync(
        Guid tripId, int round, MatchingPolicy policy, CancellationToken ct = default)
    {
        var row = await _rounds.LoadRoundAsync(tripId, round, ct);
        if (row is null)
        {
            return new StageWorkResult(StageWorkOutcome.RoundGone, [], false);
        }

        if (!string.Equals(row.Status, MatchingRoundStatuses.Processing, StringComparison.Ordinal))
        {
            return new StageWorkResult(StageWorkOutcome.NotWritable, [], false, row.Status);
        }

        if (row.CancelRequested)
        {
            return new StageWorkResult(StageWorkOutcome.NotWritable, [], false, "CANCEL_REQUESTED");
        }

        if (row.PickupLongitude is not double longitude || row.PickupLatitude is not double latitude)
        {
            return new StageWorkResult(StageWorkOutcome.NotWritable, [], false, "PICKUP_MISSING");
        }

        var stage = policy.Stage(row.CurrentStage);
        var existing = await _work.LoadStageCandidatesAsync(tripId, round, stage.Name, ct);
        if (existing.Count > 0)
        {
            // Replay the frozen stage verbatim and heal any attempt row that
            // is missing (the snapshot+attempts commit is atomic, but a
            // legacy/partial state must still converge instead of wedging).
            var replayed = await _work.EnsureStageSnapshotAsync(
                tripId, round, stage.Name, [], policy.RankingConfigVersion,
                _clock.GetUtcNow(), ct);
            return replayed.Outcome switch
            {
                StageEnsureOutcome.Ready => new StageWorkResult(
                    StageWorkOutcome.Ready, replayed.Candidates, true),
                StageEnsureOutcome.RoundGone => new StageWorkResult(
                    StageWorkOutcome.RoundGone, [], false),
                StageEnsureOutcome.StageMismatch => new StageWorkResult(
                    StageWorkOutcome.NotWritable, [], false, "STAGE_MISMATCH"),
                _ => new StageWorkResult(
                    StageWorkOutcome.NotWritable, [], false, replayed.Reason),
            };
        }

        if (string.Equals(row.StageEvaluated, stage.Name, StringComparison.Ordinal))
        {
            // Discovery already completed with zero candidates: the stage is
            // conclusively exhausted. Do not search it again.
            return new StageWorkResult(StageWorkOutcome.Ready, [], true);
        }

        var attempts = await _rounds.LoadAttemptsAsync(tripId, round, ct);
        var attemptedDrivers = attempts.Select(a => a.DriverId).ToHashSet();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        IReadOnlyList<EnrichedCandidate> discovered;
        try
        {
            var count = Math.Min(
                (long)Math.Max(0, stage.MaxCandidates) * policy.DiscoveryOversample,
                policy.DiscoveryHardCap);
            discovered = await _discovery.DiscoverAsync(
                longitude, latitude, stage.SearchRadiusKm, (int)Math.Max(1L, count), ct);
        }
        catch (Exception ex) when (ex is RedisException or SocketException or TimeoutException)
        {
            sw.Stop();
            _metrics.GeoQueryDuration(sw.Elapsed);
            _metrics.Degraded();
            return new StageWorkResult(
                StageWorkOutcome.DependencyUnavailable, [], false, "REDIS_UNAVAILABLE");
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
            var (reason, candidate) = CandidateEligibility.Decide(hit, attemptedDrivers, policy, now);
            if (candidate is null)
            {
                if (reason is EligibilityReason.StaleLocation or EligibilityReason.MissingLocation)
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

        var ranked = CandidateRanker.Rank(eligible, policy, stage.MaxCandidates);
        var seeds = ranked
            .Select(c => new StageCandidateSeed(
                c.DriverId, Guid.NewGuid(), c.DistanceKm, c.Score,
                c.DriverAggregateVersion, c.CapturedAt))
            .ToList();

        var frozen = await _work.EnsureStageSnapshotAsync(
            tripId, round, stage.Name, seeds, policy.RankingConfigVersion, now, ct);
        return frozen.Outcome switch
        {
            StageEnsureOutcome.Ready => new StageWorkResult(
                StageWorkOutcome.Ready, frozen.Candidates, true),
            StageEnsureOutcome.RoundGone => new StageWorkResult(
                StageWorkOutcome.RoundGone, [], false),
            StageEnsureOutcome.StageMismatch => new StageWorkResult(
                StageWorkOutcome.NotWritable, [], false, "STAGE_MISMATCH"),
            _ => new StageWorkResult(
                StageWorkOutcome.NotWritable, [], false, frozen.Reason),
        };
    }
}
