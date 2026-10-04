// TripAssignment coherence tests: one inseparable triple, no frozen
// first-round value, no stale-round integration behavior.

using RideHailing.TripService.Domain.Trips;

namespace RideHailing.TripService.UnitTests.Domain.Trips;

public sealed class TripAssignmentTests
{
    [Fact]
    public void ValidAssignment_PreservesCoherentTriple()
    {
        var assignment = new TripAssignment(
            TripTestBuilders.DriverId, TripTestBuilders.ReservationId, 3);

        Assert.Equal(TripTestBuilders.DriverId, assignment.DriverId);
        Assert.Equal(TripTestBuilders.ReservationId, assignment.ReservationId);
        Assert.Equal(3, assignment.MatchingRound);
    }

    [Fact]
    public void EmptyDriverId_IsRejected()
    {
        Assert.Throws<ArgumentException>(() =>
            new TripAssignment(Guid.Empty, TripTestBuilders.ReservationId, 1));
    }

    [Fact]
    public void EmptyReservationId_IsRejected()
    {
        Assert.Throws<ArgumentException>(() =>
            new TripAssignment(TripTestBuilders.DriverId, Guid.Empty, 1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(42)]
    public void MatchingRoundRepresentation_IsPreservedVerbatim(int round)
    {
        var assignment = new TripAssignment(
            TripTestBuilders.DriverId, TripTestBuilders.ReservationId, round);

        Assert.Equal(round, assignment.MatchingRound);
    }

    [Fact]
    public void EqualAssignments_AreValueEqual()
    {
        var left = new TripAssignment(
            TripTestBuilders.DriverId, TripTestBuilders.ReservationId, 1);
        var right = new TripAssignment(
            TripTestBuilders.DriverId, TripTestBuilders.ReservationId, 1);

        Assert.Equal(left, right);
        Assert.Equal(right.GetHashCode(), left.GetHashCode());
    }

    [Fact]
    public void PublicSurface_IsImmutable()
    {
        var writable = typeof(TripAssignment)
            .GetProperties()
            .Where(p => p.CanWrite)
            .Select(p => p.Name)
            .ToList();

        Assert.Empty(writable);
    }
}
