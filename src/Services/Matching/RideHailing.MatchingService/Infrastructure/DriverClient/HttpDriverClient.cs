// Typed HttpClient implementation of the Driver seam.
// Mapping is total and explicit: every HTTP outcome becomes either a domain
// result or a propagated transport exception. No silent fallbacks
// (forbids assuming success when Driver is unavailable).
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
        // Driver reserve contract: POST /drivers/{id}/reserve with the
        // caller-observed version. Mapping below is unchanged:
        // 200 → Reserved, 409 → Rejected(code), 404 → Rejected(DRIVER_NOT_FOUND),
        // anything else throws for the caller to classify (UNKNOWN).
        using var response = await _http.PostAsJsonAsync(
            $"drivers/{request.DriverId:N}/reserve",
            new
            {
                tripId = request.TripId,
                reservationId = request.ReservationId,
                expectedVersion = request.ExpectedVersion,
                matchingRound = request.MatchingRound,
            },
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
            // L2: only the Driver domain's structured body is an authoritative
            // "no such driver". A bare 404 may be routing/proxy/transport and
            // carries no proof that the original reservation had no effect.
            if (await HasAuthoritativeDriverNotFoundAsync(response, cancellationToken))
            {
                return new DriverReserveResult(
                    DriverReserveOutcomeKind.Rejected, "DRIVER_NOT_FOUND", null);
            }

            throw new HttpRequestException(
                "Driver reserve 404 without authoritative DRIVER_NOT_FOUND provenance.");
        }

        // 5xx and anything unexpected: transport-level failure for the caller
        // to classify (UNKNOWN), never a business answer.
        throw new HttpRequestException(
            $"Driver reserve transport failure: {(int)response.StatusCode} {response.ReasonPhrase}.");
    }

    /// <summary>
    /// Reads the current Driver version (see <see cref="IDriverClient"/>).
    /// 404 means the driver is unknown (conclusive: start no attempt);
    /// anything else unexpected throws for the caller to classify.
    /// </summary>
    public async Task<DriverVersionRead?> TryGetDriverVersionAsync(
        Guid driverId,
        CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync(
            $"drivers/{driverId:N}",
            cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            // L2: absence precedes dispatch, so this answer can be conclusive
            // only when it is the authoritative Driver-domain 404. Any other
            // 404 is transport uncertainty and must keep the attempt UNKNOWN.
            if (await HasAuthoritativeDriverNotFoundAsync(response, cancellationToken))
            {
                return null;
            }

            throw new HttpRequestException(
                "Driver version read returned a 404 without authoritative DRIVER_NOT_FOUND provenance.");
        }

        response.EnsureSuccessStatusCode();

        using var doc = await System.Text.Json.JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        var root = doc.RootElement;
        return new DriverVersionRead(
            root.GetProperty("version").GetInt32(),
            root.GetProperty("status").GetString() ?? "UNKNOWN");
    }

    public async Task<ReservationReconcileResult> GetReservationAsync(
        Guid driverId,
        Guid reservationId,
        CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync(
            $"internal/drivers/{driverId:N}/reservations/{reservationId:N}",
            cancellationToken);

        // A BARE 404 carries no domain body, so it can never be a
        // conclusive NOT_FOUND. It may be a wrong BaseAddress, a proxy, or a
        // missing route — all transport uncertainty. Throw so the caller keeps
        // the attempt UNKNOWN and reconciles instead of advancing to the next
        // candidate (which could double-reserve). NOT_FOUND arrives ONLY as a
        // 200 body outcome below. Unknown outcome STRINGS still throw.
        response.EnsureSuccessStatusCode();

        var body = await response.Content
            .ReadFromJsonAsync<ReconcileBody>(cancellationToken);
        var outcome = (body?.Outcome) switch
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
        return new ReservationReconcileResult(outcome, body?.Version);
    }

    /// <summary>
    /// L2 provenance gate: a 404 is authoritative absence only when the
    /// Driver service itself emitted the stable DRIVER_NOT_FOUND domain body.
    /// Empty/HTML/other-code bodies stay indistinguishable from a proxy or
    /// routing 404 and are classified as transport uncertainty by callers.
    /// </summary>
    private static async Task<bool> HasAuthoritativeDriverNotFoundAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        string text;
        try
        {
            text = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(text);
            return document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                && document.RootElement.TryGetProperty("code", out var code)
                && code.ValueKind == System.Text.Json.JsonValueKind.String
                && string.Equals(code.GetString(), "DRIVER_NOT_FOUND", StringComparison.Ordinal);
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    private sealed record ReserveSuccessBody(int? Version);
    private sealed record ConflictBody(string? Code);
    private sealed record ReconcileBody(string? Outcome, int? Version);
}
