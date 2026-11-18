// Driver Reject recovery: a reject/cancellation commits
// the release obligation before the Driver command is published. The normal
// completion path is the authoritative DriverReservationReleasedV1 event, but
// that event does not exist when the Driver already stopped owning the exact
// reservation (for example an independent exact-token release won the race
// before the Trip command arrived). This reconciler reads the Driver-owned
// reservation truth for each pending exact intent and completes the obligation
// only on conclusive "the exact reservation is no longer held" evidence.
//
// UNKNOWN, transport failure, a different live reservation, or ON_TRIP never
// complete an obligation. Redis and command dispatch success are irrelevant.

using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using RideHailing.TripService.Application.Trips.Lifecycle;
using RideHailing.TripService.Domain.Trips;
using RideHailing.TripService.Infrastructure.Persistence;

namespace RideHailing.TripService.Integration.Messaging;

public sealed class DriverCleanupReconciler(
    IServiceScopeFactory scopes, IHttpClientFactory clients,
    ILogger<DriverCleanupReconciler> log) : BackgroundService
{
    private static readonly HashSet<string> ConclusiveNotOwned = new(StringComparer.Ordinal)
    {
        "NOT_COMMITTED_OR_AVAILABLE",
        "RESERVED_BY_OTHER",
        "OFFLINE",
        "NOT_FOUND",
    };

    private int _offset;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ReconcileAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or
                Npgsql.NpgsqlException or TripConcurrencyConflictException or DbUpdateConcurrencyException or
                System.Text.Json.JsonException)
            {
                log.LogWarning("Driver reservation authority unavailable or changed concurrently; pending release retained.");
            }
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }

    internal async Task ReconcileAsync(CancellationToken token)
    {
        await using var scope = scopes.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<TripDbContext>();
        var trips = await database.Trips
            .Where(trip => trip.State == TripState.Cancelled || trip.State == TripState.MatchFailed)
            .Where(trip => trip.CleanupIntents.Any(intent =>
                intent.Kind == TripCleanupKind.ReleaseReservation && intent.CompletedAt == null))
            .OrderBy(trip => trip.UpdatedAt).ThenBy(trip => trip.TripId)
            .Skip(_offset).Take(16).ToListAsync(token);
        // Round-robin the bounded batch so a set of permanently unconfirmable
        // obligations cannot starve newer pending releases forever.
        _offset = trips.Count < 16 ? 0 : _offset + trips.Count;
        using var client = clients.CreateClient("driver-cleanup");
        foreach (var trip in trips)
        {
            foreach (var intent in trip.CleanupIntents.Where(intent =>
                intent.Kind == TripCleanupKind.ReleaseReservation && intent.CompletedAt is null))
            {
                using var response = await client.GetAsync(
                    $"internal/drivers/{intent.DriverId:D}/reservations/{intent.ReservationId:D}", token);
                if (!response.IsSuccessStatusCode) continue;
                var proof = await response.Content.ReadFromJsonAsync<ReservationTruth>(token);
                if (proof is null || proof.DriverId != intent.DriverId ||
                    !ConclusiveNotOwned.Contains(proof.Outcome)) continue;
                var result = await scope.ServiceProvider.GetRequiredService<ConfirmTripCleanupHandler>()
                    .HandleAsync(trip.TripId, intent.Key,
                        CleanupAuthorityResult.ExactReservationNoLongerOwned, token);
                if (!result.IsSuccess)
                    log.LogWarning("Driver cleanup confirmation conflicted; pending release retained.");
            }
        }
    }

    private sealed record ReservationTruth(Guid DriverId, string Outcome, int? Version);
}
