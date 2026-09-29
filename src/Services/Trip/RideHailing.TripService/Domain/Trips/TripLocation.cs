// Trip-side domain coordinate: pickup or dropoff (Doc 01 §5.3, §6.1).
//
// This is a domain value object, not the HTTP transport DTO
// (RideHailing.TripService.Api.TripLocationDto): the domain layer must not
// depend on transport shapes. Only the coordinate itself is modeled here;
// address, geocoding and accuracy metadata are intentionally absent.

namespace RideHailing.TripService.Domain.Trips;

/// <summary>
/// Identity-less pickup/dropoff coordinate. Immutable; equality is by value.
/// </summary>
public sealed record TripLocation
{
    /// <summary>Latitude in degrees. Canonical range: -90..90, finite.</summary>
    public double Latitude { get; }

    /// <summary>Longitude in degrees. Canonical range: -180..180, finite.</summary>
    public double Longitude { get; }

    /// <summary>
    /// Creates a coordinate. Rejects non-finite values and values outside
    /// the canonical ranges (Doc 01 §5.3).
    /// </summary>
    public TripLocation(double latitude, double longitude)
    {
        if (!double.IsFinite(latitude) || latitude < -90 || latitude > 90)
        {
            throw new ArgumentOutOfRangeException(nameof(latitude), latitude, "Latitude must be finite and within -90..90.");
        }

        if (!double.IsFinite(longitude) || longitude < -180 || longitude > 180)
        {
            throw new ArgumentOutOfRangeException(nameof(longitude), longitude, "Longitude must be finite and within -180..180.");
        }

        Latitude = latitude;
        Longitude = longitude;
    }
}
