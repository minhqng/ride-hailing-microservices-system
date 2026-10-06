// B-W05-09: pipeline metrics (05B §14.2: candidate_count, geo_query_duration,
// stale_filtered). Instruments live here; OTLP export wiring stays C-owned
// (PlatformBootstrap). Tests observe via MeterListener (real instruments).
using System.Diagnostics.Metrics;

namespace RideHailing.MatchingService.Application.Matching;

public sealed class MatchingMetrics
{
    public const string MeterName = "RideHailing.MatchingService";

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

    public void Discovered(int count) => _discovered.Add(count);

    public void Eligible(int count) => _eligible.Add(count);

    public void StaleFiltered(int count) => _staleFiltered.Add(count);

    public void Excluded(int count) => _excluded.Add(count);

    public void Degraded() => _degraded.Add(1);

    public void GeoQueryDuration(TimeSpan duration) => _geoQueryDurationMs.Record(duration.TotalMilliseconds);
}
