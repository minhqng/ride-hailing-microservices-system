// POST /trips transport adapter (W4).
//
// THIN ADAPTER ONLY: resolves the authenticated Rider identity, reads the
// opaque Idempotency-Key header, converts transport coordinates to domain
// TripLocation values, invokes CreateRideHandler, and maps the application
// outcome to HTTP. No fingerprinting, fare calculation, active-Trip checks or
// database access live here; those remain application responsibilities.
// Authentication itself (JWT issuance/validation) is a shared/C-owned
// concern: until shared middleware populates HttpContext.User, anonymous
// requests safely receive 401.

using System.Security.Claims;
using Microsoft.Extensions.Primitives;
using RideHailing.TripService.Application.Trips.CreateRide;
using RideHailing.TripService.Domain.Trips;

namespace RideHailing.TripService.Api;

/// <summary>
/// Minimal-API registration and transport mapping for POST /trips.
/// </summary>
public static class CreateRideEndpoint
{
    /// <summary>Maps the frozen POST /trips route.</summary>
    public static void MapTripEndpoints(this WebApplication app)
    {
        app.MapPost(TripRoutes.CreateRide, HandleAsync);
    }

    private static async Task<IResult> HandleAsync(
        HttpContext context,
        CreateRideRequest? request,
        CreateRideHandler handler,
        CancellationToken cancellationToken)
    {
        if (!RiderIdentity.TryExtractRiderId(context.User, out var riderId))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "Authentication required.",
                extensions: new Dictionary<string, object?> { ["code"] = "UNAUTHORIZED" });
        }

        if (!RiderIdentity.IsRider(context.User))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "CreateRide requires the Rider role.",
                extensions: new Dictionary<string, object?> { ["code"] = "FORBIDDEN" });
        }

        if (!TryExtractKey(context.Request.Headers, out var key))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "A single non-empty Idempotency-Key header (max 128 chars) is required.",
                extensions: new Dictionary<string, object?> { ["code"] = "VALIDATION_ERROR" });
        }

        TripLocation pickup;
        TripLocation dropoff;
        try
        {
            if (request?.Pickup is null || request?.Dropoff is null)
            {
                throw new ArgumentOutOfRangeException(nameof(request), "CreateRide requires pickup and dropoff coordinates.");
            }

            // Domain TripLocation remains the semantic validation source; the
            // adapter only converts and maps rejection to 400.
            pickup = new TripLocation(request.Pickup.Latitude, request.Pickup.Longitude);
            dropoff = new TripLocation(request.Dropoff.Latitude, request.Dropoff.Longitude);
        }
        catch (ArgumentOutOfRangeException)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Pickup/dropoff coordinates are invalid.",
                extensions: new Dictionary<string, object?> { ["code"] = "VALIDATION_ERROR" });
        }

        var result = await handler
            .HandleAsync(new CreateRideCommand(riderId, pickup, dropoff, key), cancellationToken)
            .ConfigureAwait(false);

        if (result.IsSuccess)
        {
            var body = new CreateRideResponse { TripId = result.TripId };
            return result.IsReplay
                ? Results.Ok(body)
                : Results.Created($"/trips/{result.TripId:D}", body);
        }

        return result.Error switch
        {
            CreateRideError.Validation => Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: result.ErrorMessage,
                extensions: new Dictionary<string, object?> { ["code"] = "VALIDATION_ERROR" }),
            CreateRideError.IdempotencyConflict => Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: result.ErrorMessage,
                extensions: new Dictionary<string, object?> { ["code"] = "IDEMPOTENCY_CONFLICT" }),
            CreateRideError.ActiveTripConflict => Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: result.ErrorMessage,
                extensions: new Dictionary<string, object?> { ["code"] = "DOMAIN_CONFLICT" }),
            _ => Results.Problem(
                statusCode: StatusCodes.Status500InternalServerError,
                title: "Unexpected CreateRide outcome.",
                extensions: new Dictionary<string, object?> { ["code"] = "UNKNOWN" }),
        };
    }

    private static bool TryExtractKey(IHeaderDictionary headers, out string key)
    {
        key = string.Empty;
        if (!headers.TryGetValue(TripIdentityHeaders.IdempotencyKey, out StringValues values) ||
            values.Count != 1)
        {
            return false;
        }

        var candidate = values.ToString();
        if (string.IsNullOrWhiteSpace(candidate) ||
            candidate.Length > CreateRideRequestFingerprint.MaxKeyLength)
        {
            return false;
        }

        // Opaque exact identity: no case/whitespace normalization.
        key = candidate;
        return true;
    }
}
