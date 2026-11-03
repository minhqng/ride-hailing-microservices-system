using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace RideHailing.TripService.Api;

/// <summary>
/// Rider-scoped Trip creation limiter. The partition key is the authenticated
/// JWT actor, never a client-supplied identity; the endpoint itself still
/// checks the Rider role and ownership.
/// </summary>
public static class TripRateLimitPolicy
{
    public const string CreateRide = "trip-create";
    public const int PermitLimit = 30;

    public static RateLimitPartition<string> CreateRidePartition(HttpContext context)
    {
        var key = RiderIdentity.TryExtractRiderId(context.User, out var riderId)
            ? "rider:" + riderId.ToString("D")
            : "anonymous";
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = PermitLimit,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true,
        });
    }
}
