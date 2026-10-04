// Construction vs reconstitution tests: the creation factory builds valid
// new Trips, while the internal reconstitution path rejects structurally
// impossible combinations without over-constraining states whose later
// workflow fields are not modeled yet.

using RideHailing.TripService.Domain.Trips;

namespace RideHailing.TripService.UnitTests.Domain.Trips;

public sealed class TripConstructionTests
{
    [Fact]
    public void Create_BuildsCreatedTripWithoutAssignment()
    {
        var trip = Trip.Create(
            TripTestBuilders.TripId,
            TripTestBuilders.RiderId,
            TripTestBuilders.Location(),
            TripTestBuilders.Location(10.776889, 106.700806),
            TripTestBuilders.Fare(),
            new DateTimeOffset(2026, 9, 26, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(TripTestBuilders.TripId, trip.TripId);
        Assert.Equal(TripTestBuilders.RiderId, trip.RiderId);
        Assert.Equal(TripState.Created, trip.State);
        Assert.Null(trip.Assignment);
        Assert.Equal(TripTestBuilders.Fare(), trip.Fare);
        Assert.Equal(new DateTimeOffset(2026, 9, 26, 0, 0, 0, TimeSpan.Zero), trip.CreatedAt);
    }

    [Fact]
    public void Create_WithEmptyIdentities_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => Trip.Create(
            Guid.Empty,
            TripTestBuilders.RiderId,
            TripTestBuilders.Location(),
            TripTestBuilders.Location(),
            TripTestBuilders.Fare(),
            new DateTimeOffset(2026, 9, 26, 0, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void Create_WithDefaultTimestamp_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => Trip.Create(
            TripTestBuilders.TripId,
            TripTestBuilders.RiderId,
            TripTestBuilders.Location(),
            TripTestBuilders.Location(),
            TripTestBuilders.Fare(),
            default));
    }

    [Theory]
    [InlineData(TripState.DriverReserved)]
    [InlineData(TripState.DriverAccepted)]
    [InlineData(TripState.ReadyToStart)]
    [InlineData(TripState.InProgress)]
    [InlineData(TripState.Completed)]
    public void Reconstitute_WithoutRequiredAssignment_IsRejected(TripState state)
    {
        Assert.Throws<ArgumentException>(() => Trip.Reconstitute(
            TripTestBuilders.TripId,
            TripTestBuilders.RiderId,
            TripTestBuilders.Location(),
            TripTestBuilders.Location(),
            TripTestBuilders.Fare(),
            state,
            null,
            DateTimeOffset.UtcNow,
            0));
    }

    [Theory]
    [InlineData(TripState.Created)]
    [InlineData(TripState.Matching)]
    [InlineData(TripState.MatchFailed)]
    public void Reconstitute_WithoutAssignment_IsAllowed(TripState state)
    {
        var trip = Trip.Reconstitute(
            TripTestBuilders.TripId,
            TripTestBuilders.RiderId,
            TripTestBuilders.Location(),
            TripTestBuilders.Location(),
            TripTestBuilders.Fare(),
            state,
            null,
            DateTimeOffset.UtcNow,
            0);

        Assert.Equal(state, trip.State);
        Assert.Null(trip.Assignment);
    }

    [Fact]
    public void Reconstitute_CancelledWithoutAssignment_IsAllowed()
    {
        var trip = Trip.Reconstitute(
            TripTestBuilders.TripId,
            TripTestBuilders.RiderId,
            TripTestBuilders.Location(),
            TripTestBuilders.Location(),
            TripTestBuilders.Fare(),
            TripState.Cancelled,
            null,
            DateTimeOffset.UtcNow,
            2);

        Assert.Equal(TripState.Cancelled, trip.State);
        Assert.Null(trip.Assignment);
    }

    [Fact]
    public void Reconstitute_PreservesHistoricalState()
    {
        var createdAt = new DateTimeOffset(2026, 9, 26, 7, 0, 0, TimeSpan.Zero);

        var trip = Trip.Reconstitute(
            TripTestBuilders.TripId,
            TripTestBuilders.RiderId,
            TripTestBuilders.Location(),
            TripTestBuilders.Location(),
            TripTestBuilders.Fare(),
            TripState.DriverReserved,
            TripTestBuilders.Assignment(),
            createdAt,
            4);

        Assert.Equal(TripState.DriverReserved, trip.State);
        Assert.Equal(TripTestBuilders.Assignment(), trip.Assignment);
        Assert.Equal(createdAt, trip.CreatedAt);
        Assert.Equal(4, trip.Version);
    }

    [Fact]
    public void Reconstitute_CancelledWithAssignment_PreservesCleanupIdentity()
    {
        var trip = Trip.Reconstitute(
            TripTestBuilders.TripId,
            TripTestBuilders.RiderId,
            TripTestBuilders.Location(),
            TripTestBuilders.Location(),
            TripTestBuilders.Fare(),
            TripState.Cancelled,
            TripTestBuilders.Assignment(),
            DateTimeOffset.UtcNow,
            5);

        Assert.Equal(TripState.Cancelled, trip.State);
        Assert.Equal(TripTestBuilders.DriverId, trip.Assignment!.DriverId);
        Assert.Equal(TripTestBuilders.ReservationId, trip.Assignment.ReservationId);
    }
}
