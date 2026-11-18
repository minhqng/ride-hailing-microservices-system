// Test-only control surface for the fault seam. Mapped
// only in the explicit Testing environment with a configured token; it never
// exists in Production and cannot activate a fault (activation is process
// environment at startup).
using System.Security.Cryptography;
using System.Text;
using RideHailing.DriverService.Infrastructure.Faults;

namespace RideHailing.DriverService.Api;

public static class DriverFaultEndpoints
{
    public const string ControlTokenHeader = "X-Fault-Control-Token";

    public static void Map(WebApplication app, DriverFaultPoint faults, string controlToken)
    {
        app.MapGet("/internal/test/faults", (HttpRequest request) =>
            Authorized(request, controlToken)
                ? Results.Ok(faults.Snapshot())
                : Results.NotFound());

        app.MapPost("/internal/test/faults/{name}/release", (string name, HttpRequest request) =>
        {
            if (!Authorized(request, controlToken) || !faults.IsEnabledFor(name))
            {
                return Results.NotFound();
            }

            var released = faults.Release(name);
            return Results.Ok(new { released, snapshot = faults.Snapshot() });
        });
    }

    public static bool Authorized(HttpRequest request, string expectedToken)
    {
        if (string.IsNullOrWhiteSpace(expectedToken)
            || !request.Headers.TryGetValue(ControlTokenHeader, out var values))
        {
            return false;
        }

        var provided = values.ToString();
        if (provided.Length == 0)
        {
            return false;
        }

        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expectedToken));
        var providedHash = SHA256.HashData(Encoding.UTF8.GetBytes(provided));
        return CryptographicOperations.FixedTimeEquals(expectedHash, providedHash);
    }
}
