using RideHailing.TripService.Application.Trips.Lifecycle;
using RideHailing.TripService.Domain.Trips;
using RideHailing.TripService.Infrastructure.Persistence;
using RideHailing.TripService.Integration.DriverDirectory;
using RideHailing.TripService.Integration.Observability;

namespace RideHailing.TripService.Api;

/// <summary>
/// Public Driver-actor lifecycle transport. Thin adapters over the existing
/// production Trip handlers: no business rules live here. The authenticated
/// Driver principal is bound to the Driver-owned profile, and the aggregate
/// still checks the exact current assignment and reservation identity.
/// Reject is exposed with the DS03 terminal-rejection policy (Doc 01 §40.1);
/// it records a durable MATCH_FAILED/DRIVER_REJECTED result and exact cleanup
/// intents, never a claim that the remote release already happened.
/// </summary>
public static class DriverLifecycleEndpoint
{
    public static void MapDriverLifecycleEndpoints(this WebApplication app)
    {
        app.MapPost(TripRoutes.AcceptDriver, (
                Guid tripId,
                DriverLifecycleRequest request,
                HttpContext context,
                RecordDriverAcceptedHandler handler,
                IDriverDirectory directory,
                CancellationToken cancellationToken) =>
            HandleAsync("DriverAccept", tripId, request, context, directory,
                (driverId, reservationId, token) =>
                    handler.HandleAsync(tripId, driverId, reservationId, token),
                LifecycleResponse,
                cancellationToken));

        app.MapPost(TripRoutes.RejectDriver, (
                Guid tripId,
                DriverLifecycleRequest request,
                HttpContext context,
                RecordDriverRejectedHandler handler,
                IDriverDirectory directory,
                CancellationToken cancellationToken) =>
            HandleAsync("DriverReject", tripId, request, context, directory,
                (driverId, reservationId, token) =>
                    handler.HandleAsync(tripId, driverId, reservationId, token),
                result => Results.Ok(new DriverRejectResponse
                {
                    TripId = result.TripId,
                    State = result.State.ToCanonicalName(),
                    FailureReason = result.FailureReason?.ToCanonicalName(),
                    Replayed = result.IsReplay,
                    CleanupPending = result.PendingCleanupCount > 0,
                }),
                cancellationToken));

        app.MapPost(TripRoutes.StartTrip, (
                Guid tripId,
                DriverLifecycleRequest request,
                HttpContext context,
                StartTripHandler handler,
                IDriverDirectory directory,
                CancellationToken cancellationToken) =>
            HandleAsync("DriverStart", tripId, request, context, directory,
                (driverId, reservationId, token) =>
                    handler.HandleAsync(tripId, driverId, reservationId, token),
                LifecycleResponse,
                cancellationToken));

        app.MapPost(TripRoutes.CompleteTrip, (
                Guid tripId,
                DriverLifecycleRequest request,
                HttpContext context,
                CompleteTripHandler handler,
                IDriverDirectory directory,
                CancellationToken cancellationToken) =>
            HandleAsync("DriverComplete", tripId, request, context, directory,
                (driverId, reservationId, token) =>
                    handler.HandleAsync(tripId, driverId, reservationId, token),
                LifecycleResponse,
                cancellationToken));
    }

    private static IResult LifecycleResponse(TripLifecycleResult result) =>
        Results.Ok(new DriverLifecycleResponse
        {
            TripId = result.TripId,
            State = result.State.ToCanonicalName(),
            Replayed = result.IsReplay,
        });

