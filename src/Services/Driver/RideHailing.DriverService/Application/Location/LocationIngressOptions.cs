// B-W03-05 (P1 review fix): freshness/accuracy windows are CONFIGURATION-owned
// policy (Doc 01 §5.3: "not hard-coded universal truth"), not code constants.
// Frozen W3 baseline values are the defaults; ops/config may tighten or relax
// them without a code change. Coordinate bounds stay in code: they are the
// coordinate invariant (-90/90, -180/180), not policy.
namespace RideHailing.DriverService.Application.Location;

public sealed class LocationIngressOptions
{
    public TimeSpan MaxFutureSkew { get; set; } = TimeSpan.FromMinutes(5);

    public TimeSpan MaxPastAge { get; set; } = TimeSpan.FromHours(24);

    public static LocationIngressOptions FrozenW3Baseline() => new();
}
