// B-W07-01/04/07: frozen HTTP contracts for release, fenced state changes,
// and reservation reconciliation. Pure mappings, directly unit-tested.
using RideHailing.DriverService.Domain.Drivers;
using RideHailing.DriverService.Infrastructure.Persistence;

namespace RideHailing.DriverService.Api;

public static class DriverStateResults
{
    public static IResult ToHttpResult(DriverMutationResult result, string appliedOutcome)
    {
        return result.Outcome switch
        {
            DriverMutationOutcome.Applied => Results.Ok(Body(result.Driver!, appliedOutcome)),
            DriverMutationOutcome.Unchanged => Results.Ok(Body(result.Driver!, "noop")),
            DriverMutationOutcome.Conflict => Results.Conflict(new
            {
                code = result.ConflictCode,
                message = result.ConflictMessage,
            }),
            DriverMutationOutcome.DriverNotFound => Results.NotFound(),
            _ => throw new ArgumentOutOfRangeException(nameof(result)),
        };
    }

    public static IResult ToHttpResult(ReservationQuery query, Guid driverId)
    {
        return Results.Ok(new
        {
            driverId,
            outcome = query.Outcome switch
            {
                ReservationQueryOutcome.OwnedByThisReservation => "OWNED_BY_THIS_RESERVATION",
                ReservationQueryOutcome.NotCommittedOrAvailable => "NOT_COMMITTED_OR_AVAILABLE",
                ReservationQueryOutcome.ReservedByOther => "RESERVED_BY_OTHER",
                ReservationQueryOutcome.Offline => "OFFLINE",
                ReservationQueryOutcome.OnTrip => "ON_TRIP",
                ReservationQueryOutcome.NotFound => "NOT_FOUND",
                _ => throw new ArgumentOutOfRangeException(nameof(query)),
            },
            version = query.Version,
        });
    }

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
