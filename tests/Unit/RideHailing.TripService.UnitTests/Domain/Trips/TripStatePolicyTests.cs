// DSI-013 active-state classification proof (A-W04-04): exact domain set,
// six active and three non-active states. One table-driven theory.

using RideHailing.TripService.Domain.Trips;

namespace RideHailing.TripService.UnitTests.Domain.Trips;

public sealed class TripStatePolicyTests
{
    [Theory]
    [InlineData(TripState.Created)]
    [InlineData(TripState.Matching)]
    [InlineData(TripState.DriverReserved)]
    [InlineData(TripState.DriverAccepted)]
    [InlineData(TripState.ReadyToStart)]
    [InlineData(TripState.InProgress)]
    public void ActiveStates_OccupyRiderBookingSlot(TripState state)
    {
        Assert.True(state.IsActiveForRiderBooking());
        Assert.Contains(state, TripStatePolicy.ActiveForRiderBooking);
    }

    [Theory]
    [InlineData(TripState.Completed)]
    [InlineData(TripState.Cancelled)]
    [InlineData(TripState.MatchFailed)]
    public void TerminalStates_DoNotOccupyRiderBookingSlot(TripState state)
    {
        Assert.False(state.IsActiveForRiderBooking());
        Assert.DoesNotContain(state, TripStatePolicy.ActiveForRiderBooking);
        Assert.True(state.IsTerminal());
    }

    [Fact]
    public void ActiveSet_ContainsExactlySixStates()
    {
        Assert.Equal(6, TripStatePolicy.ActiveForRiderBooking.Count);
        Assert.Equal(Enum.GetValues<TripState>().Length - 3, TripStatePolicy.ActiveForRiderBooking.Count);
    }
}
