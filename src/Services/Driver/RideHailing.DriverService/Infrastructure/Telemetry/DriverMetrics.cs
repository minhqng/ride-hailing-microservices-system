// Driver authority instruments. These counters describe committed
// authoritative effects and definitive domain rejections only; transport and
// database failures are never classified as domain outcomes. Caller-observed
// reserve uncertainty belongs to Matching (matching_unknown_total), because
// Driver cannot infer a lost response from its own committed transaction.
using System.Diagnostics.Metrics;
using RideHailing.DriverService.Domain.Drivers;

namespace RideHailing.DriverService.Infrastructure.Telemetry;

public sealed class DriverMetrics
{
    public const string MeterName = DriverTelemetry.MeterName;

    private static readonly Meter Meter = new(MeterName);

    private readonly Counter<long> _reserveSuccess = Meter.CreateCounter<long>(
        "driver_reserve_success_total",
        description: "Committed NEW Driver reservation effects. Idempotent same-reservation replays are not effects.");

    private readonly Counter<long> _reserveConflict = Meter.CreateCounter<long>(
        "driver_reserve_conflict_total",
        description: "Definitive domain reserve rejections (conflict/not-found), never database or transport failure.");

    private readonly Counter<long> _staleReleaseBlocked = Meter.CreateCounter<long>(
        "driver_stale_release_blocked_total",
        description: "Release attempts blocked by current-reservation fencing. Idempotent no-ops are not counted.");

    public void ReserveSucceeded() => _reserveSuccess.Add(1);

    public void ReserveConflict(string reasonClass) =>
        _reserveConflict.Add(1, new KeyValuePair<string, object?>("reason_class", reasonClass));

    public void StaleReleaseBlocked(string reasonClass) =>
        _staleReleaseBlocked.Add(1, new KeyValuePair<string, object?>("reason_class", reasonClass));

    /// <summary>Bounded label mapping: never a raw exception message or identity.</summary>
    public static string ReserveReasonClass(string? conflictCode) => conflictCode switch
    {
        DriverErrors.DriverNotAvailable => "reservation_conflict",
        DriverErrors.StaleVersion => "version_fence",
        DriverErrors.StaleReservation => "stale_identity",
        DriverErrors.TripMismatch => "identity_mismatch",
        DriverErrors.IllegalTransition => "illegal_transition",
        DriverErrors.InvalidIdentity or DriverErrors.InvalidRequest => "invalid_identity",
        _ => "domain_conflict",
    };

    /// <summary>Bounded label mapping for fenced stale-release rejections.</summary>
    public static string ReleaseReasonClass(string? conflictCode) => conflictCode switch
    {
        DriverErrors.StaleReservation => "foreign_reservation",
        "RESERVATION_ROUND_MISMATCH" => "round_mismatch",
        _ => "domain_conflict",
    };

    /// <summary>
    /// Only exact-token/round fencing rejections are "stale release blocked".
    /// Provenance/legality refusals are not stale-release signals and must not
    /// inflate the conflict counter.
    /// </summary>
    public static bool IsStaleReleaseBlocked(string? conflictCode) =>
        conflictCode is DriverErrors.StaleReservation or "RESERVATION_ROUND_MISMATCH";
}
