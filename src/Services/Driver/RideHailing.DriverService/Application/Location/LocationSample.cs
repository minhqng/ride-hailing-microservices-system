// B-W03-05: location sample (Doc 01 §5.2). Chốt ①: lat [-90,90] accepted at ingress.
// GEO indexability is a W4 projection concern, NOT an ingress rejection.
namespace RideHailing.DriverService.Application.Location;

public sealed record LocationSample(
    Guid DriverId,
    string DeviceSessionId,
    long SequenceNo,
    DateTimeOffset CapturedAt,
    double Latitude,
    double Longitude);
