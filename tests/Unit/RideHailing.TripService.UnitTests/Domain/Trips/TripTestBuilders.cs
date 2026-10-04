// Shared builders for Trip-domain unit tests: deterministic identities and
// small aggregate arrangers. No reflection, no framework DSL.

using RideHailing.TripService.Domain.Trips;

namespace RideHailing.TripService.UnitTests.Domain.Trips;

internal static class TripTestBuilders
{
    internal static readonly Guid TripId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    internal static readonly Guid RiderId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    internal static readonly Guid DriverId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    internal static readonly Guid ReservationId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    internal static readonly Guid OtherDriverId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    internal static readonly Guid OtherReservationId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    internal static TripLocation Location(double latitude = 10.762622, double longitude = 106.660172) =>
        new(latitude, longitude);

    internal static FareSnapshot Fare(
        string currency = "VND",
        decimal amount = 50000m,
        string pricingPolicyVersion = "p0-test",
        DateTimeOffset? createdAt = null) =>
        new(currency, amount, pricingPolicyVersion, createdAt ?? new DateTimeOffset(2026, 9, 26, 0, 0, 0, TimeSpan.Zero));

    internal static TripAssignment Assignment(
        Guid? driverId = null,
        Guid? reservationId = null,
        int matchingRound = 1) =>
        new(driverId ?? DriverId, reservationId ?? ReservationId, matchingRound);

    internal static Trip NewTrip(DateTimeOffset? createdAt = null) =>
        Trip.Create(
            TripId,
            RiderId,
            Location(),
            Location(10.776889, 106.700806),
            Fare(),
            createdAt ?? new DateTimeOffset(2026, 9, 26, 0, 0, 0, TimeSpan.Zero));

    internal static Trip AtState(TripState state, bool withAssignment = true, int version = 7)
    {
        TripAssignment? assignment = withAssignment switch
        {
            true when state is TripState.DriverReserved or TripState.DriverAccepted
                or TripState.ReadyToStart or TripState.InProgress
                or TripState.Completed or TripState.Cancelled => Assignment(),
            _ => null,
        };

        return Trip.Reconstitute(
            TripId,
            RiderId,
            Location(),
            Location(10.776889, 106.700806),
            Fare(),
            state,
            assignment,
            new DateTimeOffset(2026, 9, 26, 0, 0, 0, TimeSpan.Zero),
            version);
    }

    internal static Trip MatchingTrip() => AtState(TripState.Matching, withAssignment: false);
}
