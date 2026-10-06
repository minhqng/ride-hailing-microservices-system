// B-W03-04: DSI-002/003 coherence + legal/illegal transition matrix (Doc 01 §4.4).
// Pure unit, no PG/Redis.
using RideHailing.DriverService.Domain.Drivers;

namespace RideHailing.DriverService.UnitTests;

public class DriverStateTests
{
    private static Driver NewDriver() => Driver.Create(Guid.NewGuid(), Guid.NewGuid());

    [Fact]
    public void Create_Starts_Offline_Coherent()
    {
        var d = NewDriver();
        Assert.Equal(DriverStatus.Offline, d.Status);
        Assert.Null(d.ReservedTripId);
        Assert.Null(d.ReservationId);
        Assert.Null(d.ActiveTripId);
    }

    [Fact]
    public void GoOnline_Offline_To_Available()
    {
        var d = NewDriver();
        var v0 = d.Version;
        d.GoOnline();
        Assert.Equal(DriverStatus.Available, d.Status);
        Assert.Equal(v0 + 1, d.Version);
    }

    [Fact]
    public void GoOnline_Retry_Is_Idempotent()
    {
        var d = NewDriver();
        d.GoOnline();
        var v = d.Version;
        d.GoOnline();
        Assert.Equal(DriverStatus.Available, d.Status);
        Assert.Equal(v, d.Version);
    }

    [Fact]
    public void GoOffline_Available_To_Offline()
    {
        var d = NewDriver();
        d.GoOnline();
        d.GoOffline();
        Assert.Equal(DriverStatus.Offline, d.Status);
    }

    [Fact]
    public void GoOffline_Retry_Is_Idempotent()
    {
        var d = NewDriver();
        d.GoOffline();
        Assert.Equal(DriverStatus.Offline, d.Status);
    }

    [Fact]
    public void GoOffline_While_Reserved_Is_Rejected()
    {
        var d = NewDriver();
        d.GoOnline();
        d.Reserve(Guid.NewGuid(), Guid.NewGuid());
        var ex = Assert.Throws<DriverDomainException>(() => d.GoOffline());
        Assert.Equal(DriverErrors.ReservationActive, ex.Code);
        Assert.Equal(DriverStatus.Reserved, d.Status); // unchanged
    }

    [Fact]
    public void Reserve_Requires_Available()
    {
        var d = NewDriver(); // OFFLINE
        var ex = Assert.Throws<DriverDomainException>(() => d.Reserve(Guid.NewGuid(), Guid.NewGuid()));
        Assert.Equal(DriverErrors.DriverNotAvailable, ex.Code);
    }

    [Fact]
    public void Reserve_Same_Reservation_Is_Idempotent()
    {
        var d = NewDriver();
        d.GoOnline();
        var t = Guid.NewGuid();
        var r = Guid.NewGuid();
        d.Reserve(t, r);
        var v = d.Version;
        d.Reserve(t, r);
        Assert.Equal(DriverStatus.Reserved, d.Status);
        Assert.Equal(v, d.Version);
        Assert.Equal(t, d.ReservedTripId);
        Assert.Equal(r, d.ReservationId);
    }

    [Fact]
    public void Reserve_Different_Reservation_Conflicts()
    {
        var d = NewDriver();
        d.GoOnline();
        d.Reserve(Guid.NewGuid(), Guid.NewGuid());
        var ex = Assert.Throws<DriverDomainException>(() => d.Reserve(Guid.NewGuid(), Guid.NewGuid()));
        Assert.Equal(DriverErrors.DriverNotAvailable, ex.Code);
    }

    [Fact]
    public void Release_Exact_Token_Returns_Available()
    {
        var d = NewDriver();
        d.GoOnline();
        var t = Guid.NewGuid();
        var r = Guid.NewGuid();
        d.Reserve(t, r);
        d.Release(t, r);
        Assert.Equal(DriverStatus.Available, d.Status);
        Assert.Null(d.ReservedTripId);
        Assert.Null(d.ReservationId);
    }

    [Fact]
    public void Release_Stale_Token_Does_Not_Mutate()
    {
        var d = NewDriver();
        d.GoOnline();
        var t = Guid.NewGuid();
        var r = Guid.NewGuid();
        d.Reserve(t, r);
        var ex = Assert.Throws<DriverDomainException>(() => d.Release(t, Guid.NewGuid()));
        Assert.Equal(DriverErrors.StaleReservation, ex.Code);
        Assert.Equal(DriverStatus.Reserved, d.Status);
        Assert.Equal(r, d.ReservationId); // R survives
    }

    [Fact]
    public void StartTrip_Exact_Identity_Goes_OnTrip()
    {
        var d = NewDriver();
        d.GoOnline();
        var t = Guid.NewGuid();
        var r = Guid.NewGuid();
        d.Reserve(t, r);
        d.MarkTripStarted(t, r);
        Assert.Equal(DriverStatus.OnTrip, d.Status);
        Assert.Equal(t, d.ActiveTripId);
    }

    [Fact]
    public void StartTrip_Stale_Identity_Rejected()
    {
        var d = NewDriver();
        d.GoOnline();
        d.Reserve(Guid.NewGuid(), Guid.NewGuid());
        var ex = Assert.Throws<DriverDomainException>(() => d.MarkTripStarted(Guid.NewGuid(), Guid.NewGuid()));
        Assert.Equal(DriverErrors.TripMismatch, ex.Code);
        Assert.Equal(DriverStatus.Reserved, d.Status);
    }

    [Fact]
    public void CompleteTrip_Current_Trip_Returns_Available()
    {
        var d = NewDriver();
        d.GoOnline();
        var t = Guid.NewGuid();
        var r = Guid.NewGuid();
        d.Reserve(t, r);
        d.MarkTripStarted(t, r);
        d.MarkTripCompleted(t);
        Assert.Equal(DriverStatus.Available, d.Status);
        Assert.Null(d.ActiveTripId);
    }

    [Fact]
    public void CompleteTrip_Wrong_Trip_Rejected()
    {
        var d = NewDriver();
        d.GoOnline();
        var t = Guid.NewGuid();
        d.Reserve(t, Guid.NewGuid());
        d.MarkTripStarted(t, d.ReservationId!.Value);
        var ex = Assert.Throws<DriverDomainException>(() => d.MarkTripCompleted(Guid.NewGuid()));
        Assert.Equal(DriverErrors.TripMismatch, ex.Code);
    }

    [Fact]
    public void Release_While_OnTrip_Rejected()
    {
        var d = NewDriver();
        d.GoOnline();
        var t = Guid.NewGuid();
        var r = Guid.NewGuid();
        d.Reserve(t, r);
        d.MarkTripStarted(t, r);
        Assert.Throws<DriverDomainException>(() => d.Release(t, r));
        Assert.Equal(DriverStatus.OnTrip, d.Status);
    }

    [Fact]
    public void Rehydrate_Rejects_Incoherent_State()
    {
        // AVAILABLE must not carry reservation refs (DSI-002).
        Assert.Throws<DriverDomainException>(() => Driver.Rehydrate(
            Guid.NewGuid(), Guid.NewGuid(), DriverStatus.Available, 1,
            Guid.NewGuid(), Guid.NewGuid(), null, null, DateTimeOffset.UtcNow));
    }
}
