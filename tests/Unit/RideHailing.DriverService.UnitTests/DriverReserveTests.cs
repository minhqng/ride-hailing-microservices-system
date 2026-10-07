// B-W06-01/02/03: OCC fence precedence matrix (Doc 01 §1.4/§4.4, DSI-001/003/005).
// Pure unit, no PG.
using RideHailing.DriverService.Domain.Drivers;

namespace RideHailing.DriverService.UnitTests;

public class DriverReserveTests
{
    private static Driver AvailableDriver()
    {
        var d = Driver.Create(Guid.NewGuid(), Guid.NewGuid());
        d.GoOnline(d.Version);
        return d;
    }

    [Fact]
    public void Reserve_Current_Version_Wins_And_Bumps()
    {
        var d = AvailableDriver();
        var v0 = d.Version;
        var t = Guid.NewGuid();
        var r = Guid.NewGuid();
        d.Reserve(t, r, v0);
        Assert.Equal(DriverStatus.Reserved, d.Status);
        Assert.Equal(v0 + 1, d.Version);
        Assert.Equal(t, d.ReservedTripId);
        Assert.Equal(r, d.ReservationId);
    }

    [Fact]
    public void Reserve_Stale_Version_Different_Reservation_Is_Fenced()
    {
        var d = AvailableDriver();
        var stale = d.Version;
        // A newer authority act happened first (any mutation bumps version).
        d.AttachVehicle(Guid.NewGuid());
        var ex = Assert.Throws<DriverDomainException>(
            () => d.Reserve(Guid.NewGuid(), Guid.NewGuid(), stale));
        Assert.Equal(DriverErrors.StaleVersion, ex.Code);
        Assert.Equal(DriverStatus.Available, d.Status);
    }

    [Fact]
    public void Reserve_Stale_Version_After_Release_Is_Fenced()
    {
        // v0 AVAILABLE -> reserve (v+1 RESERVED) -> release (v+2 AVAILABLE).
        // A stale caller holding v0 must not re-reserve over newer authority.
        var d = AvailableDriver();
        var stale = d.Version;
        var t1 = Guid.NewGuid();
        var r1 = Guid.NewGuid();
        d.Reserve(t1, r1, stale);
        d.Release(t1, r1);
        Assert.Equal(DriverStatus.Available, d.Status);
        var ex = Assert.Throws<DriverDomainException>(
            () => d.Reserve(Guid.NewGuid(), Guid.NewGuid(), stale));
        Assert.Equal(DriverErrors.StaleVersion, ex.Code);
        Assert.Equal(DriverStatus.Available, d.Status);
        Assert.Null(d.ReservedTripId);
    }

    [Fact]
    public void Reserve_Stale_Version_Same_Reservation_Still_Replays()
    {
        // DSI-005 over DSI-003: the effect already exists, so the retry that
        // carries its original (now stale) version still returns canonical
        // success with no new effect.
        var d = AvailableDriver();
        var v0 = d.Version;
        var t = Guid.NewGuid();
        var r = Guid.NewGuid();
        d.Reserve(t, r, v0);
        var v = d.Version;
        d.Reserve(t, r, v0);
        Assert.Equal(DriverStatus.Reserved, d.Status);
        Assert.Equal(v, d.Version);
    }

    [Fact]
    public void Reserve_Current_Version_Different_Reservation_Conflicts()
    {
        var d = AvailableDriver();
        d.Reserve(Guid.NewGuid(), Guid.NewGuid(), d.Version);
        var ex = Assert.Throws<DriverDomainException>(
            () => d.Reserve(Guid.NewGuid(), Guid.NewGuid(), d.Version));
        Assert.Equal(DriverErrors.DriverNotAvailable, ex.Code);
    }

    [Theory]
    [InlineData(DriverStatus.Offline)]
    [InlineData(DriverStatus.OnTrip)]
    public void Reserve_NonReservable_State_Conflicts(DriverStatus _)
    {
        var d = Driver.Create(Guid.NewGuid(), Guid.NewGuid());
        if (_ == DriverStatus.OnTrip)
        {
            d.GoOnline(d.Version);
            var t = Guid.NewGuid();
            var r = Guid.NewGuid();
            d.Reserve(t, r, d.Version);
            d.MarkTripStarted(t, r);
        }

        var ex = Assert.Throws<DriverDomainException>(
            () => d.Reserve(Guid.NewGuid(), Guid.NewGuid(), d.Version));
        Assert.Equal(DriverErrors.DriverNotAvailable, ex.Code);
    }

    [Fact]
    public void Reserve_Empty_Identity_Rejected()
    {
        var d = AvailableDriver();
        var v = d.Version;
        Assert.Throws<DriverDomainException>(() => d.Reserve(Guid.Empty, Guid.NewGuid(), v));
        Assert.Throws<DriverDomainException>(() => d.Reserve(Guid.NewGuid(), Guid.Empty, v));
        Assert.Equal(DriverStatus.Available, d.Status);
    }

    [Fact]
    public void Reserve_NonPositive_ExpectedVersion_Rejected()
    {
        var d = AvailableDriver();
        Assert.Throws<DriverDomainException>(() => d.Reserve(Guid.NewGuid(), Guid.NewGuid(), 0));
        Assert.Throws<DriverDomainException>(() => d.Reserve(Guid.NewGuid(), Guid.NewGuid(), -3));
        Assert.Equal(DriverStatus.Available, d.Status);
    }

    [Fact]
    public void Reserve_Same_Identity_After_Release_Is_A_New_Effect()
    {
        // Boundary pin (W6 semantics; W7 owns R-freshness): once Release ends
        // the effect, the row is AVAILABLE with no memory of R. A fresh-version
        // re-reserve with the same (T,R) is a new effect and wins — the driver
        // layer cannot see reservation history. Cross-effect R-freshness
        // enforcement awaits the W7 attempt ledger (UNIQUE(reservation_id)).
        var d = AvailableDriver();
        var t = Guid.NewGuid();
        var r = Guid.NewGuid();
        d.Reserve(t, r, d.Version);
        d.Release(t, r);
        var v = d.Version;
        d.Reserve(t, r, v);
        Assert.Equal(DriverStatus.Reserved, d.Status);
        Assert.Equal(v + 1, d.Version);
        Assert.Equal(t, d.ReservedTripId);
        Assert.Equal(r, d.ReservationId);
    }
}
