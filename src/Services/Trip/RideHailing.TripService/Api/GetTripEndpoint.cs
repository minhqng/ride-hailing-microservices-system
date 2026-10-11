// GET /trips/{tripId} transport adapter (A-W05 read seam).
//
// THIN ADAPTER ONLY: resolves the authenticated Rider identity, loads the
// owned Trip, and projects the authoritative business truth (canonical
// status, current matching round, coherent assignment reference) to the
// frozen GetTripResponse shape. Missing and not-owned Trips share 404
// without disclosure (SEC-03). The optional X-Correlation-Id header is
// diagnostic only: accepted, logged and echoed, never used as business
// identity. No fare/Saga/Outbox/Payment internals on this surface.

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using RideHailing.TripService.Domain.Trips;
using RideHailing.TripService.Infrastructure.Persistence;

namespace RideHailing.TripService.Api;

/// <summary>
/// Minimal-API registration and transport mapping for GET /trips/{tripId}.
/// </summary>
public static class GetTripEndpoint
{
    /// <summary>Maps the frozen GET /trips/{tripId:guid} route.</summary>
    public static void MapGetTripEndpoint(this WebApplication app)
    {
        app.MapGet(TripRoutes.GetTrip, HandleAsync);
    }

    private static async Task<IResult> HandleAsync(
        Guid tripId,
        HttpContext context,
        TripDbContext db,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var log = loggerFactory.CreateLogger("RideHailing.TripService.Api.GetTrip");

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
                title: "GetTrip requires the Rider role.",
                extensions: new Dictionary<string, object?> { ["code"] = "FORBIDDEN" });
        }

        var trip = await db.Trips
            .AsNoTracking()
            .SingleOrDefaultAsync(t => t.TripId == tripId, cancellationToken)
            .ConfigureAwait(false);

        if (trip is null || trip.RiderId != riderId)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Trip was not found.",
                extensions: new Dictionary<string, object?> { ["code"] = "NOT_FOUND" });
        }

        if (context.Request.Headers.TryGetValue(TripIdentityHeaders.CorrelationId, out StringValues correlation) &&
            !StringValues.IsNullOrEmpty(correlation))
        {
            context.Response.Headers[TripIdentityHeaders.CorrelationId] = correlation.ToString();
        }

        log.LogInformation(
            "GetTrip {TripId} state {State} round {MatchingRound}.",
            trip.TripId,
            trip.State.ToCanonicalName(),
            trip.CurrentMatchingRound);

        return Results.Ok(new GetTripResponse
        {
            TripId = trip.TripId,
            Status = trip.State.ToCanonicalName(),
            Pickup = new TripLocationDto { Latitude = trip.Pickup.Latitude, Longitude = trip.Pickup.Longitude },
            Dropoff = new TripLocationDto { Latitude = trip.Dropoff.Latitude, Longitude = trip.Dropoff.Longitude },
            MatchingRound = trip.CurrentMatchingRound,
            AssignedDriverId = trip.Assignment?.DriverId,
            ReservationId = trip.Assignment?.ReservationId,
        });
    }
}
