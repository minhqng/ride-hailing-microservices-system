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

using System.Diagnostics;
using System.Security.Claims;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Primitives;
using RideHailing.TripService.Application.Trips.CreateRide;
using RideHailing.TripService.Domain.Trips;
using RideHailing.TripService.Integration.Messaging;
using RideHailing.TripService.Integration.Observability;

namespace RideHailing.TripService.Api;

/// <summary>
/// Minimal-API registration and transport mapping for POST /trips.
/// </summary>
public static class CreateRideEndpoint
{
    /// <summary>Maps the frozen POST /trips route.</summary>
    public static void MapTripEndpoints(this WebApplication app)
    {
        app.MapPost(TripRoutes.CreateRide, HandleAsync)
            .RequireRateLimiting(TripRateLimitPolicy.CreateRide);
    }

    private static async Task<IResult> HandleAsync(
        HttpContext context,
        CreateRideRequest? request,
        CreateRideHandler handler,
        TripRideRequestedIntake rideRequested,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var log = loggerFactory.CreateLogger("RideHailing.TripService.Api.CreateRide");

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

        // A-W12-01: one domain-decision span per logical CreateRide. Its
        // context becomes the durable traceparent of the RideRequested outbox
        // row (via Activity.Current at intent creation), so the later relay
        // publication and every downstream consumer stay children of this
        // decision across process boundaries and publication delay.
        using var span = TripDiagnostics.Activities.StartActivity("RideHailing.Trip/CreateRide");
        span?.SetTag("ride.operation", "CreateRide");

        var result = await handler
            .HandleAsync(new CreateRideCommand(riderId, pickup, dropoff, key), cancellationToken)
            .ConfigureAwait(false);

        if (result.IsSuccess)
        {
            span?.SetTag("trip.id", result.TripId.ToString());
            // W9: durable local intent replaces the W8 direct publish. Broker
            // availability is the relay's problem, not this request's.
            if (!await rideRequested.EnsureRideRequestedAsync(result.TripId, riderId, cancellationToken))
            {
                span?.SetTag("ride.outcome", "created_intent_pending");
                context.Response.Headers.RetryAfter = "5";
                return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "Ride saved; the matching intent is not yet durably committed. Retry with the same Idempotency-Key and body.",
                    extensions: new Dictionary<string, object?>
                    {
                        ["code"] = "MATCHING_INTENT_PENDING", ["tripId"] = result.TripId
                    });
            }

            // A-W12-02: the Trip row committed inside the handler, so a new
            // non-replay create is exactly one committed business creation.
            // Idempotent replays and create-race reconciliation never count.
            if (!result.IsReplay)
            {
                TripDiagnostics.RecordTripCreated();
                log.LogInformation("CreateRide committed trip {TripId}.", result.TripId);
            }

            span?.SetTag("ride.outcome", result.IsReplay ? "replay" : "created");
            var body = new CreateRideResponse { TripId = result.TripId };
            return result.IsReplay
                ? Results.Ok(body)
                : Results.Created($"/trips/{result.TripId:D}", body);
        }

        // Domain-conflict rejections only (A-W12-02): validation 400s are
        // input hygiene, not business-transition rejections.
        var rejection = result.Error switch
        {
            CreateRideError.ActiveTripConflict => TripDiagnostics.RejectionReasons.ActiveTripConflict,
            CreateRideError.IdempotencyConflict => TripDiagnostics.RejectionReasons.IdempotencyConflict,
            _ => null,
        };
        if (rejection is not null)
        {
            TripDiagnostics.RecordTransitionRejected(rejection);
            span?.SetTag("ride.rejection", rejection);
        }

        span?.SetTag("ride.outcome", "rejected");
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
