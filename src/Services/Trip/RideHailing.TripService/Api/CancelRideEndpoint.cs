// POST /trips/{tripId}/cancel transport adapter (A-W05 cancellation matrix).
//
// THIN ADAPTER ONLY: resolves the authenticated Rider identity, invokes the
// rider-scoped CancelRideHandler, and maps the application outcome to HTTP.
// No assignment/round inspection, no cleanup orchestration and no policy
// invention live here: the frozen pre-start policy allows cancellation from
// CREATED through READY_TO_START (READY_TO_START additionally commits the
// exact void leg in the handler). An already CANCELLED ride replays 200;
// other terminal states and IN_PROGRESS reject cancellation. Cleanup
// obligations are saved atomically with cancellation and are not exposed on
// this response.

using RideHailing.TripService.Application.Trips.Lifecycle;
using RideHailing.TripService.Domain.Trips;
using RideHailing.TripService.Infrastructure.Persistence;
using RideHailing.TripService.Integration.Observability;

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

        // A-W12-01: one domain-decision span per logical CancelRide. A cancel
        // stages cleanup intents in the same transaction, so this context
        // becomes the durable parent of the relayed release/cancel commands.
        using var span = TripDiagnostics.Activities.StartActivity("RideHailing.Trip/CancelRide");
        span?.SetTag("ride.operation", "CancelRide");
        span?.SetTag("trip.id", tripId.ToString());

        TripLifecycleResult result;
        try
        {
            result = await handler.HandleAsync(tripId, riderId, cancellationToken).ConfigureAwait(false);
        }
        catch (TripConcurrencyConflictException)
        {
            // Infrastructure race, not a domain rejection: no counter.
            span?.SetTag("ride.outcome", "concurrency_conflict");
            return Results.Problem(statusCode: StatusCodes.Status409Conflict,
                title: "Trip changed concurrently. Reload its current state before retrying cancellation.",
                extensions: new Dictionary<string, object?> { ["code"] = "CONCURRENCY_CONFLICT" });
        }

        if (result.IsNotFound)
        {
            span?.SetTag("ride.outcome", "not_found");
            return Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Trip was not found.",
                extensions: new Dictionary<string, object?> { ["code"] = "NOT_FOUND" });
        }

        if (result.IsSuccess)
        {
            // A-W12-02: one committed CANCELLED transition; the idempotent
            // already-CANCELLED replay never counts.
            if (!result.IsReplay)
                TripDiagnostics.RecordTripCancelled();
            span?.SetTag("ride.outcome", result.IsReplay ? "replay" : "cancelled");
            span?.SetTag("trip.state", result.State.ToCanonicalName());
            return Results.Ok(new CancelRideResponse { TripId = result.TripId, Status = "CANCELLED" });
        }

        // A-W12-02: domain rejections by bounded reason class.
        var rejection = result.ErrorCode switch
        {
            TripErrorCode.TerminalState => TripDiagnostics.RejectionReasons.TerminalState,
            TripErrorCode.IllegalTransition => TripDiagnostics.RejectionReasons.InvalidTransition,
            TripErrorCode.AssignmentRequired => TripDiagnostics.RejectionReasons.IdentityRequired,
            _ => TripDiagnostics.RejectionReasons.InvalidTransition,
        };
        TripDiagnostics.RecordTransitionRejected(rejection);
        span?.SetTag("ride.rejection", rejection);
        span?.SetTag("ride.outcome", "rejected");
        span?.SetTag("trip.state", result.State.ToCanonicalName());

        var code = result.ErrorCode switch
        {
            TripErrorCode.TerminalState => "TERMINAL_STATE",
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
