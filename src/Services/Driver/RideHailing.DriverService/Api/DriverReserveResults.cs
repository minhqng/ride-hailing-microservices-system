// B-W06-01: frozen HTTP contract for the OCC reserve endpoint.
// Pure mapping (outcome -> IResult), directly unit-tested without an HTTP
// server: status codes, stable machine codes, and leakage discipline are
// proven in DriverReserveResultsTests instead of by review alone.
using RideHailing.DriverService.Domain.Drivers;
using RideHailing.DriverService.Infrastructure.Persistence;

namespace RideHailing.DriverService.Api;

public static class DriverReserveResults
{
    public static IResult ToHttpResult(ReserveResult result) => result.Outcome switch
    {
        ReserveOutcome.Won => Results.Ok(Body(result.Driver!, "won")),
        ReserveOutcome.Replayed => Results.Ok(Body(result.Driver!, "replayed")),
        ReserveOutcome.Conflict => Results.Conflict(new
        {
            code = result.ConflictCode,
            message = result.ConflictMessage,
        }),
        ReserveOutcome.DriverNotFound => Results.NotFound(),
        _ => throw new ArgumentOutOfRangeException(nameof(result)),
    };

    private static object Body(Driver d, string outcome) => new
    {
        driverId = d.DriverId,
        status = DriverRepository.ToDb(d.Status),
        version = d.Version,
        reservedTripId = d.ReservedTripId,
        reservationId = d.ReservationId,
        outcome,
    };
}
