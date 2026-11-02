using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;

namespace RideHailing.Security;

public sealed class ServiceTokenHandler(IConfiguration configuration) : DelegatingHandler
{
    private readonly SemaphoreSlim refreshLock = new(1, 1);
    private string? token;
    private DateTimeOffset expiresAt;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var endpoint = configuration["Auth:TokenEndpoint"];
        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            await refreshLock.WaitAsync(cancellationToken);
            try
            {
                if (token is null || expiresAt <= DateTimeOffset.UtcNow.AddSeconds(15))
                {
                    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                    using var response = await client.PostAsJsonAsync(endpoint, new
                    {
                        clientId = configuration["Auth:ClientId"],
                        clientSecret = configuration["Auth:ClientSecret"],
                    }, cancellationToken);
                    response.EnsureSuccessStatusCode();
                    var grant = await response.Content.ReadFromJsonAsync<TokenGrant>(cancellationToken)
                        ?? throw new HttpRequestException("Identity returned an empty service grant.");
                    if (string.IsNullOrWhiteSpace(grant.AccessToken) || grant.ExpiresIn is < 1 or > 900)
                        throw new HttpRequestException("Identity returned an invalid service grant.");
                    token = grant.AccessToken;
                    expiresAt = DateTimeOffset.UtcNow.AddSeconds(grant.ExpiresIn);
                }
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
            finally { refreshLock.Release(); }
        }
        // An unconfigured client supplies no principal. The receiving service
        // still requires authentication; an HTTP failure remains UNKNOWN.
        return await base.SendAsync(request, cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) refreshLock.Dispose();
        base.Dispose(disposing);
    }

    private sealed record TokenGrant(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);
}
