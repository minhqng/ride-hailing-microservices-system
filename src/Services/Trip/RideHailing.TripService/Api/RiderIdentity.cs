// Rider identity extraction at the Trip transport boundary (W4).
//
// CONSUMER-COMPATIBILITY RULE ONLY. Trip consumes an already-authenticated
// ClaimsPrincipal and derives the Rider actor identity from it; Trip never
// issues tokens, validates signatures, or configures JWT infrastructure
// (shared/C-owned concerns). Supported identity representations, in order:
// ClaimTypes.NameIdentifier first, "sub" fallback. If both exist they must
// agree or the request fails closed with no identity. Role membership
// accepts ClaimTypes.Role or "jwt role claim" values equal to "Rider".
// Same-type duplicates resolve by distinct value: repeated identical Guids
// are tolerated (claim mapping layers may duplicate), conflicting Guids or
// any malformed/Guid.Empty value fails closed. A malformed secondary claim
// is never ignored because another valid claim exists. Only claims from
// authenticated identities are considered; unauthenticated identities in a
// multi-identity principal contribute no identity.

using System.Security.Claims;

namespace RideHailing.TripService.Api;

/// <summary>
/// Trip-local derivation of the authenticated Rider actor from an
/// already-populated <see cref="ClaimsPrincipal"/>.
/// </summary>
public static class RiderIdentity
{
    /// <summary>Canonical Rider role name.</summary>
    public const string RiderRole = "Rider";

    /// <summary>JWT subject claim, accepted as a fallback identity representation.</summary>
    public const string SubjectClaimType = "sub";

    /// <summary>JWT short role claim, accepted alongside <see cref="ClaimTypes.Role"/>.</summary>
    public const string JwtRoleClaimType = "role";

    /// <summary>
    /// Tries to derive the Rider identity. Returns false when the principal is
    /// unauthenticated, carries no usable identity claim, carries an invalid
    /// identity value, or carries contradictory identity claims. Each identity
    /// claim type must resolve to exactly one distinct non-empty Guid; repeated
    /// identical values are tolerated, conflicting or malformed values fail closed.
    /// </summary>
    public static bool TryExtractRiderId(ClaimsPrincipal principal, out Guid riderId)
    {
        riderId = Guid.Empty;
        if (principal.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        if (!TryResolveUniqueGuidClaim(principal, ClaimTypes.NameIdentifier, out var nameIdValue))
        {
            return false;
        }

        if (!TryResolveUniqueGuidClaim(principal, SubjectClaimType, out var subjectValue))
        {
            return false;
        }

        if (nameIdValue is null && subjectValue is null)
        {
            return false;
        }

        if (nameIdValue.HasValue && subjectValue.HasValue && nameIdValue.Value != subjectValue.Value)
        {
            return false;
        }

        riderId = nameIdValue ?? subjectValue ?? Guid.Empty;
        return riderId != Guid.Empty;
    }

    /// <summary>
    /// True when the authenticated principal carries the canonical Rider role.
    /// Like identity extraction, only roles from authenticated identities
    /// count: a role carried by an unauthenticated identity never authorizes.
    /// </summary>
    public static bool IsRider(ClaimsPrincipal principal) =>
        principal.Identities.Where(i => i.IsAuthenticated)
            .SelectMany(i => i.FindAll(ClaimTypes.Role).Concat(i.FindAll(JwtRoleClaimType)))
            .Any(c => string.Equals(c.Value, RiderRole, StringComparison.Ordinal));

    /// <summary>
    /// Resolves one identity claim type to a single unambiguous Rider Guid.
    /// Returns true with a null value when the type is absent; true with a value
    /// when every present claim parses to the same non-empty Guid; false when any
    /// value is malformed/Guid.Empty or distinct values conflict.
    /// Only claims from authenticated identities are considered: a claim
    /// carried by an unauthenticated identity must never authenticate a request.
    /// </summary>
    private static bool TryResolveUniqueGuidClaim(
        ClaimsPrincipal principal,
        string claimType,
        out Guid? riderId)
    {
        riderId = null;
        var distinct = new HashSet<Guid>();
        var present = false;

        foreach (var identity in principal.Identities.Where(i => i.IsAuthenticated))
        {
            foreach (var claim in identity.FindAll(claimType))
            {
                present = true;
                if (!Guid.TryParse(claim.Value, out var parsed) || parsed == Guid.Empty)
                {
                    riderId = null;
                    return false;
                }

                distinct.Add(parsed);
                if (distinct.Count > 1)
                {
                    riderId = null;
                    return false;
                }
            }
        }

        if (!present)
        {
            return true;
        }

        riderId = distinct.Single();
        return true;
    }
}
