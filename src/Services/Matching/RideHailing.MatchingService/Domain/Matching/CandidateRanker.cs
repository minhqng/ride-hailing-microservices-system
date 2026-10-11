// B-W05-03/07: deterministic ranking (DSI-019).
// Total order, no floating-point equality traps for ORDERING purposes:
// score ASC, then driverId ASC (Guid.CompareTo is total and stable).
// NaN scores are rejected, not ordered (fail-closed: NaN input = bug).
using System.Globalization;

namespace RideHailing.MatchingService.Domain.Matching;

public static class CandidateRanker
{
    public static IReadOnlyList<RankedCandidate> Rank(
        IReadOnlyList<EligibleCandidate> candidates,
        MatchingPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(policy);

        var scored = new List<(EligibleCandidate Candidate, double Score)>(candidates.Count);
        foreach (var c in candidates)
        {
            if (!double.IsFinite(c.DistanceKm) || c.DistanceKm < 0)
            {
                throw new ArgumentException($"Non-finite/negative distance for {c.DriverId}.", nameof(candidates));
            }

            if (c.Age < TimeSpan.Zero)
            {
                throw new ArgumentException($"Negative age for {c.DriverId} (clock skew).", nameof(candidates));
            }

            var score = c.DistanceKm + c.Age.TotalSeconds * policy.FreshnessWeightKmPerSecond;
            if (!double.IsFinite(score))
            {
                throw new ArgumentException($"Non-finite score for {c.DriverId}.", nameof(candidates));
            }

            scored.Add((c, score));
        }

        var bounded = scored
            .OrderBy(x => x.Score)
            .ThenBy(x => x.Candidate.DriverId)
            .Take(Math.Max(0, policy.MaxCandidates))
            .Select((x, i) => new RankedCandidate(
                i, x.Candidate.DriverId, x.Candidate.DistanceKm, x.Score,
                x.Candidate.DriverAggregateVersion, x.Candidate.CapturedAt))
            .ToList();
        return bounded;
    }

    /// <summary>
    /// Deterministic snapshot hash (DSI-019 replay key): SHA-256 over the full
    /// ordering determinants — config version, rank instant, and per entry the
    /// order, identity, distance, score, version and captured timestamp.
    /// Same (snapshot, config, rank instant) ⇒ same hash, always. A different
    /// instant or any determinant change ⇒ different hash (no silent collision).
    /// All timestamps must be UTC ISO-8601 "O" (fixed-width, Z-suffixed), the
    /// sole writer format, so lexicographic compare is a time order.
    /// </summary>
    public static string SnapshotHash(
        IReadOnlyList<SnapshotEntry> entries,
        string rankingConfigVersion,
        DateTimeOffset rankedAt)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var sb = new System.Text.StringBuilder();
        sb.Append(rankingConfigVersion).Append('|')
            .Append(rankedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)).Append('|');
        foreach (var c in entries)
        {
            sb.Append(c.Ordinal).Append(':')
                .Append(c.DriverId.ToString("N")).Append(':')
                .Append(c.ReservationId.ToString("N")).Append(':')
                .Append(c.DistanceKm.ToString("R", CultureInfo.InvariantCulture)).Append(':')
                .Append(c.Score.ToString("R", CultureInfo.InvariantCulture)).Append(':')
                .Append(c.Version).Append(':')
                .Append(c.CapturedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)).Append(';');
        }

        var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
