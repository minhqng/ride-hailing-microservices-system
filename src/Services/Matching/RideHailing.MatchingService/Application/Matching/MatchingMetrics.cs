// B-W05-09/B-W12-02/03: Matching pipeline instruments. Live here because the
// Matching application owns these quantities; OTLP export wiring stays
// C-owned (PlatformBootstrap) and tests observe via MeterListener.
//
// Semantics are deliberately split by truth source:
//   matching.round.duration      one orchestration REQUEST (including replay).
//   matching.round.candidate_count observed durable snapshot size per live round.
//   matching.attempt.created_total durable NEW matching_attempts row.
//   matching.unknown_total       newly observed unresolved reserve outcome.
//   matching.reconcile_total     actual authoritative poll (including failures).
//   matching.no_driver_total     committed NEW NO_DRIVER terminal decision.
using System.Diagnostics.Metrics;
using RideHailing.MatchingService.Application.Projection;

namespace RideHailing.MatchingService.Application.Matching;

public sealed class MatchingMetrics
{
    public const string MeterName = MatchingTelemetry.MeterName;

    private static readonly Meter Meter = new(MeterName);

    private readonly Counter<long> _discovered =
        Meter.CreateCounter<long>("matching.candidates.discovered", description: "GEOSEARCH hits before eligibility.");
    private readonly Counter<long> _eligible =
        Meter.CreateCounter<long>("matching.candidates.eligible", description: "Candidates passing eligibility.");
    private readonly Counter<long> _staleFiltered =
        Meter.CreateCounter<long>("matching.candidates.stale_filtered", description: "Candidates rejected as stale or missing location/freshness.");
    private readonly Counter<long> _excluded =
        Meter.CreateCounter<long>("matching.candidates.excluded", description: "Candidates excluded as incapable, rejected, blocked or invalid (not stale).");
    private readonly Counter<long> _degraded =
        Meter.CreateCounter<long>("matching.rounds.degraded", description: "Compute calls returning degraded for dependency failure.");
    private readonly Histogram<double> _geoQueryDurationMs =
        Meter.CreateHistogram<double>("matching.geo.query.duration", unit: "ms", description: "GEOSEARCH+enrich wall time.");

    private readonly Histogram<double> _roundDurationMs =
        Meter.CreateHistogram<double>("matching.round.duration", unit: "ms",
            description: "Wall time of one reserve-orchestration request, tagged by outcome.");
    private readonly Histogram<long> _candidateCount =
        Meter.CreateHistogram<long>("matching.round.candidate_count", unit: "{candidate}",
            description: "Candidate sequence length offered to a live (PROCESSING) round (caller/frozen-snapshot input).");
    private readonly Counter<long> _roundTotal =
        Meter.CreateCounter<long>("matching.round.total",
            description: "Reserve-orchestration requests observed, tagged by outcome and replay.");
    private readonly Counter<long> _attemptCreated =
        Meter.CreateCounter<long>("matching.attempt.created_total",
            description: "Durable NEW matching_attempts rows (write-ahead attempt identity).");
    private readonly Counter<long> _unknown =
        Meter.CreateCounter<long>("matching.unknown_total",
            description: "Committed UNKNOWN attempt transitions from reserve transport ambiguity or contention exhaustion; ordinary conflicts and pre-effect failures are excluded.");
    private readonly Counter<long> _reconcile =
        Meter.CreateCounter<long>("matching.reconcile_total",
            description: "Actual authoritative reconcile polls, including repeated transport uncertainty.");
    private readonly Counter<long> _noDriver =
        Meter.CreateCounter<long>("matching.no_driver_total",
            description: "Committed NEW NO_DRIVER terminal decisions; relays and replays are excluded.");
    private readonly Counter<long> _staleStateReject =
        Meter.CreateCounter<long>("matching.projection.stale_state_reject",
            description: "FCALL state section rejected as stale (including equal-version contract replays).");
    private readonly Counter<long> _staleLocationReject =
        Meter.CreateCounter<long>("matching.projection.stale_location_reject",
            description: "FCALL location section rejected as stale/stale-session.");
    private readonly Histogram<double> _projectionLatencyMs =
        Meter.CreateHistogram<double>("matching.projection.latency", unit: "ms",
            description: "Single FCALL execution (Redis round-trip + function), tagged by operation and outcome.");

    public void Discovered(int count) => _discovered.Add(count);

    public void Eligible(int count) => _eligible.Add(count);

    public void StaleFiltered(int count) => _staleFiltered.Add(count);

    public void Excluded(int count) => _excluded.Add(count);

    public void Degraded() => _degraded.Add(1);

    public void GeoQueryDuration(TimeSpan duration) => _geoQueryDurationMs.Record(duration.TotalMilliseconds);

    public void RoundDuration(TimeSpan duration, string outcome) =>
        _roundDurationMs.Record(duration.TotalMilliseconds,
            new KeyValuePair<string, object?>("outcome", outcome));

    public void CandidateCount(int count) => _candidateCount.Record(count);

    public void RoundObserved(string outcome, bool replay) =>
        _roundTotal.Add(1,
            new KeyValuePair<string, object?>("outcome", outcome),
            new KeyValuePair<string, object?>("replay", replay));

    public void AttemptCreated() => _attemptCreated.Add(1);

    public void Unknown(string reason) =>
        _unknown.Add(1, new KeyValuePair<string, object?>("reason", reason));

    public void Reconcile(string outcome) =>
        _reconcile.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

    public void NoDriver() => _noDriver.Add(1);

    /// <summary>
    /// One observation per FCALL result. Stale counters are emitted only from
    /// the updater (the layer that sees the function reply), never from the
    /// handler, so layering cannot double count. The W4 contract classifies an
    /// equal aggregate version and a non-advancing location sample as
    /// 'stale'; those semantic replays therefore increment the same counter.
    /// </summary>
    public void ProjectionObserved(string operation, ProjectionResult result, TimeSpan elapsed)
    {
        var outcome = result.Degraded
            ? "degraded"
            : operation == "state"
                ? SectionOutcomeCode(result.State)
                : SectionOutcomeCode(result.Location);
        _projectionLatencyMs.Record(elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>("operation", operation),
            new KeyValuePair<string, object?>("outcome", outcome));

        if (result.State == SectionOutcome.Stale)
        {
            _staleStateReject.Add(1);
        }

        if (result.Location is SectionOutcome.Stale or SectionOutcome.StaleSession)
        {
            _staleLocationReject.Add(1);
        }
    }

    public void ProjectionFailed(string operation, TimeSpan elapsed, bool degraded) =>
        _projectionLatencyMs.Record(elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>("operation", operation),
            new KeyValuePair<string, object?>("outcome", degraded ? "degraded" : "error"));

    public void ProjectionConflict(string operation, TimeSpan elapsed) =>
        _projectionLatencyMs.Record(elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>("operation", operation),
            new KeyValuePair<string, object?>("outcome", "content_conflict"));

    private static string SectionOutcomeCode(SectionOutcome outcome) => outcome switch
    {
        SectionOutcome.Applied => "applied",
        SectionOutcome.Stale => "stale",
        SectionOutcome.StaleSession => "stale_session",
        SectionOutcome.Skipped => "skipped",
        _ => "degraded",
    };
}
