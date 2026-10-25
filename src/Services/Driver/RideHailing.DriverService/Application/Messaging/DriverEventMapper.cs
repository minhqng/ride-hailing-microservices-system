// B-W08-05: pure mapping from Driver mutation outcomes to canonical events.
// W9 split the two concerns:
//   - For*Effect methods map only a COMMITTED state change; the repository
//     records that event as an Outbox intent in the same transaction.
//   - For*Result methods preserve the W8 compatibility contract (win/applied
//     and idempotent replay/noop yield the same event for read-only consumers);
//     they are not used to create a second W9 intent for a no-op.
using RideHailing.DriverService.Domain.Drivers;
using RideHailing.DriverService.Infrastructure.Persistence;

namespace RideHailing.DriverService.Application.Messaging;

public sealed record DriverEventToPublish(
    string MessageType,
    string RoutingKey,
    DriverStateEventPayload Payload,
    int Version);

public static class DriverEventMapper
{
    public static DriverEventToPublish ForTripStartedEffect(Driver driver, Guid tripId, Guid reservationId) =>
        Event(driver.DriverId, DriverEventTypes.TripStarted, DriverEventRoutes.TripStarted,
            "ON_TRIP", driver.Version, tripId, reservationId);

    public static DriverEventToPublish ForTripCompletedEffect(Driver driver, Guid tripId, Guid reservationId) =>
        Event(driver.DriverId, DriverEventTypes.TripCompleted, DriverEventRoutes.TripCompleted,
            "AVAILABLE", driver.Version, tripId, reservationId);

    // Committed-effect mappings used by the persistence layer when it records
    // the durable Outbox intent in the SAME transaction as the Driver row.
    // Only an actually applied effect is passed here: an idempotent replay or
    // no-op has no new authoritative state, so W9 records no new event for it.
    public static DriverEventToPublish ForReservedEffect(
        Driver driver, Guid tripId, Guid reservationId) =>
        Event(
            driver.DriverId, DriverEventTypes.Reserved, DriverEventRoutes.Reserved,
            "RESERVED", driver.Version, tripId, reservationId, driver.ReservationRound);

    public static DriverEventToPublish ForReleasedEffect(
        Driver driver, Guid tripId, Guid reservationId) =>
        Event(
            driver.DriverId, DriverEventTypes.ReservationReleased, DriverEventRoutes.ReservationReleased,
            "AVAILABLE", driver.Version, tripId, reservationId);

    public static DriverEventToPublish ForAvailableEffect(Driver driver) =>
        Event(
            driver.DriverId, DriverEventTypes.Available, DriverEventRoutes.Available,
            "AVAILABLE", driver.Version, null, null);

    public static DriverEventToPublish ForOfflineEffect(Driver driver) =>
        Event(
            driver.DriverId, DriverEventTypes.Offline, DriverEventRoutes.Offline,
            "OFFLINE", driver.Version, null, null);

    // W8 outcome mapping retained for compatibility tests and read-only
    // consumers. W9 persistence uses the committed-effect methods above, so a
    // Replayed/Unchanged outcome never creates a second logical Outbox row.
    public static DriverEventToPublish? ForReserveResult(
        Guid driverId, Guid tripId, Guid reservationId, ReserveResult result) =>
        result.Outcome switch
        {
            ReserveOutcome.Won => ForReservedEffect(result.Driver!, tripId, reservationId),
            ReserveOutcome.Replayed => ForReservedEffect(result.Driver!, tripId, reservationId),
            _ => null,
        };

    public static DriverEventToPublish? ForReleaseResult(
        Guid driverId, Guid tripId, Guid reservationId, DriverMutationResult result) =>
        result.Outcome switch
        {
            DriverMutationOutcome.Applied => ForReleasedEffect(result.Driver!, tripId, reservationId),
            DriverMutationOutcome.Unchanged => ForAvailableEffect(result.Driver!),
            _ => null,
        };

    public static DriverEventToPublish? ForOnlineResult(Guid driverId, DriverMutationResult result) =>
        result.Outcome switch
        {
            DriverMutationOutcome.Applied or DriverMutationOutcome.Unchanged => ForAvailableEffect(result.Driver!),
            _ => null,
        };

    public static DriverEventToPublish? ForOfflineResult(Guid driverId, DriverMutationResult result) =>
        result.Outcome switch
        {
            DriverMutationOutcome.Applied or DriverMutationOutcome.Unchanged => ForOfflineEffect(result.Driver!),
            _ => null,
        };

    private static DriverEventToPublish Event(
        Guid driverId, string type, string route, string status, int version,
        Guid? tripId, Guid? reservationId, int? matchingRound = null) =>
        new(type, route,
            new DriverStateEventPayload(driverId, status, version, tripId, reservationId, matchingRound),
            version);
}
