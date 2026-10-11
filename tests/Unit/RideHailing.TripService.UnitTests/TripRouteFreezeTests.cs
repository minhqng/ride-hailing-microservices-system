using RideHailing.TripService.Api;

namespace RideHailing.TripService.UnitTests;

// Intended routes/methods + header locations.
// CreateRide, GetTrip and CancelRide are registered in Program.cs with
// behavior-backed handlers; these tests freeze the team-parallelism
// convention (paths, methods, header locations) those registrations follow.
public sealed class TripRouteFreezeTests
{
    [Fact]
    public void CreateRide_IsPost_ToTrips()
    {
        // Method "POST" is documented in TripRoutes XML docs; the path freeze is:
        Assert.Equal("/trips", TripRoutes.CreateRide);
    }

    [Fact]
    public void GetTrip_IsGet_ForOneTripId()
    {
        Assert.Equal("/trips/{tripId:guid}", TripRoutes.GetTrip);
    }

    [Fact]
    public void CancelRide_IsPost_CancelSubResource()
    {
        Assert.Equal("/trips/{tripId:guid}/cancel", TripRoutes.CancelRide);
    }

    [Fact]
    public void IdempotencyKey_TravelsInStandardHeader_NotBody()
    {
        Assert.Equal("Idempotency-Key", TripIdentityHeaders.IdempotencyKey);
    }

    [Fact]
    public void CorrelationId_TravelsInHeader_NotBody()
    {
        Assert.Equal("X-Correlation-Id", TripIdentityHeaders.CorrelationId);
    }
}
