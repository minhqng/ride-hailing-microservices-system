using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using RideHailing.Security;

namespace RideHailing.IdentityService.Infrastructure;

public sealed class PrincipalIssuer : IDisposable
{
    private readonly RSA rsa = RSA.Create();
    private readonly IConfiguration configuration;
    private readonly SigningCredentials credentials;

    public PrincipalIssuer(IConfiguration configuration)
    {
        this.configuration = configuration;
        var encoded = configuration["Auth:PrivateKeyBase64"];
        if (string.IsNullOrWhiteSpace(encoded)) throw new InvalidOperationException("Identity requires Auth:PrivateKeyBase64.");
        rsa.ImportFromPem(Encoding.UTF8.GetString(Convert.FromBase64String(encoded)));
        if (rsa.KeySize < 2048) throw new InvalidOperationException("Identity requires an RSA key of at least 2048 bits.");
        using var publicKey = RSA.Create();
        publicKey.ImportFromPem(Encoding.UTF8.GetString(Convert.FromBase64String(configuration["Auth:PublicKeyBase64"]!)));
        if (!rsa.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(publicKey.ExportSubjectPublicKeyInfo()))
            throw new InvalidOperationException("Identity signing and validation keys must belong to the same key pair.");
        credentials = new SigningCredentials(new RsaSecurityKey(rsa), SecurityAlgorithms.RsaSha256);
    }

    public object ForUser(IdentityAccount account) => Issue(account.UserId.ToString("D"),
        [new Claim("role", account.Role)], 900, account.UserId, account.Role);

    public object? ForService(string clientId, string clientSecret)
    {
        string[] permissions = clientId switch
        {
            "matching" => ["matching:reserve"],
            "trip" => ["trip:release", "matching:manage"],
            _ => [],
        };
        var expected = configuration[$"Auth:ServiceClients:{clientId}:Secret"];
        if (permissions.Length == 0 || string.IsNullOrWhiteSpace(expected) || expected.Length < 32 ||
            !CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(expected)),
                SHA256.HashData(Encoding.UTF8.GetBytes(clientSecret)))) return null;
        var claims = new List<Claim> { new("role", "Service"), new("client_id", clientId) };
        claims.AddRange(permissions.Select(permission => new Claim("permission", permission)));
        return Issue($"service:{clientId}", claims, 120, null, "Service");
    }

    private object Issue(string subject, IEnumerable<Claim> claims, int lifetime, Guid? userId, string role)
    {
        var now = DateTime.UtcNow;
        var token = new JwtSecurityToken(
            configuration["Auth:Issuer"] ?? RideAuthentication.DefaultIssuer,
            configuration["Auth:Audience"] ?? RideAuthentication.DefaultAudience,
            claims.Concat([new Claim(JwtRegisteredClaimNames.Sub, subject),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
                new Claim(JwtRegisteredClaimNames.Iat, new DateTimeOffset(now).ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64)]),
            notBefore: now, expires: now.AddSeconds(lifetime), signingCredentials: credentials);
        return new { access_token = new JwtSecurityTokenHandler().WriteToken(token), token_type = "Bearer", expires_in = lifetime, userId, role };
    }

    public void Dispose() => rsa.Dispose();
}
