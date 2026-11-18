// Test-only workflow triggers for the real Matching process.
// The production service has no reserve/reconcile transport yet, so the
// process-kill proof needs a way to invoke the real orchestrator inside the
// real host. Mapped only under the explicit Testing environment with a
// configured control token; absent in Production.
using System.Security.Cryptography;
using System.Text;
using RideHailing.MatchingService.Application.Reserve;

namespace RideHailing.MatchingService.Api;

public static class MatchingTestEndpoints
{
    public const string ControlTokenHeader = "X-Fault-Control-Token";

    public static void Map(WebApplication app, string controlToken)
    {
        app.MapPost("/internal/test/matching/reserve", async (
            MatchingReserveTestRequest request,
            MatchingReserveOrchestrator orchestrator,
            HttpRequest httpRequest,
            CancellationToken ct) =>
        {
            if (!Authorized(httpRequest, controlToken))
            {
                return Results.NotFound();
            }

            if (request.TripId == Guid.Empty
                || request.MatchingRound < 1
                || request.DriverIds is null
                || request.DriverIds.Length == 0
                || request.DriverIds.Any(id => id == Guid.Empty))
            {
                return Results.BadRequest(new { code = "INVALID_REQUEST" });
            }

            var inputMessageId = string.IsNullOrWhiteSpace(request.InputMessageId)
                ? Guid.NewGuid().ToString("D")
                : request.InputMessageId;

            var result = await orchestrator.ExecuteRoundAsync(
                request.TripId, request.MatchingRound, request.DriverIds,
                inputMessageId, ct);

            return Results.Ok(new
            {
                outcome = result.Outcome.ToString(),
                driverId = result.DriverId,
                reservationId = result.ReservationId,
                code = result.Code,
                resultMessageId = result.ResultMessageId,
            });
        });

        app.MapPost(
            "/internal/test/matching/attempts/{tripId:guid}/{matchingRound:int}/{ordinal:int}/reconcile",
            async (
                Guid tripId,
                int matchingRound,
                int ordinal,
                MatchingReserveOrchestrator orchestrator,
                HttpRequest httpRequest,
                CancellationToken ct) =>
            {
                if (!Authorized(httpRequest, controlToken))
                {
                    return Results.NotFound();
                }

                if (tripId == Guid.Empty || matchingRound < 1 || ordinal < 0)
                {
                    return Results.BadRequest(new { code = "INVALID_REQUEST" });
                }

                var result = await orchestrator.ReconcileAsync(
                    tripId, matchingRound, ordinal, ct);

                return Results.Ok(new
                {
                    outcome = result.Outcome.ToString(),
                    reservationId = result.ReservationId,
                    code = result.Code,
                });
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

    public sealed record MatchingReserveTestRequest(
        Guid TripId, int MatchingRound, Guid[]? DriverIds, string? InputMessageId);
}
