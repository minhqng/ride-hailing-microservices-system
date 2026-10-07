// B-W02-02: typed HttpClient implementation of the Driver seam.
// Mapping is total and explicit: every HTTP outcome becomes either a domain
// result or a propagated transport exception. No silent fallbacks
// (Doc 02 §19.6 forbids assuming success when Driver is unavailable).
using System.Net;
using System.Net.Http.Json;

namespace RideHailing.MatchingService.Infrastructure.DriverClient;

public sealed class HttpDriverClient : IDriverClient
{
    private readonly HttpClient _http;

    public HttpDriverClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<DriverReserveResult> ReserveAsync(
        DriverReserveRequest request,
        CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync(
            $"internal/drivers/{request.DriverId:N}/reservations",
            new { tripId = request.TripId, reservationId = request.ReservationId },
            cancellationToken);

        if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created)
        {
            var body = await response.Content
                .ReadFromJsonAsync<ReserveSuccessBody>(cancellationToken);
            return new DriverReserveResult(
                DriverReserveOutcomeKind.Reserved, null, body?.Version);
        }

        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            var body = await response.Content
                .ReadFromJsonAsync<ConflictBody>(cancellationToken);
            return new DriverReserveResult(
                DriverReserveOutcomeKind.Rejected,
                string.IsNullOrWhiteSpace(body?.Code) ? "DRIVER_NOT_AVAILABLE" : body.Code,
                null);
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new DriverReserveResult(
                DriverReserveOutcomeKind.Rejected, "DRIVER_NOT_FOUND", null);
        }

        // 5xx and anything unexpected: transport-level failure for the caller
        // to classify (UNKNOWN), never a business answer.
        throw new HttpRequestException(
            $"Driver reserve transport failure: {(int)response.StatusCode} {response.ReasonPhrase}.");
    }

    public async Task<ReservationReconcileOutcome> GetReservationAsync(
        Guid driverId,
        Guid reservationId,
        CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync(
            $"internal/drivers/{driverId:N}/reservations/{reservationId:N}",
            cancellationToken);

        // DSI-031: a BARE 404 carries no domain body, so it can never be a
        // conclusive NOT_FOUND. It may be a wrong BaseAddress, a proxy, or a
        // missing route — all transport uncertainty. Throw so the caller keeps
        // the attempt UNKNOWN and reconciles instead of advancing to the next
        // candidate (which could double-reserve). NOT_FOUND arrives ONLY as a
        // 200 body outcome below. Unknown outcome STRINGS still throw.
        response.EnsureSuccessStatusCode();

        var body = await response.Content
            .ReadFromJsonAsync<ReconcileBody>(cancellationToken);
        return (body?.Outcome) switch
        {
            "OWNED_BY_THIS_RESERVATION" => ReservationReconcileOutcome.OwnedByThisReservation,
            "NOT_COMMITTED_OR_AVAILABLE" => ReservationReconcileOutcome.NotCommittedOrAvailable,
            "RESERVED_BY_OTHER" => ReservationReconcileOutcome.ReservedByOther,
            "OFFLINE" => ReservationReconcileOutcome.Offline,
            "ON_TRIP" => ReservationReconcileOutcome.OnTrip,
            "NOT_FOUND" => ReservationReconcileOutcome.NotFound,
            _ => throw new InvalidOperationException(
                $"Unknown reconcile outcome '{body?.Outcome}': Driver contract drift."),
        };
    }

    private sealed record ReserveSuccessBody(int? Version);
    private sealed record ConflictBody(string? Code);
    private sealed record ReconcileBody(string? Outcome);
}
