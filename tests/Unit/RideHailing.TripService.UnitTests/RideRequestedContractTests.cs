// A-W05-07/08 proofs: RideRequestedV1 payload shape stability and the
// producer-side emission guard. The checked-in example fixture is the
// cross-team review surface; these tests prove the shape Trip will emit
// (when W9 Outbox publishes it) round-trips exactly, and that FromTrip
// refuses every non-emittable Trip shape.

using System.Text.Json;
using RideHailing.TripService.Domain.Trips;
using RideHailing.TripService.Integration.Matching;
using RideHailing.TripService.UnitTests.Domain.Trips;

namespace RideHailing.TripService.UnitTests;

public sealed class RideRequestedContractTests
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = null,
    };

    private static string FixturePath()
    {
        // Anchor at the repository root (marked by the solution filter file)
        // so the test locates the checked-in fixture regardless of the
        // runner's working directory.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RideHailing.Distributed.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.True(directory is not null, "Repository root (RideHailing.Distributed.slnx) was not found.");
        return Path.Combine(
            directory!.FullName,
            "src", "Services", "Trip", "RideHailing.TripService",
            "Integration", "Matching", "Fixtures", "ride-requested-v1.example.json");
    }

    [Fact]
    public void ExampleFixture_MatchesProducerShape_Exactly()
    {
        var json = File.ReadAllText(FixturePath());
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", root.GetProperty("tripId").GetString());
        Assert.Equal(1, root.GetProperty("matchingRound").GetInt32());
        Assert.Equal(10.762622, root.GetProperty("pickup").GetProperty("latitude").GetDouble());
        Assert.Equal(106.660172, root.GetProperty("pickup").GetProperty("longitude").GetDouble());
        Assert.Equal(10.776889, root.GetProperty("dropoff").GetProperty("latitude").GetDouble());
        Assert.Equal(106.700806, root.GetProperty("dropoff").GetProperty("longitude").GetDouble());
        Assert.Equal("2026-09-26T00:00:00+00:00", root.GetProperty("requestedAt").GetString());

        var payload = JsonSerializer.Deserialize<RideRequestedV1>(json, Json);
        Assert.NotNull(payload);
        Assert.Equal(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), payload.TripId);
        Assert.Equal(1, payload.MatchingRound);

        var reserialized = JsonSerializer.Serialize(payload, Json);
        using var roundTripped = JsonDocument.Parse(reserialized);
        Assert.Equal(5, roundTripped.RootElement.EnumerateObject().Count());
        foreach (var name in new[] { "tripId", "matchingRound", "pickup", "dropoff", "requestedAt" })
        {
            Assert.True(roundTripped.RootElement.TryGetProperty(name, out _), $"missing wire property '{name}'");
        }
    }

    [Fact]
    public void FromTrip_OpenMatchingRound_BuildsPayload()
    {
        var trip = TripTestBuilders.MatchingTrip();
        var requestedAt = new DateTimeOffset(2026, 9, 26, 7, 30, 0, TimeSpan.Zero);

        var payload = RideRequestedV1.FromTrip(trip, requestedAt);

        Assert.Equal(trip.TripId, payload.TripId);
        Assert.Equal(1, payload.MatchingRound);
        Assert.Equal(trip.Pickup.Latitude, payload.Pickup.Latitude);
        Assert.Equal(trip.Pickup.Longitude, payload.Pickup.Longitude);
        Assert.Equal(trip.Dropoff.Latitude, payload.Dropoff.Latitude);
        Assert.Equal(trip.Dropoff.Longitude, payload.Dropoff.Longitude);
        Assert.Equal(requestedAt, payload.RequestedAt);
    }

    [Theory]
    [InlineData(TripState.Created)]
    [InlineData(TripState.DriverReserved)]
    [InlineData(TripState.DriverAccepted)]
    [InlineData(TripState.Cancelled)]
    public void FromTrip_NonEmittableState_IsRejected(TripState state)
    {
        var trip = TripTestBuilders.AtState(state);

        Assert.Throws<InvalidOperationException>(() =>
            RideRequestedV1.FromTrip(trip, TripTestBuilders.ActionMoment));
    }

    [Fact]
    public void FromTrip_MatchingWithAssignment_IsRejected()
    {
        var trip = Trip.Reconstitute(
            TripTestBuilders.TripId,
            TripTestBuilders.RiderId,
            TripTestBuilders.Location(),
            TripTestBuilders.Location(),
            TripTestBuilders.Fare(),
            TripState.Matching,
            1,
            TripTestBuilders.Assignment(),
            TripTestBuilders.CreatedMoment,
            TripTestBuilders.CreatedMoment,
            3);

        Assert.Throws<InvalidOperationException>(() =>
            RideRequestedV1.FromTrip(trip, TripTestBuilders.ActionMoment));
    }

    [Fact]
    public void FromTrip_DefaultTimestamp_IsRejected()
    {
        var trip = TripTestBuilders.MatchingTrip();

        Assert.Throws<ArgumentException>(() => RideRequestedV1.FromTrip(trip, default));
    }
}
