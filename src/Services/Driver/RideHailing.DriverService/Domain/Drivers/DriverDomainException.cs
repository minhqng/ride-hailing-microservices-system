// Domain exception. Carries a stable Code so API can map to 400/409.
// Domain never knows HTTP.
namespace RideHailing.DriverService.Domain.Drivers;

public sealed class DriverDomainException : Exception
{
    public string Code { get; }

    public DriverDomainException(string code, string message)
        : base(message)
    {
        Code = code;
    }
}
