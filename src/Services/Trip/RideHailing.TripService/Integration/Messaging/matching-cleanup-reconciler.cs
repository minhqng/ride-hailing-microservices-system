using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using RideHailing.TripService.Application.Trips.Lifecycle;
using RideHailing.TripService.Domain.Trips;
using RideHailing.TripService.Infrastructure.Persistence;

namespace RideHailing.TripService.Integration.Messaging;

public sealed class MatchingCleanupReconciler(
    IServiceScopeFactory scopes, IHttpClientFactory clients,
    ILogger<MatchingCleanupReconciler> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ReconcileAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or
                Npgsql.NpgsqlException or TripConcurrencyConflictException or DbUpdateConcurrencyException or System.Text.Json.JsonException)
            {
                log.LogWarning("Matching cleanup authority unavailable or changed concurrently; pending work retained.");
            }
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }

    private async Task ReconcileAsync(CancellationToken token)
    {
        await using var scope = scopes.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<TripDbContext>();
        var trips = await database.Trips.Where(trip => trip.State == TripState.Cancelled || trip.State == TripState.MatchFailed)
            .Where(trip => trip.CleanupIntents.Any(intent => intent.Kind == TripCleanupKind.CancelMatching && intent.CompletedAt == null))
            .OrderBy(trip => trip.UpdatedAt).Take(16).ToListAsync(token);
        using var client = clients.CreateClient("matching-cleanup");
        foreach (var trip in trips)
        {
            foreach (var intent in trip.CleanupIntents.Where(intent => intent.Kind == TripCleanupKind.CancelMatching && intent.CompletedAt is null))
            {
                using var response = await client.GetAsync($"internal/matching/rounds/{trip.TripId:D}/{intent.MatchingRound}/cancellation", token);
                if (!response.IsSuccessStatusCode) continue;
                var proof = await response.Content.ReadFromJsonAsync<CancellationProof>(token);
                if (proof is null || proof.TripId != trip.TripId || proof.MatchingRound != intent.MatchingRound || proof.State != "STOPPED") continue;
                var result = await scope.ServiceProvider.GetRequiredService<ConfirmTripCleanupHandler>()
                    .HandleAsync(trip.TripId, intent.Key, CleanupAuthorityResult.MatchingStopped, token);
                if (!result.IsSuccess) log.LogWarning("Matching cleanup confirmation conflicted; pending work retained.");
            }
        }
    }

    private sealed record CancellationProof(Guid TripId, int MatchingRound, string State);
}
