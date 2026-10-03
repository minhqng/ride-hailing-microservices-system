// B-W03-05: ingress validation (Doc 01 §5.3). Invalid input throws (API maps to 400).
using RideHailing.DriverService.Domain.Drivers;

namespace RideHailing.DriverService.Application.Location;

public static class LocationValidator
{
    public static void Validate(
        LocationSample sample,
        DateTimeOffset? now = null,
        LocationIngressOptions? options = null)
    {
        var policy = options ?? LocationIngressOptions.FrozenW3Baseline();
        if (sample.DriverId == Guid.Empty)
        {
            throw new DriverDomainException(DriverErrors.InvalidLocation, "driverId must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(sample.DeviceSessionId))
        {
            throw new DriverDomainException(DriverErrors.InvalidLocation, "deviceSessionId must not be empty.");
        }

        if (sample.SequenceNo < 0)
        {
            throw new DriverDomainException(DriverErrors.InvalidLocation, "sequenceNo must be >= 0.");
        }

        if (!double.IsFinite(sample.Latitude) || !double.IsFinite(sample.Longitude))
        {
            throw new DriverDomainException(DriverErrors.InvalidCoordinate, "coordinates must be finite.");
        }

        if (sample.Latitude is < -90 or > 90)
        {
            throw new DriverDomainException(DriverErrors.InvalidCoordinate, "latitude must be in [-90,90].");
        }

        if (sample.Longitude is < -180 or > 180)
        {
            throw new DriverDomainException(DriverErrors.InvalidCoordinate, "longitude must be in [-180,180].");
        }

        if (sample.CapturedAt == default)
        {
            throw new DriverDomainException(DriverErrors.InvalidLocation, "capturedAt must be set.");
        }

        var reference = now ?? DateTimeOffset.UtcNow;
        if (sample.CapturedAt > reference + policy.MaxFutureSkew)
        {
            throw new DriverDomainException(DriverErrors.InvalidLocation, "capturedAt is too far in the future.");
        }

        if (sample.CapturedAt < reference - policy.MaxPastAge)
        {
            throw new DriverDomainException(DriverErrors.InvalidLocation, "capturedAt is too old.");
        }
    }

    // W4 hook: Redis GEOADD only indexes lat ≈ [-85.05112878, 85.05112878].
    // W3 accepts polar latitudes; projection decides indexability (chốt ①).
    public static bool IsGeoIndexable(double latitude, double longitude)
    {
        return longitude is >= -180 and <= 180
            && latitude is >= -85.05112878 and <= 85.05112878;
    }
}
