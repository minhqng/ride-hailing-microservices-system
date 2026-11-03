using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace RideHailing.TripService.Integration.DriverDirectory;

public enum DriverLookupStatus
{
    Found,
    NotFound,
    Forbidden,
    Unavailable,
}

public sealed record DriverLookup(DriverLookupStatus Status, Guid UserId = default);

/// <summary>
/// Resolves the Driver-owned profile for a driver id so Trip can prove the
/// authenticated Driver principal owns that profile before mutating a Trip.
/// </summary>
public interface IDriverDirectory
{
    Task<DriverLookup> LookupAsync(Guid driverId, string? authorization, CancellationToken cancellationToken);
}

/// <summary>
/// Calls the Driver service with the caller's own token. The Driver access
/// guard enforces that the token owns the profile; Trip additionally compares
/// the returned owner id with the JWT actor id.
/// </summary>
public sealed class HttpDriverDirectory(HttpClient client) : IDriverDirectory
{
    public async Task<DriverLookup> LookupAsync(
        Guid driverId, string? authorization, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/drivers/{driverId:D}");
        if (!string.IsNullOrWhiteSpace(authorization))
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }

        using var response = await client.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new DriverLookup(DriverLookupStatus.NotFound);
        }

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return new DriverLookup(DriverLookupStatus.Forbidden);
        }

        if (!response.IsSuccessStatusCode)
        {
            return new DriverLookup(DriverLookupStatus.Unavailable);
        }

        DriverProfile? profile;
        try
        {
            profile = await response.Content.ReadFromJsonAsync<DriverProfile>(cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or NotSupportedException
                                          or System.Text.Json.JsonException)
        {
            return new DriverLookup(DriverLookupStatus.Unavailable);
        }

        return profile is null || profile.UserId == Guid.Empty
            ? new DriverLookup(DriverLookupStatus.Unavailable)
            : new DriverLookup(DriverLookupStatus.Found, profile.UserId);
    }

    private sealed record DriverProfile(Guid DriverId, Guid UserId);
}
