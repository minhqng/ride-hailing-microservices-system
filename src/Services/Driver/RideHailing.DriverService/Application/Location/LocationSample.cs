// Location sample. Lat [-90,90] accepted at ingress.
namespace RideHailing.DriverService.Application.Location;

public sealed record LocationSample(
    Guid DriverId,
    string DeviceSessionId,
    long SequenceNo,
    DateTimeOffset CapturedAt,
    double Latitude,
    double Longitude);
