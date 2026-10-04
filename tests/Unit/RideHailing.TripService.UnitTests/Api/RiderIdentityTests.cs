// Rider identity derivation proof (A-W04-01 transport boundary).
//
// Trip consumes an already-authenticated ClaimsPrincipal and derives the
// Rider actor from it; Trip never issues tokens or validates signatures
// (shared/C-owned). These tests pin the fail-closed consumer contract:
// NameIdentifier-first with "sub" fallback, agreement when both exist,
// Rider role via ClaimTypes.Role or the "role" short claim, tolerance of
// repeated identical Guids, and rejection of every ambiguous/malformed/
// unauthenticated shape with no identity.

using System.Security.Claims;
using RideHailing.TripService.Api;

namespace RideHailing.TripService.UnitTests.Api;

public sealed class RiderIdentityTests
{
    private static readonly Guid RiderA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid RiderB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static ClaimsPrincipal Principal(params Claim[] claims)
    {
        var identity = new ClaimsIdentity(claims, authenticationType: "test");
        return new ClaimsPrincipal(identity);
    }

    [Fact]
    public void NameIdentifier_Alone_Resolves()
    {
        var principal = Principal(new Claim(ClaimTypes.NameIdentifier, RiderA.ToString("D")));

        Assert.True(RiderIdentity.TryExtractRiderId(principal, out var riderId));
        Assert.Equal(RiderA, riderId);
    }

    [Fact]
    public void Sub_Fallback_Resolves_When_NameIdentifier_Absent()
    {
        var principal = Principal(new Claim("sub", RiderA.ToString("D")));

        Assert.True(RiderIdentity.TryExtractRiderId(principal, out var riderId));
        Assert.Equal(RiderA, riderId);
    }

    [Fact]
    public void Agreeing_NameIdentifier_And_Sub_Resolve()
    {
        var same = RiderA.ToString("D");
        var principal = Principal(
            new Claim(ClaimTypes.NameIdentifier, same),
            new Claim("sub", same));

        Assert.True(RiderIdentity.TryExtractRiderId(principal, out var riderId));
        Assert.Equal(RiderA, riderId);
    }

    [Fact]
    public void Conflicting_NameIdentifier_And_Sub_Fail_Closed()
    {
        var principal = Principal(
            new Claim(ClaimTypes.NameIdentifier, RiderA.ToString("D")),
            new Claim("sub", RiderB.ToString("D")));

        Assert.False(RiderIdentity.TryExtractRiderId(principal, out _));
    }

    [Fact]
    public void Malformed_Sub_Beside_Valid_NameIdentifier_Fails_Closed()
    {
        var principal = Principal(
            new Claim(ClaimTypes.NameIdentifier, RiderA.ToString("D")),
            new Claim("sub", "not-a-guid"));

        Assert.False(RiderIdentity.TryExtractRiderId(principal, out _));
    }

    [Fact]
    public void Empty_Guid_Claim_Fails_Closed()
    {
        var principal = Principal(new Claim(ClaimTypes.NameIdentifier, Guid.Empty.ToString("D")));

        Assert.False(RiderIdentity.TryExtractRiderId(principal, out _));
    }

    [Fact]
    public void Conflicting_Same_Type_Duplicates_Fail_Closed()
    {
        var principal = Principal(
            new Claim(ClaimTypes.NameIdentifier, RiderA.ToString("D")),
            new Claim(ClaimTypes.NameIdentifier, RiderB.ToString("D")));

        Assert.False(RiderIdentity.TryExtractRiderId(principal, out _));
    }

    [Fact]
    public void Repeated_Identical_Guids_Are_Tolerated()
    {
        var same = RiderA.ToString("D");
        var principal = Principal(
            new Claim(ClaimTypes.NameIdentifier, same),
            new Claim(ClaimTypes.NameIdentifier, same),
            new Claim("sub", same));

        Assert.True(RiderIdentity.TryExtractRiderId(principal, out var riderId));
        Assert.Equal(RiderA, riderId);
    }

    [Fact]
    public void Unauthenticated_Principal_Fails_Closed()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity());

        Assert.False(RiderIdentity.TryExtractRiderId(principal, out _));
    }

    [Fact]
    public void No_Identity_Claims_Fail_Closed()
    {
        var principal = Principal(new Claim(ClaimTypes.Role, "Rider"));

        Assert.False(RiderIdentity.TryExtractRiderId(principal, out _));
    }

    [Fact]
    public void Claims_From_Unauthenticated_Identities_Are_Ignored()
    {
        var authenticated = new ClaimsIdentity(authenticationType: "test");
        var unauthenticated = new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, RiderA.ToString("D")) });
        var principal = new ClaimsPrincipal(new[] { authenticated, unauthenticated });

        Assert.False(RiderIdentity.TryExtractRiderId(principal, out _));
    }

    [Fact]
    public void Authenticated_Identity_Resolves_Beside_Unauthenticated_Decoy()
    {
        var authenticated = new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, RiderA.ToString("D")) },
            authenticationType: "test");
        var unauthenticated = new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, RiderB.ToString("D")) });
        var principal = new ClaimsPrincipal(new[] { authenticated, unauthenticated });

        Assert.True(RiderIdentity.TryExtractRiderId(principal, out var riderId));
        Assert.Equal(RiderA, riderId);
    }

    [Theory]
    [InlineData("Rider", true)]
    [InlineData("rider", false)]
    [InlineData("Driver", false)]
    [InlineData("Admin", false)]
    public void ClaimTypes_Role_Membership_Is_Ordinal(string role, bool expected)
    {
        var principal = Principal(new Claim(ClaimTypes.Role, role));

        Assert.Equal(expected, RiderIdentity.IsRider(principal));
    }

    [Theory]
    [InlineData("Rider", true)]
    [InlineData("Driver", false)]
    public void Short_Role_Claim_Is_Honored(string role, bool expected)
    {
        var principal = Principal(new Claim("role", role));

        Assert.Equal(expected, RiderIdentity.IsRider(principal));
    }

    [Fact]
    public void Missing_Role_Is_Not_Rider()
    {
        var principal = Principal(new Claim(ClaimTypes.NameIdentifier, RiderA.ToString("D")));

        Assert.False(RiderIdentity.IsRider(principal));
    }

    [Fact]
    public void Role_From_Unauthenticated_Identity_Does_Not_Authorize()
    {
        var authenticated = new ClaimsIdentity(authenticationType: "test");
        var unauthenticated = new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.Role, "Rider") });
        var principal = new ClaimsPrincipal(new[] { authenticated, unauthenticated });

        Assert.False(RiderIdentity.IsRider(principal));
    }

    [Fact]
    public void Authenticated_Role_Honored_Beside_Unauthenticated_Decoy()
    {
        var authenticated = new ClaimsIdentity(
            new[] { new Claim("role", "Rider") },
            authenticationType: "test");
        var unauthenticated = new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.Role, "Driver") });
        var principal = new ClaimsPrincipal(new[] { authenticated, unauthenticated });

        Assert.True(RiderIdentity.IsRider(principal));
    }
}
