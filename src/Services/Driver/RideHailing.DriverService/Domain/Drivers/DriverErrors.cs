// B-W03-01: domain error codes. Domain throws; Application/API maps to HTTP (chốt ②).
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
    public const string AlreadyExists = "DRIVER_ALREADY_EXISTS";
    public const string StaleLocation = "STALE_LOCATION";
}
