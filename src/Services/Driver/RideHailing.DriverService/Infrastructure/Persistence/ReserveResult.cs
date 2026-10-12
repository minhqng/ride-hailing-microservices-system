// B-W06-01/02/03: OCC reserve outcome (Doc 01 §4.4 completeness rule).
// Every (state, request) classifies as exactly one case: the winner writes,
// a replay returns canonical success, anything else conflicts or misses.
// No `undefined`.
using RideHailing.DriverService.Domain.Drivers;

namespace RideHailing.DriverService.Infrastructure.Persistence;

public enum ReserveOutcome
{
    Won,
    Replayed,
    Conflict,
    DriverNotFound,
}

public sealed record ReserveResult(
    ReserveOutcome Outcome,
    Driver? Driver,
    string? ConflictCode,
    string? ConflictMessage)
{
    public static ReserveResult Won(Driver driver) =>
        new(ReserveOutcome.Won, driver, null, null);

    public static ReserveResult Replayed(Driver driver) =>
        new(ReserveOutcome.Replayed, driver, null, null);

    public static ReserveResult Conflict(string code, string message) =>
        new(ReserveOutcome.Conflict, null, code, message);

    public static ReserveResult NotFound() =>
        new(ReserveOutcome.DriverNotFound, null, null, null);
}
