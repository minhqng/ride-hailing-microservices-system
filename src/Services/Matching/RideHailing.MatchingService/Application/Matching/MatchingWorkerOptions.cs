// B-REC-01 worker policy. Bounded operational tuning, validated at startup;
// no business semantics live here.
namespace RideHailing.MatchingService.Application.Matching;

public sealed class MatchingWorkerOptions
{
    public const string SectionName = "Matching:Worker";

    public bool Enabled { get; set; } = true;

    /// <summary>Pause after each discovery pass (no tight retry loop).</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    public int BatchSize { get; set; } = 16;

    public int MaxConcurrentRounds { get; set; } = 4;

    /// <summary>Minimum age before a checked round is reconsidered.</summary>
    public TimeSpan ReconcileBackoff { get; set; } = TimeSpan.FromSeconds(2);

    public string? Validate()
    {
        if (PollInterval <= TimeSpan.Zero || PollInterval > TimeSpan.FromMinutes(1))
        {
            return "Matching:Worker:PollInterval must be within (0, 1m].";
        }

        if (BatchSize is < 1 or > 512)
        {
            return "Matching:Worker:BatchSize must be within 1..512.";
        }

        if (MaxConcurrentRounds is < 1 or > 64)
        {
            return "Matching:Worker:MaxConcurrentRounds must be within 1..64.";
        }

        if (ReconcileBackoff < TimeSpan.Zero || ReconcileBackoff > TimeSpan.FromMinutes(5))
        {
            return "Matching:Worker:ReconcileBackoff must be within [0, 5m].";
        }

        return null;
    }
}
