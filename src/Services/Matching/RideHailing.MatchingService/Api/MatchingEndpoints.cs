// Internal Matching control surface.
//
// - GET /internal/matching/rounds/{tripId}/{round}/cancellation is the
//   production read used by Trip's cleanup reconciler. The Program.cs
//   middleware requires a service principal with the matching:manage grant.
// - POST /internal/matching/rounds is a test/demo seam only. Production work
//   arrives as RideRequestedV1 and is executed by MatchingWorkDispatcher, so
//   the manual route is not mapped in Production (it remains available in
//   Development/Testing for the demo and evidence harnesses).
//
// Both are idempotent: the same (tripId, matchingRound) replays the same
// ordered result without minting new attempt identities.
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using RideHailing.MatchingService.Application.Matching;
using RideHailing.MatchingService.Domain.Matching;
using StackExchange.Redis;

namespace RideHailing.MatchingService.Api;

public static class MatchingEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/internal/matching/rounds/{tripId:guid}/{round:int}/cancellation", async (
            Guid tripId, int round,
            RideHailing.MatchingService.Infrastructure.Persistence.MatchingWorkRepository work,
            CancellationToken token) =>
        {
            if (tripId == Guid.Empty || round < 1) return Results.BadRequest();
            var stopped = await work.IsCancellationStoppedAsync(tripId, round, token);
            return Results.Ok(new { tripId, matchingRound = round, state = stopped ? "STOPPED" : "PENDING" });
        });

        if (app.Environment.IsProduction())
        {
            return;
        }

        app.MapPost("/internal/matching/rounds", async (
            StartRoundRequest req,
            MatchingRoundService rounds,
            IOptions<MatchingPolicy> policy,
            CancellationToken ct) =>
        {
            var request = new ComputeRoundRequest(
                req.TripId, req.MatchingRound,
                req.PickupLongitude, req.PickupLatitude);

            ComputeRoundResult result;
            try
            {
                result = await rounds.ComputeAsync(request, policy.Value, ct);
            }
            catch (Exception ex) when (ex is ArgumentException or ArgumentNullException)
            {
                return Results.BadRequest(new { code = "INVALID_REQUEST", message = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                // Terminal/cancelled/blocked round or exhausted ledger retry:
                // a domain conflict, not a system error (DOMAIN_CONFLICT).
                return Results.Conflict(new { code = "DOMAIN_CONFLICT", message = ex.Message });
            }
            catch (Exception ex) when (ex is Npgsql.NpgsqlException
                or RedisConnectionException
                or RedisTimeoutException
                or SocketException
                or TimeoutException)
            {
                // Dependency failure without a proven effect: 503 with a static
                // message (never exception text — no leakage). Cancellation
                // (OperationCanceledException) is deliberately NOT caught.
                return Results.Json(
                    new { code = "DEPENDENCY_UNAVAILABLE", message = "A backing dependency is unavailable." },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            return Results.Ok(new
            {
                tripId = result.TripId,
                matchingRound = result.MatchingRound,
                status = result.Status,
                degraded = result.Degraded,
                candidates = result.Candidates.Select(c => new
                {
                    ordinal = c.Ordinal,
                    driverId = c.DriverId,
                    reservationId = c.ReservationId,
                    distanceKm = c.DistanceKm,
                }),
                metrics = new
                {
                    discovered = result.Metrics.Discovered,
                    eligible = result.Metrics.Eligible,
                    staleFiltered = result.Metrics.StaleFiltered,
                    excluded = result.Metrics.Excluded,
                    geoQueryMs = result.Metrics.GeoQueryMs,
                },
            });
        });
    }

    public sealed record StartRoundRequest(
        Guid TripId,
        int MatchingRound,
        double PickupLongitude,
        double PickupLatitude);
}
