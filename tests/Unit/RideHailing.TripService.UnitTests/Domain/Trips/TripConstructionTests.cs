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
        Assert.Equal(0, trip.CurrentMatchingRound);
        Assert.Equal(trip.CreatedAt, trip.UpdatedAt);
        Assert.Equal(0, trip.Version);
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
            1,
            null,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            0));
    }

    [Theory]
    [InlineData(TripState.Created)]
    [InlineData(TripState.Matching)]
    [InlineData(TripState.MatchFailed)]
    public void Reconstitute_WithoutAssignment_IsAllowed(TripState state)
    {
        var round = state == TripState.Created ? 0 : 1;
        var trip = Trip.Reconstitute(
            TripTestBuilders.TripId,
            TripTestBuilders.RiderId,
            TripTestBuilders.Location(),
            TripTestBuilders.Location(),
            TripTestBuilders.Fare(),
            state,
            round,
            null,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            0);

        Assert.Equal(state, trip.State);
        Assert.Null(trip.Assignment);
        Assert.Equal(round, trip.CurrentMatchingRound);
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
            1,
            null,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            2);

        Assert.Equal(TripState.Cancelled, trip.State);
        Assert.Null(trip.Assignment);
    }

    [Fact]
    public void Reconstitute_PreservesHistoricalState()
    {
        var createdAt = new DateTimeOffset(2026, 9, 26, 7, 0, 0, TimeSpan.Zero);
        var updatedAt = new DateTimeOffset(2026, 9, 26, 8, 0, 0, TimeSpan.Zero);

        var trip = Trip.Reconstitute(
            TripTestBuilders.TripId,
            TripTestBuilders.RiderId,
            TripTestBuilders.Location(),
            TripTestBuilders.Location(),
            TripTestBuilders.Fare(),
            TripState.DriverReserved,
            1,
            TripTestBuilders.Assignment(),
            createdAt,
            updatedAt,
            4);

        Assert.Equal(TripState.DriverReserved, trip.State);
        Assert.Equal(TripTestBuilders.Assignment(), trip.Assignment);
        Assert.Equal(createdAt, trip.CreatedAt);
        Assert.Equal(updatedAt, trip.UpdatedAt);
        Assert.Equal(1, trip.CurrentMatchingRound);
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
            1,
            TripTestBuilders.Assignment(),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            5);

        Assert.Equal(TripState.Cancelled, trip.State);
        Assert.Equal(TripTestBuilders.DriverId, trip.Assignment!.DriverId);
        Assert.Equal(TripTestBuilders.ReservationId, trip.Assignment.ReservationId);
    }

    [Fact]
    public void Reconstitute_CreatedWithOpenRound_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => Trip.Reconstitute(
            TripTestBuilders.TripId,
            TripTestBuilders.RiderId,
            TripTestBuilders.Location(),
            TripTestBuilders.Location(),
            TripTestBuilders.Fare(),
            TripState.Created,
            1,
            null,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            0));
    }

    [Fact]
    public void Reconstitute_MatchingWithoutOpenRound_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => Trip.Reconstitute(
            TripTestBuilders.TripId,
            TripTestBuilders.RiderId,
            TripTestBuilders.Location(),
            TripTestBuilders.Location(),
            TripTestBuilders.Fare(),
            TripState.Matching,
            0,
            null,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            0));
    }

    [Fact]
    public void Reconstitute_AssignmentDisagreeingWithCurrentRound_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => Trip.Reconstitute(
            TripTestBuilders.TripId,
            TripTestBuilders.RiderId,
            TripTestBuilders.Location(),
            TripTestBuilders.Location(),
            TripTestBuilders.Fare(),
            TripState.DriverReserved,
            2,
            TripTestBuilders.Assignment(matchingRound: 1),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            3));
    }
}
