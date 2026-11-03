using System.Security.Claims;

namespace RideHailing.TripService.Api;

/// <summary>
/// Transport identity for the Driver-actor lifecycle routes. The actor id is
/// the Identity user id from the validated JWT; the Driver profile id is
/// resolved through the Driver-owned directory and must belong to that user.
/// </summary>
public static class DriverIdentity
{
    public const string DriverRole = "Driver";

    public static bool IsDriver(ClaimsPrincipal principal) =>
        principal.Identities.Where(identity => identity.IsAuthenticated)
            .SelectMany(identity => identity.FindAll(ClaimTypes.Role)
                .Concat(identity.FindAll(RiderIdentity.JwtRoleClaimType)))
            .Any(claim => string.Equals(claim.Value, DriverRole, StringComparison.Ordinal));

    /// <summary>
    /// The claim extraction is role-agnostic (it resolves the authenticated
    /// actor guid and rejects contradictory claims); the Driver role is
    /// checked separately by <see cref="IsDriver"/>.
    /// </summary>
    public static bool TryExtractActorId(ClaimsPrincipal principal, out Guid actorId) =>
        RiderIdentity.TryExtractRiderId(principal, out actorId);
}
