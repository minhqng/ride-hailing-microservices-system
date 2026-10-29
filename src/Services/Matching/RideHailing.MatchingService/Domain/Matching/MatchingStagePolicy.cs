// DSI-021/022: progressive search stages S0 -> S1 -> S2 (Doc 01 §9).
// Radius/budget values are deployment configuration, never correctness
// constants. The stage names themselves are the canonical generations; the
// validator requires a contiguous S0..Sn sequence with strictly expanding
// radii so a round can only move forward into a larger candidate space.
namespace RideHailing.MatchingService.Domain.Matching;

public sealed class MatchingStagePolicy
{
    public string Name { get; set; } = "S0";

    public double SearchRadiusKm { get; set; } = 5.0;

    public int MaxCandidates { get; set; } = 20;
}
