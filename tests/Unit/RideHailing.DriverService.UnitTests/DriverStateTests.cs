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

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Create_Empty_Identity_Is_Invalid_Not_Conflict(bool emptyDriver, bool emptyUser)
    {
        // Unfixable input must classify 400 (INVALID_IDENTITY), never 409:
        // a conflict-retry on empty GUIDs could never succeed (Doc 01 §22).
        var ex = Assert.Throws<DriverDomainException>(() => Driver.Create(
            emptyDriver ? Guid.Empty : Guid.NewGuid(),
            emptyUser ? Guid.Empty : Guid.NewGuid()));
        Assert.Equal(DriverErrors.InvalidIdentity, ex.Code);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Rehydrate_Empty_Identity_Is_Rejected(bool emptyDriver, bool emptyUser)
    {
        var ex = Assert.Throws<DriverDomainException>(() => Driver.Rehydrate(
            emptyDriver ? Guid.Empty : Guid.NewGuid(),
            emptyUser ? Guid.Empty : Guid.NewGuid(),
            DriverStatus.Offline, 1, null, null, null, null, null, DateTimeOffset.UtcNow));
        Assert.Equal(DriverErrors.InvalidIdentity, ex.Code);
    }

    [Fact]
    public void Reserve_Empty_Identity_Is_Invalid_Not_Conflict()
    {
        var d = NewDriver();
        d.GoOnline(d.Version);
        var v = d.Version;
        var ex = Assert.Throws<DriverDomainException>(() => d.Reserve(Guid.Empty, Guid.NewGuid(), d.Version));
        Assert.Equal(DriverErrors.InvalidIdentity, ex.Code);
        Assert.Equal(DriverStatus.Available, d.Status);
        Assert.Equal(v, d.Version);
    }

    [Fact]
    public void Create_Both_Empty_Is_Invalid()
    {
        var ex = Assert.Throws<DriverDomainException>(() => Driver.Create(Guid.Empty, Guid.Empty));
        Assert.Equal(DriverErrors.InvalidIdentity, ex.Code);
    }

    [Fact]
    public void Reserve_Empty_Reservation_Is_Invalid()
    {
        var d = NewDriver();
        d.GoOnline(d.Version);
        var ex = Assert.Throws<DriverDomainException>(() => d.Reserve(Guid.NewGuid(), Guid.Empty, d.Version));
        Assert.Equal(DriverErrors.InvalidIdentity, ex.Code);
        Assert.Equal(DriverStatus.Available, d.Status);
    }

    [Theory]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, false, false, true)]
    public void Rehydrate_Empty_Optional_Ref_Is_Rejected(
        bool emptyTrip, bool emptyReservation, bool emptyActive, bool emptyVehicle)
    {
        var ex = Assert.Throws<DriverDomainException>(() => Driver.Rehydrate(
            Guid.NewGuid(), Guid.NewGuid(), DriverStatus.Reserved, 2,
            emptyTrip ? Guid.Empty : Guid.NewGuid(),
            emptyReservation ? Guid.Empty : Guid.NewGuid(),
            null,
            emptyActive ? Guid.Empty : null,
            emptyVehicle ? Guid.Empty : null,
            DateTimeOffset.UtcNow));
        Assert.Equal(DriverErrors.InvalidIdentity, ex.Code);
    }

    [Fact]
    public void Create_Empty_Vehicle_Is_Rejected()
    {
        var ex = Assert.Throws<DriverDomainException>(
            () => Driver.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.Empty));
        Assert.Equal(DriverErrors.InvalidIdentity, ex.Code);
    }

    [Fact]
    public void AttachVehicle_Empty_Is_Invalid()
    {
        var d = NewDriver();
        var ex = Assert.Throws<DriverDomainException>(() => d.AttachVehicle(Guid.Empty));
        Assert.Equal(DriverErrors.InvalidIdentity, ex.Code);
        Assert.Null(d.VehicleId);
    }

    [Fact]
    public void Release_Empty_Token_Is_Invalid_Without_Mutation()
    {
        var d = NewDriver();
        d.GoOnline(d.Version);
        var t = Guid.NewGuid();
        var r = Guid.NewGuid();
        d.Reserve(t, r, d.Version);
        var v = d.Version;
        var ex = Assert.Throws<DriverDomainException>(() => d.Release(Guid.Empty, Guid.Empty));
        Assert.Equal(DriverErrors.InvalidIdentity, ex.Code);
        Assert.Equal(DriverStatus.Reserved, d.Status);
        Assert.Equal(t, d.ReservedTripId);
        Assert.Equal(v, d.Version);
    }

    [Fact]
    public void Start_Empty_Token_Is_Invalid_Without_Mutation()
    {
        var d = NewDriver();
        d.GoOnline(d.Version);
        d.Reserve(Guid.NewGuid(), Guid.NewGuid(), d.Version);
        var ex = Assert.Throws<DriverDomainException>(() => d.MarkTripStarted(Guid.Empty, Guid.Empty));
        Assert.Equal(DriverErrors.InvalidIdentity, ex.Code);
        Assert.Equal(DriverStatus.Reserved, d.Status);
    }

    [Fact]
    public void Complete_Empty_Trip_Is_Invalid_Without_Mutation()
    {
        var d = NewDriver();
        d.GoOnline(d.Version);
        var t = Guid.NewGuid();
        var r = Guid.NewGuid();
        d.Reserve(t, r, d.Version);
        d.MarkTripStarted(t, r);
        var ex = Assert.Throws<DriverDomainException>(() => d.MarkTripCompleted(Guid.Empty));
        Assert.Equal(DriverErrors.InvalidIdentity, ex.Code);
        Assert.Equal(DriverStatus.OnTrip, d.Status);
        Assert.Equal(t, d.ActiveTripId);
    }

    [Fact]
    public void GoOnline_Offline_To_Available()
    {
        var d = NewDriver();
        var v0 = d.Version;
        d.GoOnline(d.Version);
        Assert.Equal(DriverStatus.Available, d.Status);
        Assert.Equal(v0 + 1, d.Version);
    }

    [Fact]
    public void GoOnline_Retry_Is_Idempotent()
    {
        var d = NewDriver();
        d.GoOnline(d.Version);
        var v = d.Version;
        d.GoOnline(d.Version);
        Assert.Equal(DriverStatus.Available, d.Status);
        Assert.Equal(v, d.Version);
    }

    [Fact]
    public void GoOffline_Available_To_Offline()
    {
        var d = NewDriver();
        d.GoOnline(d.Version);
        d.GoOffline(d.Version);
        Assert.Equal(DriverStatus.Offline, d.Status);
    }

    [Fact]
    public void GoOffline_Retry_Is_Idempotent()
    {
        var d = NewDriver();
        d.GoOffline(d.Version);
        Assert.Equal(DriverStatus.Offline, d.Status);
    }

    [Fact]
    public void GoOffline_While_Reserved_Is_Rejected()
    {
        var d = NewDriver();
        d.GoOnline(d.Version);
        d.Reserve(Guid.NewGuid(), Guid.NewGuid(), d.Version);
        var ex = Assert.Throws<DriverDomainException>(() => d.GoOffline(d.Version));
        Assert.Equal(DriverErrors.ReservationActive, ex.Code);
        Assert.Equal(DriverStatus.Reserved, d.Status); // unchanged
    }

    [Fact]
    public void Reserve_Requires_Available()
    {
        var d = NewDriver(); // OFFLINE
        var ex = Assert.Throws<DriverDomainException>(() => d.Reserve(Guid.NewGuid(), Guid.NewGuid(), d.Version));
        Assert.Equal(DriverErrors.DriverNotAvailable, ex.Code);
    }

    [Fact]
    public void Reserve_Same_Reservation_Is_Idempotent()
    {
        var d = NewDriver();
        d.GoOnline(d.Version);
        var t = Guid.NewGuid();
        var r = Guid.NewGuid();
        var v0 = d.Version;
        d.Reserve(t, r, v0);
        var v = d.Version;
        // Replay carries the version the caller originally saw (now stale):
        // same logical effect already exists, so canonical success with no
        // new effect and no version bump (DSI-005 over DSI-003 precedence).
        d.Reserve(t, r, v0);
        Assert.Equal(DriverStatus.Reserved, d.Status);
        Assert.Equal(v, d.Version);
        Assert.Equal(t, d.ReservedTripId);
        Assert.Equal(r, d.ReservationId);
    }

    [Fact]
    public void Reserve_Different_Reservation_Conflicts()
    {
        var d = NewDriver();
        d.GoOnline(d.Version);
        d.Reserve(Guid.NewGuid(), Guid.NewGuid(), d.Version);
        var ex = Assert.Throws<DriverDomainException>(() => d.Reserve(Guid.NewGuid(), Guid.NewGuid(), d.Version));
        Assert.Equal(DriverErrors.DriverNotAvailable, ex.Code);
    }

    [Fact]
    public void Release_Exact_Token_Returns_Available()
    {
        var d = NewDriver();
        d.GoOnline(d.Version);
        var t = Guid.NewGuid();
        var r = Guid.NewGuid();
        d.Reserve(t, r, d.Version);
        d.Release(t, r);
        Assert.Equal(DriverStatus.Available, d.Status);
        Assert.Null(d.ReservedTripId);
        Assert.Null(d.ReservationId);
    }

    [Fact]
    public void Release_Stale_Token_Does_Not_Mutate()
    {
        var d = NewDriver();
        d.GoOnline(d.Version);
        var t = Guid.NewGuid();
        var r = Guid.NewGuid();
        d.Reserve(t, r, d.Version);
        var ex = Assert.Throws<DriverDomainException>(() => d.Release(t, Guid.NewGuid()));
        Assert.Equal(DriverErrors.StaleReservation, ex.Code);
        Assert.Equal(DriverStatus.Reserved, d.Status);
        Assert.Equal(r, d.ReservationId); // R survives
    }

    [Fact]
    public void StartTrip_Exact_Identity_Goes_OnTrip()
    {
        var d = NewDriver();
        d.GoOnline(d.Version);
        var t = Guid.NewGuid();
        var r = Guid.NewGuid();
        d.Reserve(t, r, d.Version);
        d.MarkTripStarted(t, r);
        Assert.Equal(DriverStatus.OnTrip, d.Status);
        Assert.Equal(t, d.ActiveTripId);
    }

    [Fact]
    public void StartTrip_Stale_Identity_Rejected()
    {
        var d = NewDriver();
        d.GoOnline(d.Version);
        d.Reserve(Guid.NewGuid(), Guid.NewGuid(), d.Version);
        var ex = Assert.Throws<DriverDomainException>(() => d.MarkTripStarted(Guid.NewGuid(), Guid.NewGuid()));
        Assert.Equal(DriverErrors.TripMismatch, ex.Code);
        Assert.Equal(DriverStatus.Reserved, d.Status);
    }

    [Fact]
    public void CompleteTrip_Current_Trip_Returns_Available()
    {
        var d = NewDriver();
        d.GoOnline(d.Version);
        var t = Guid.NewGuid();
        var r = Guid.NewGuid();
        d.Reserve(t, r, d.Version);
        d.MarkTripStarted(t, r);
        d.MarkTripCompleted(t);
        Assert.Equal(DriverStatus.Available, d.Status);
        Assert.Null(d.ActiveTripId);
    }

    [Fact]
    public void CompleteTrip_Wrong_Trip_Rejected()
    {
        var d = NewDriver();
        d.GoOnline(d.Version);
        var t = Guid.NewGuid();
        d.Reserve(t, Guid.NewGuid(), d.Version);
        d.MarkTripStarted(t, d.ReservationId!.Value);
        var ex = Assert.Throws<DriverDomainException>(() => d.MarkTripCompleted(Guid.NewGuid()));
        Assert.Equal(DriverErrors.TripMismatch, ex.Code);
    }

    [Fact]
    public void Release_While_OnTrip_Rejected()
    {
        var d = NewDriver();
        d.GoOnline(d.Version);
        var t = Guid.NewGuid();
        var r = Guid.NewGuid();
        d.Reserve(t, r, d.Version);
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
            Guid.NewGuid(), Guid.NewGuid(), null, null, null, DateTimeOffset.UtcNow));
    }
}
