// B-W08-05: pure mapping from Driver mutation outcomes to the events that
// must be published (unit-tested without a broker). Rules:
//   - win/applied effect → the event for the new state;
//   - idempotent replay/noop → the SAME event (deterministic identity makes
//     it converge downstream; also heals a prior publish loss);
//   - conflict/miss → nothing (no effect happened).
// The publisher sends whatever this returns; best-effort delivery stays in
// the publisher/endpoint layer, never in this mapping.
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
    public static DriverEventToPublish? ForReserveResult(
        Guid driverId, Guid tripId, Guid reservationId, ReserveResult result) =>
        result.Outcome switch
        {
            ReserveOutcome.Won => Event(
                driverId, DriverEventTypes.Reserved, DriverEventRoutes.Reserved,
                "RESERVED", result.Driver!.Version, tripId, reservationId),
            ReserveOutcome.Replayed => Event(
                driverId, DriverEventTypes.Reserved, DriverEventRoutes.Reserved,
                "RESERVED", result.Driver!.Version, tripId, reservationId),
            _ => null,
        };

    public static DriverEventToPublish? ForReleaseResult(
        Guid driverId, Guid tripId, Guid reservationId, DriverMutationResult result) =>
        result.Outcome switch
        {
            DriverMutationOutcome.Applied => Event(
                driverId,
                DriverEventTypes.ReservationReleased, DriverEventRoutes.ReservationReleased,
                "AVAILABLE", result.Driver!.Version, tripId, reservationId),
            DriverMutationOutcome.Unchanged => Event(
                driverId, DriverEventTypes.Available, DriverEventRoutes.Available,
                "AVAILABLE", result.Driver!.Version, null, null),
            _ => null,
        };

    public static DriverEventToPublish? ForOnlineResult(Guid driverId, DriverMutationResult result) =>
        result.Outcome switch
        {
            DriverMutationOutcome.Applied or DriverMutationOutcome.Unchanged => Event(
                driverId, DriverEventTypes.Available, DriverEventRoutes.Available,
                "AVAILABLE", result.Driver!.Version, null, null),
            _ => null,
        };

    public static DriverEventToPublish? ForOfflineResult(Guid driverId, DriverMutationResult result) =>
        result.Outcome switch
        {
            DriverMutationOutcome.Applied or DriverMutationOutcome.Unchanged => Event(
                driverId, DriverEventTypes.Offline, DriverEventRoutes.Offline,
                "OFFLINE", result.Driver!.Version, null, null),
            _ => null,
        };

    private static DriverEventToPublish Event(
        Guid driverId, string type, string route, string status, int version,
        Guid? tripId, Guid? reservationId) =>
        new(type, route,
            new DriverStateEventPayload(driverId, status, version, tripId, reservationId),
            version);
}
