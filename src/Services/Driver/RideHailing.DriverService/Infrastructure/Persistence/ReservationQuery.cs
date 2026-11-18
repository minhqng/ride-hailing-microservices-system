// Read-only reservation truth for reconciliation.
// The seven canonical outcomes; the endpoint emits the first six as 200-body
// values (DEPENDENCY_UNAVAILABLE never comes from this query — transport
// failure surfaces as HTTP 503 and the CALLER maps it).
namespace RideHailing.DriverService.Infrastructure.Persistence;

public enum ReservationQueryOutcome
{
    OwnedByThisReservation,
    NotCommittedOrAvailable,
    ReservedByOther,
    Offline,
    OnTrip,
    NotFound,
}

public sealed record ReservationQuery(ReservationQueryOutcome Outcome, int? Version);
