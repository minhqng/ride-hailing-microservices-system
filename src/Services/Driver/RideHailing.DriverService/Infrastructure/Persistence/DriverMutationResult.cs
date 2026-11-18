// Guarded Driver state-mutation outcomes.
// Same shape as the reserve result: the mutation either applies, is an
// idempotent no-op on already-holding state, conflicts, or misses the row.
using RideHailing.DriverService.Domain.Drivers;

namespace RideHailing.DriverService.Infrastructure.Persistence;

public enum DriverMutationOutcome
{
    Applied,
    Unchanged,
    Conflict,
    DriverNotFound,
}

public sealed record DriverMutationResult(
    DriverMutationOutcome Outcome,
    Driver? Driver,
    string? ConflictCode,
    string? ConflictMessage)
{
    public static DriverMutationResult Applied(Driver driver) =>
        new(DriverMutationOutcome.Applied, driver, null, null);

    public static DriverMutationResult Unchanged(Driver driver) =>
        new(DriverMutationOutcome.Unchanged, driver, null, null);

    public static DriverMutationResult Conflict(string code, string message) =>
        new(DriverMutationOutcome.Conflict, null, code, message);

    public static DriverMutationResult NotFound() =>
        new(DriverMutationOutcome.DriverNotFound, null, null, null);
}
