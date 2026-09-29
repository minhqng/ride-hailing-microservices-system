using System.Text.Json;
using RideHailing.TripService.Api;

namespace RideHailing.TripService.UnitTests;

// P2 contract tests: transport SHAPE stability only.
// No FSM, fare, assignment, concurrency, or idempotency semantics (W3+).
public sealed class TripHttpContractTests
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = null, // wire names come from explicit JsonPropertyName
    };

    private static TripLocationDto Pickup() => new() { Latitude = 10.762622, Longitude = 106.660172 };

    private static TripLocationDto Dropoff() => new() { Latitude = 10.776889, Longitude = 106.700806 };

    [Fact]
    public void CreateRideRequest_RoundTrips_PickupDropoff()
    {
        var request = new CreateRideRequest { Pickup = Pickup(), Dropoff = Dropoff() };

        var json = JsonSerializer.Serialize(request, Json);
        var back = JsonSerializer.Deserialize<CreateRideRequest>(json, Json);

        Assert.NotNull(back);
        Assert.Equal(request.Pickup.Latitude, back.Pickup.Latitude);
        Assert.Equal(request.Pickup.Longitude, back.Pickup.Longitude);
        Assert.Equal(request.Dropoff.Latitude, back.Dropoff.Latitude);
        Assert.Equal(request.Dropoff.Longitude, back.Dropoff.Longitude);
    }

    [Fact]
    public void CreateRideRequest_UsesFrozenCamelCasePropertyNames()
    {
        var request = new CreateRideRequest { Pickup = Pickup(), Dropoff = Dropoff() };

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(request, Json));
        Assert.True(doc.RootElement.TryGetProperty("pickup", out _));
        Assert.True(doc.RootElement.TryGetProperty("dropoff", out _));
        Assert.False(doc.RootElement.TryGetProperty("Pickup", out _));
        Assert.False(doc.RootElement.TryGetProperty("Dropoff", out _));

        var pickup = doc.RootElement.GetProperty("pickup");
        Assert.True(pickup.TryGetProperty("latitude", out _));
        Assert.True(pickup.TryGetProperty("longitude", out _));
    }

    [Fact]
    public void CreateRideResponse_TripId_Guid_RoundTrips_AsWireString()
    {
        var tripId = Guid.NewGuid();
        var response = new CreateRideResponse { TripId = tripId };

        var json = JsonSerializer.Serialize(response, Json);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(tripId.ToString(), doc.RootElement.GetProperty("tripId").GetString());

        var back = JsonSerializer.Deserialize<CreateRideResponse>(json, Json);
        Assert.NotNull(back);
        Assert.Equal(tripId, back.TripId);
    }

    [Fact]
    public void GetTripResponse_WithoutAssignment_PreservesNulls_AndMatchingRound()
    {
        var response = new GetTripResponse
        {
            TripId = Guid.NewGuid(),
            Status = "MATCHING",
            Pickup = Pickup(),
            Dropoff = Dropoff(),
            MatchingRound = 1,
            AssignedDriverId = null,
            ReservationId = null,
        };

        var back = JsonSerializer.Deserialize<GetTripResponse>(
            JsonSerializer.Serialize(response, Json), Json);

        Assert.NotNull(back);
        Assert.Equal(response.TripId, back.TripId);
        Assert.Equal("MATCHING", back.Status);
        Assert.Equal(1, back.MatchingRound); // int generation, never an opaque Guid
        Assert.Null(back.AssignedDriverId);
        Assert.Null(back.ReservationId);
    }

    [Fact]
    public void GetTripResponse_WithAssignment_RoundTrips_ExactIdentities()
    {
        var driverId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        var response = new GetTripResponse
        {
            TripId = Guid.NewGuid(),
            Status = "DRIVER_RESERVED",
            Pickup = Pickup(),
            Dropoff = Dropoff(),
            MatchingRound = 2,
            AssignedDriverId = driverId,
            ReservationId = reservationId,
        };

        var back = JsonSerializer.Deserialize<GetTripResponse>(
            JsonSerializer.Serialize(response, Json), Json);

        Assert.NotNull(back);
        Assert.Equal(driverId, back.AssignedDriverId);
        Assert.Equal(reservationId, back.ReservationId);
        Assert.Equal(2, back.MatchingRound);
    }

    [Fact]
    public void GetTripResponse_UsesFrozenWireNames()
    {
        var response = new GetTripResponse
        {
            TripId = Guid.NewGuid(),
            Status = "MATCHING",
            Pickup = Pickup(),
            Dropoff = Dropoff(),
            MatchingRound = 1,
        };

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(response, Json));
        foreach (var name in new[] { "tripId", "status", "pickup", "dropoff", "matchingRound", "assignedDriverId", "reservationId" })
        {
            Assert.True(doc.RootElement.TryGetProperty(name, out _), $"missing wire property '{name}'");
        }
    }

    [Fact]
    public void CancelRideResponse_RoundTrips_TerminalStatus()
    {
        var tripId = Guid.NewGuid();
        var response = new CancelRideResponse { TripId = tripId, Status = "CANCELLED" };

        var back = JsonSerializer.Deserialize<CancelRideResponse>(
            JsonSerializer.Serialize(response, Json), Json);

        Assert.NotNull(back);
        Assert.Equal(tripId, back.TripId);
        Assert.Equal("CANCELLED", back.Status);
    }

    [Fact]
    public void TripLocation_CoordinateRepresentation_RoundTrips()
    {
        var location = new TripLocationDto { Latitude = -33.8688, Longitude = 151.2093 };

        var back = JsonSerializer.Deserialize<TripLocationDto>(
            JsonSerializer.Serialize(location, Json), Json);

        Assert.NotNull(back);
        Assert.Equal(-33.8688, back.Latitude);
        Assert.Equal(151.2093, back.Longitude);
    }
}
