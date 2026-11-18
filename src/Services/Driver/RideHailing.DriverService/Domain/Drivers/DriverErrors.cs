// Domain error codes. Domain throws; Application/API maps to HTTP.
namespace RideHailing.DriverService.Domain.Drivers;

public static class DriverErrors
{
    public const string IllegalTransition = "ILLEGAL_TRANSITION";
    public const string DriverNotAvailable = "DRIVER_NOT_AVAILABLE";
    public const string ReservationActive = "RESERVATION_ACTIVE";
    public const string StaleReservation = "STALE_RESERVATION";
    public const string TripMismatch = "TRIP_MISMATCH";
    public const string InvalidCoordinate = "INVALID_COORDINATE";
    public const string InvalidLocation = "INVALID_LOCATION";
    public const string InvalidRequest = "INVALID_REQUEST";
    public const string InvalidIdentity = "INVALID_IDENTITY";
    public const string AlreadyExists = "DRIVER_ALREADY_EXISTS";
    /// <summary>Structured authoritative absence (HTTP 404 body). Consumers
    /// must treat a bare 404 without this code as transport uncertainty.</summary>
    public const string DriverNotFound = "DRIVER_NOT_FOUND";
    public const string StaleLocation = "STALE_LOCATION";
    public const string StaleVersion = "STALE_VERSION";
}
