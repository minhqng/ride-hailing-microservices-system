// POST /trips/{tripId}/cancel transport adapter (A-W05 cancellation matrix).
//
// THIN ADAPTER ONLY: resolves the authenticated Rider identity, invokes the
// rider-scoped CancelRideHandler, and maps the application outcome to HTTP.
// No assignment/round inspection, no cleanup orchestration and no policy
// invention live here: unfrozen CREATED/READY_TO_START cancellation stays a
// 409 with an explicit policy code, terminal rows stay 409 terminal, and the
// future cleanup requirement travels on the domain result for the workflow
// layer, not on this response.

using RideHailing.TripService.Application.Trips.Lifecycle;
using RideHailing.TripService.Domain.Trips;

namespace RideHailing.TripService.Api;

/// <summary>
/// Minimal-API registration and transport mapping for POST
/// /trips/{tripId:guid}/cancel.
/// </summary>
public static class CancelRideEndpoint
{
    /// <summary>Maps the frozen POST /trips/{tripId:guid}/cancel route.</summary>
    public static void MapCancelRideEndpoint(this WebApplication app)
    {
        app.MapPost(TripRoutes.CancelRide, HandleAsync);
    }

    private static async Task<IResult> HandleAsync(
        Guid tripId,
        HttpContext context,
        CancelRideHandler handler,
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
                title: "CancelRide requires the Rider role.",
                extensions: new Dictionary<string, object?> { ["code"] = "FORBIDDEN" });
        }

        var result = await handler
            .HandleAsync(tripId, riderId, cancellationToken)
            .ConfigureAwait(false);

        if (result.IsNotFound)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Trip was not found.",
                extensions: new Dictionary<string, object?> { ["code"] = "NOT_FOUND" });
        }

        if (result.IsSuccess)
        {
            return Results.Ok(new CancelRideResponse { TripId = result.TripId, Status = "CANCELLED" });
        }

        var code = result.ErrorCode switch
        {
            TripErrorCode.TerminalState => "TERMINAL_STATE",
            TripErrorCode.CancellationPolicyUnresolved => "CANCELLATION_POLICY_UNRESOLVED",
            TripErrorCode.IllegalTransition => "DOMAIN_CONFLICT",
            TripErrorCode.AssignmentRequired => "VALIDATION_ERROR",
            _ => "DOMAIN_CONFLICT",
        };

        var status = result.ErrorCode == TripErrorCode.AssignmentRequired
            ? StatusCodes.Status400BadRequest
            : StatusCodes.Status409Conflict;

        return Results.Problem(
            statusCode: status,
            title: result.Message,
            extensions: new Dictionary<string, object?> { ["code"] = code });
    }
}
