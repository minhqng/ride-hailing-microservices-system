using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using RideHailing.Security;

namespace RideHailing.DriverService.Api;

/// <summary>
/// Driver-scoped location ingress limiter. A legitimate driver may send
/// frequent updates (120/minute is a generous ceiling); the partition key is
/// the authenticated JWT actor, never a device/body value.
/// </summary>
public static class DriverRateLimitPolicy
{
    public const string Location = "driver-location";
    public const int PermitLimit = 120;

    public static RateLimitPartition<string> LocationPartition(HttpContext context)
    {
        var key = RideAuthentication.TryActorId(context.User, out var actorId)
            ? "driver:" + actorId.ToString("D")
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
