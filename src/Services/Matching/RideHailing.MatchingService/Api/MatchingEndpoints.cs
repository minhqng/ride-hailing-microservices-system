// B-W05: internal matching trigger (test/demo seam, not the W8 broker path).
// W8 will invoke the same application entry with RideRequestedV1 content.
// Idempotent: same (tripId, matchingRound) replays the same ordered result
// without minting new attempt identities (DSI-018).
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
                // a domain conflict, not a system error (Doc 01 §22 DOMAIN_CONFLICT).
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
