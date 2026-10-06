// B-W05-01: candidate discovery over the W4 derived projection.
// Reads GEO (nearest-first, bounded) + projection hashes. Never authoritative:
// every hit is re-validated by eligibility and finally by Driver reserve (W6).
// Redis failure propagates as RedisConnectionException/Timeout for the caller
// to degrade (never silent empty, never fabricated candidates).
using System.Globalization;
using RideHailing.MatchingService.Application.Projection;
using RideHailing.MatchingService.Domain.Matching;
using StackExchange.Redis;

namespace RideHailing.MatchingService.Application.Matching;

public sealed class CandidateDiscovery
{
    private readonly Func<CancellationToken, Task<IDatabase>> _getDatabase;

    public CandidateDiscovery(Func<CancellationToken, Task<IDatabase>> getDatabase)
    {
        _getDatabase = getDatabase;
    }

    public async Task<IReadOnlyList<EnrichedCandidate>> DiscoverAsync(
        double longitude, double latitude, double radiusKm, int limit,
        CancellationToken ct = default)
    {
        // Raw GEOSEARCH (not the typed client): exact wire control independent
        // of client-library overload shapes. Nearest-first + bounded COUNT; the
        // application re-sorts explicitly and never trusts server order.
        var db = await _getDatabase(ct);
        var raw = await db.ExecuteAsync("GEOSEARCH",
            RedisProjectionKeys.GeoKey,
            "FROMLONLAT", longitude.ToString("R", CultureInfo.InvariantCulture),
            latitude.ToString("R", CultureInfo.InvariantCulture),
            "BYRADIUS", radiusKm.ToString("R", CultureInfo.InvariantCulture), "km",
            "ASC", "COUNT", limit,
            "WITHDIST");
        if (raw.IsNull)
        {
            throw new InvalidOperationException("GEOSEARCH must return an array.");
        }

        var hits = ((RedisResult[])raw)!;
        var enriched = new List<EnrichedCandidate>(hits.Length);
        foreach (var entry in hits)
        {
            var pair = ((RedisResult[])entry)!;
            if (pair.Length != 2)
            {
                continue;
            }

            if (!Guid.TryParse((string?)pair[0], out var driverId))
            {
                continue; // foreign member: not ours, never a candidate.
            }

            if (!double.TryParse((string?)pair[1], NumberStyles.Float,
                CultureInfo.InvariantCulture, out var distance))
            {
                continue; // malformed distance: eligibility fails it closed later.
            }

            var fields = await db.HashGetAllAsync(RedisProjectionKeys.ProjectionKey(driverId));
            var map = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var e in fields)
            {
                if ((string?)e.Name is not string name)
                {
                    continue;
                }

                map[name] = (string?)e.Value;
            }

            enriched.Add(new EnrichedCandidate(
                driverId,
                distance,
                map.GetValueOrDefault("derivedStatus") ?? string.Empty,
                int.TryParse(map.GetValueOrDefault("driverAggregateVersion"), out var v) ? v : -1,
                double.TryParse(map.GetValueOrDefault("longitude"), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var lon) ? lon : double.NaN,
                double.TryParse(map.GetValueOrDefault("latitude"), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var lat) ? lat : double.NaN,
                DateTimeOffset.TryParse(map.GetValueOrDefault("capturedAt"), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var ts) ? ts : DateTimeOffset.MinValue,
                Capability: null)); // W4 projection carries no vehicle data (documented W6-feed gap).
        }

        return enriched;
    }
}
