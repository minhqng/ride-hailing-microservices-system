using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace RideHailing.Security;

public static class RideAuthentication
{
    public const string DefaultIssuer = "ride-hailing-identity";
    public const string DefaultAudience = "ride-hailing-api";

    public static IServiceCollection AddRideAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        var encodedKey = configuration["Auth:PublicKeyBase64"];
        if (string.IsNullOrWhiteSpace(encodedKey))
            throw new InvalidOperationException("Auth:PublicKeyBase64 is required; run the documented auth initialization.");
        var rsa = RSA.Create();
        rsa.ImportFromPem(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encodedKey)));
        if (rsa.KeySize < 2048) throw new InvalidOperationException("Authentication requires an RSA key of at least 2048 bits.");
        services.AddSingleton(rsa);
        var key = new RsaSecurityKey(rsa);
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
        {
            options.MapInboundClaims = false;
            options.IncludeErrorDetails = false;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = key,
                RequireSignedTokens = true,
                ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                ValidateIssuer = true,
                ValidIssuer = configuration["Auth:Issuer"] ?? DefaultIssuer,
                ValidateAudience = true,
                ValidAudience = configuration["Auth:Audience"] ?? DefaultAudience,
                RequireExpirationTime = true,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromSeconds(15),
                NameClaimType = JwtRegisteredClaimNames.Sub,
                RoleClaimType = "role",
            };
        });
        services.AddAuthorization();
        return services;
    }

    public static bool HasServicePermission(ClaimsPrincipal principal, string client, string permission) =>
        principal.Identity?.IsAuthenticated == true && principal.IsInRole("Service") &&
        principal.FindFirst("client_id")?.Value == client && principal.HasClaim("permission", permission);

    public static bool TryActorId(ClaimsPrincipal principal, out Guid actorId)
    {
        actorId = Guid.Empty;
        var values = principal.FindAll(JwtRegisteredClaimNames.Sub).Select(c => c.Value).Distinct().ToArray();
        return principal.Identity?.IsAuthenticated == true && values.Length == 1 &&
            Guid.TryParse(values[0], out actorId) && actorId != Guid.Empty;
    }
}
