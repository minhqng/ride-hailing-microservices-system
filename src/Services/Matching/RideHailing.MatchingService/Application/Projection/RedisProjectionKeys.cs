// B-W04-01: projection key layout (05B section 5). Matching-owned derived state.
namespace RideHailing.MatchingService.Application.Projection;

public static class RedisProjectionKeys
{
    public const string GeoKey = "drivers:geo";

    public static string ProjectionKey(Guid driverId) => $"driver:projection:{driverId:N}";

    public static string Member(Guid driverId) => driverId.ToString("N");
}
