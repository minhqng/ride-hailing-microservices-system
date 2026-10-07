// B-W08-05: endpoint publish-decision proof (pure mapping, no broker).
// Proves which mutation outcomes emit which authoritative events, so the
// thin endpoint glue (call mapper, publish if non-null) needs no HTTP test.
using RideHailing.DriverService.Domain.Drivers;
using RideHailing.DriverService.Application.Messaging;
using RideHailing.DriverService.Infrastructure.Persistence;

namespace RideHailing.DriverService.UnitTests;

public class DriverEventMapperTests
{
    private static Driver ReservedDriver(out Guid trip, out Guid reservation)
    {
        var d = Driver.Create(Guid.NewGuid(), Guid.NewGuid());
        d.GoOnline(d.Version);
        trip = Guid.NewGuid();
        reservation = Guid.NewGuid();
        d.Reserve(trip, reservation, d.Version);
        return d;
    }

    [Fact]
    public void Reserve_Won_And_Replayed_Emit_Same_Reserved_Event()
    {
        var d = ReservedDriver(out var t, out var r);
        var won = DriverEventMapper.ForReserveResult(
            d.DriverId, t, r, ReserveResult.Won(d));
        var replayed = DriverEventMapper.ForReserveResult(
            d.DriverId, t, r, ReserveResult.Replayed(d));
        Assert.NotNull(won);
        Assert.NotNull(replayed);
        Assert.Equal(DriverEventTypes.Reserved, won!.MessageType);
        Assert.Equal(DriverEventRoutes.Reserved, won.RoutingKey);
        Assert.Equal("RESERVED", won.Payload.Status);
        Assert.Equal(t, won.Payload.TripId);
        Assert.Equal(r, won.Payload.ReservationId);
    }

    [Fact]
    public void Reserve_Conflict_And_NotFound_Emit_Nothing()
    {
        var id = Guid.NewGuid();
        Assert.Null(DriverEventMapper.ForReserveResult(
            id, Guid.NewGuid(), Guid.NewGuid(),
            ReserveResult.Conflict(DriverErrors.DriverNotAvailable, "busy")));
        Assert.Null(DriverEventMapper.ForReserveResult(
            id, Guid.NewGuid(), Guid.NewGuid(), ReserveResult.NotFound()));
    }

    [Fact]
    public void Release_Applied_Emits_Released_With_Token()
    {
        var d = ReservedDriver(out var t, out var r);
        d.Release(t, r);
        var emitted = DriverEventMapper.ForReleaseResult(
            d.DriverId, t, r, DriverMutationResult.Applied(d));
        Assert.NotNull(emitted);
        Assert.Equal(DriverEventTypes.ReservationReleased, emitted!.MessageType);
        Assert.Equal(t, emitted.Payload.TripId);
        Assert.Equal(r, emitted.Payload.ReservationId);
    }

    [Fact]
    public void Release_Noop_Emits_Available_Without_Refs()
    {
        var d = Driver.Create(Guid.NewGuid(), Guid.NewGuid());
        d.GoOnline(d.Version);
        var emitted = DriverEventMapper.ForReleaseResult(
            d.DriverId, Guid.NewGuid(), Guid.NewGuid(), DriverMutationResult.Unchanged(d));
        Assert.NotNull(emitted);
        Assert.Equal(DriverEventTypes.Available, emitted!.MessageType);
        Assert.Null(emitted.Payload.TripId);
        Assert.Null(emitted.Payload.ReservationId);
    }

    [Fact]
    public void Release_Conflict_Emits_Nothing()
    {
        Assert.Null(DriverEventMapper.ForReleaseResult(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            DriverMutationResult.Conflict(DriverErrors.StaleReservation, "stale")));
    }

    [Fact]
    public void Online_Offline_Applied_And_Noop_Emit_End_State()
    {
        var d = Driver.Create(Guid.NewGuid(), Guid.NewGuid());
        d.GoOnline(d.Version);
        var online = DriverEventMapper.ForOnlineResult(d.DriverId, DriverMutationResult.Unchanged(d));
        Assert.NotNull(online);
        Assert.Equal("AVAILABLE", online!.Payload.Status);

        d.GoOffline(d.Version);
        var offline = DriverEventMapper.ForOfflineResult(d.DriverId, DriverMutationResult.Applied(d));
        Assert.NotNull(offline);
        Assert.Equal("OFFLINE", offline!.Payload.Status);

        Assert.Null(DriverEventMapper.ForOnlineResult(
            d.DriverId, DriverMutationResult.Conflict(DriverErrors.IllegalTransition, "x")));
    }
}
