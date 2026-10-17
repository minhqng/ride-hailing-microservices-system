// W08 hardening: reservation generation provenance (matchingRound lineage).
// Pure unit, no PG. The generation is write-once at the winning reserve,
// published as DriverReservedV1.matchingRound, cleared exactly with the
// reservation refs, never backfilled on replay and never fabricated.
using RideHailing.DriverService.Domain.Drivers;

namespace RideHailing.DriverService.UnitTests;

public class DriverReservationRoundTests
{
    private static Driver AvailableDriver()
    {
        var d = Driver.Create(Guid.NewGuid(), Guid.NewGuid());
        d.GoOnline(d.Version);
        return d;
    }

    [Fact]
    public void Reserve_Records_Caller_Generation_On_Win()
    {
        var d = AvailableDriver();
        d.Reserve(Guid.NewGuid(), Guid.NewGuid(), d.Version, 3);
        Assert.Equal(3, d.ReservationRound);
    }

    [Fact]
    public void Reserve_Without_Generation_Leaves_Provenance_Unknown()
    {
        // Deploy skew: a W08-era caller sends no round. Row stays RESERVED
        // with NULL provenance; the event will omit the field for Trip review.
        var d = AvailableDriver();
        d.Reserve(Guid.NewGuid(), Guid.NewGuid(), d.Version);
        Assert.Null(d.ReservationRound);
    }

    [Fact]
    public void Replay_Keeps_Recorded_Generation_Regardless_Of_Argument()
    {
        var d = AvailableDriver();
        d.Reserve(Guid.NewGuid(), Guid.NewGuid(), d.Version, 2);
        var versionAfterWin = d.Version;
        // Same-reservation replay (even with a bogus caller round) must not
        // flip published provenance: one logical reservation, one generation.
        // Precedence: the replay branch runs before the version fence.
        d.Reserve(d.ReservedTripId!.Value, d.ReservationId!.Value, versionAfterWin, 7);
        Assert.Equal(2, d.ReservationRound);
        Assert.Equal(versionAfterWin, d.Version);
    }

    [Fact]
    public void Release_Clears_Generation_With_Reservation_Identity()
    {
        var d = AvailableDriver();
        d.Reserve(Guid.NewGuid(), Guid.NewGuid(), d.Version, 5);
        d.Release(d.ReservedTripId!.Value, d.ReservationId!.Value);
        Assert.Equal(DriverStatus.Available, d.Status);
        Assert.Null(d.ReservationRound);
        Assert.Null(d.ReservationId);
    }

    [Fact]
    public void TripStart_Clears_Generation_With_Reservation_Identity()
    {
        var d = AvailableDriver();
        var t = Guid.NewGuid();
        var r = Guid.NewGuid();
        d.Reserve(t, r, d.Version, 5);
        d.MarkTripStarted(t, r);
        Assert.Equal(DriverStatus.OnTrip, d.Status);
        Assert.Null(d.ReservationRound);
        Assert.Null(d.ReservationId);
    }

    [Fact]
    public void NonPositive_Generation_Rejected_Before_Effect()
    {
        var d = AvailableDriver();
        var ex = Assert.Throws<DriverDomainException>(
            () => d.Reserve(Guid.NewGuid(), Guid.NewGuid(), d.Version, 0));
        Assert.Equal(DriverErrors.IllegalTransition, ex.Code);
        Assert.Equal(DriverStatus.Available, d.Status);
    }

    [Fact]
    public void Rehydrate_Allows_Legacy_Null_Generation_And_Rejects_NonPositive()
    {
        var legacy = Driver.Rehydrate(
            Guid.NewGuid(), Guid.NewGuid(), DriverStatus.Reserved, 4,
            Guid.NewGuid(), Guid.NewGuid(), null, null, null, DateTimeOffset.UtcNow);
        Assert.Null(legacy.ReservationRound);

        Assert.Throws<DriverDomainException>(() => Driver.Rehydrate(
            Guid.NewGuid(), Guid.NewGuid(), DriverStatus.Reserved, 4,
            Guid.NewGuid(), Guid.NewGuid(), 0, null, null, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Rehydrate_Rejects_Round_With_Cleared_Reservation_Identity()
    {
        // A round without its reservation identity is corrupt data: after a
        // release or trip start the generation cannot outlive the refs.
        Assert.Throws<DriverDomainException>(() => Driver.Rehydrate(
            Guid.NewGuid(), Guid.NewGuid(), DriverStatus.Available, 4,
            null, null, 3, null, null, DateTimeOffset.UtcNow));
    }
}