    private static async Task<IResult> HandleAsync(
        string operation,
        Guid tripId,
        DriverLifecycleRequest request,
        HttpContext context,
        IDriverDirectory directory,
        Func<Guid, Guid, CancellationToken, Task<TripLifecycleResult>> invoke,
        Func<TripLifecycleResult, IResult> mapSuccess,
        CancellationToken cancellationToken)
    {
        using var span = TripDiagnostics.Activities.StartActivity("RideHailing.Trip/" + operation);
        span?.SetTag("ride.operation", operation);
        span?.SetTag("trip.id", tripId.ToString());

        if (!DriverIdentity.TryExtractActorId(context.User, out var actorId))
        {
            span?.SetTag("ride.outcome", "unauthorized");
            return Problem(StatusCodes.Status401Unauthorized, "UNAUTHORIZED",
                "A single authenticated Driver identity is required.");
        }

        if (!DriverIdentity.IsDriver(context.User))
        {
            span?.SetTag("ride.outcome", "forbidden");
            return Problem(StatusCodes.Status403Forbidden, "FORBIDDEN",
                "The Driver role is required.");
        }

        if (tripId == Guid.Empty || request.DriverId == Guid.Empty ||
            request.ReservationId == Guid.Empty)
        {
            span?.SetTag("ride.outcome", "validation_error");
            return Problem(StatusCodes.Status400BadRequest, "VALIDATION_ERROR",
                "tripId, driverId and reservationId are required.");
        }

        DriverLookup lookup;
        try
        {
            lookup = await directory.LookupAsync(
                request.DriverId, context.Request.Headers.Authorization.ToString(),
                cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            span?.SetTag("ride.outcome", "dependency_unavailable");
            return Problem(StatusCodes.Status503ServiceUnavailable, "DEPENDENCY_UNAVAILABLE",
                "The Driver directory is unavailable.");
        }

        switch (lookup.Status)
        {
            case DriverLookupStatus.NotFound:
                span?.SetTag("ride.outcome", "driver_not_found");
                return Problem(StatusCodes.Status404NotFound, "DRIVER_NOT_FOUND",
                    "The Driver profile was not found.");
            case DriverLookupStatus.Forbidden:
                span?.SetTag("ride.outcome", "forbidden");
                return Problem(StatusCodes.Status403Forbidden, "FORBIDDEN",
                    "The Driver profile is not readable by this principal.");
            case DriverLookupStatus.Unavailable:
                span?.SetTag("ride.outcome", "dependency_unavailable");
                return Problem(StatusCodes.Status503ServiceUnavailable, "DEPENDENCY_UNAVAILABLE",
                    "The Driver directory is unavailable.");
        }

        if (lookup.UserId != actorId)
        {
            span?.SetTag("ride.outcome", "forbidden");
            return Problem(StatusCodes.Status403Forbidden, "FORBIDDEN",
                "The Driver profile does not belong to the authenticated principal.");
        }

        TripLifecycleResult result;
        try
        {
            result = await invoke(request.DriverId, request.ReservationId, cancellationToken);
        }
        catch (TripConcurrencyConflictException)
        {
            span?.SetTag("ride.outcome", "concurrency_conflict");
            return Problem(StatusCodes.Status409Conflict, "CONCURRENCY_CONFLICT",
                "Trip changed concurrently; reload the current assignment before retrying.");
        }

        if (result.IsNotFound)
        {
            span?.SetTag("ride.outcome", "not_found");
            return Problem(StatusCodes.Status404NotFound, "NOT_FOUND", "The Trip was not found.");
        }

        if (result.IsSuccess)
        {
            span?.SetTag("ride.outcome", result.IsReplay ? "replay" : "applied");
            span?.SetTag("trip.state", result.State.ToCanonicalName());
            return mapSuccess(result);
        }

        span?.SetTag("ride.outcome", "rejected");
        span?.SetTag("trip.state", result.State.ToCanonicalName());
        var code = result.ErrorCode switch
        {
            TripErrorCode.TerminalState => "TERMINAL_STATE",
            TripErrorCode.AssignmentRequired => "VALIDATION_ERROR",
            TripErrorCode.AssignmentIdentityMismatch => "ASSIGNMENT_MISMATCH",
            TripErrorCode.StaleIdentity => "STALE_IDENTITY",
            TripErrorCode.PaymentQuoteRequired => "PAYMENT_QUOTE_REQUIRED",
            _ => "DOMAIN_CONFLICT",
        };
        var status = result.ErrorCode == TripErrorCode.AssignmentRequired
            ? StatusCodes.Status400BadRequest
            : StatusCodes.Status409Conflict;
        return Problem(status, code, result.Message);
    }

    private static IResult Problem(int statusCode, string code, string title) =>
        Results.Problem(
            statusCode: statusCode,
            title: title,
            extensions: new Dictionary<string, object?> { ["code"] = code });
}
