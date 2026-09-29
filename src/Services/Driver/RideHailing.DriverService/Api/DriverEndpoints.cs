// B-W03-08: FUN-02 minimal API. Maps DomainException codes to HTTP (chốt ②).
// Domain never knows HTTP. Invalid* -> 400, conflicts/stale -> 409.
using RideHailing.DriverService.Application.Drivers;
using RideHailing.DriverService.Application.Location;
using RideHailing.DriverService.Domain.Drivers;
using RideHailing.DriverService.Infrastructure.Persistence;

namespace RideHailing.DriverService.Api;

public static class DriverEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/drivers", async (CreateDriverRequest req, DriverRepository repo) =>
        {
            var driver = Driver.Create(req.DriverId ?? Guid.NewGuid(), req.UserId, req.VehicleId);
            await repo.SaveAsync(driver);
            return Results.Created($"/drivers/{driver.DriverId}", ToDto(driver));
        });

        app.MapGet("/drivers/{id:guid}", async (Guid id, DriverRepository repo) =>
        {
            var driver = await repo.LoadAsync(id);
            return driver is null ? Results.NotFound() : Results.Ok(ToDto(driver));
        });

        app.MapPost("/drivers/{id:guid}/online", async (Guid id, DriverRepository repo) =>
        {
            var driver = await repo.LoadAsync(id);
            if (driver is null)
            {
                return Results.NotFound();
            }

            try
            {
                GoOnlineHandler.Handle(driver);
            }
            catch (DriverDomainException ex)
            {
                return MapDomainError(ex);
            }

            await repo.SaveAsync(driver);
            return Results.Ok(ToDto(driver));
        });

        app.MapPost("/drivers/{id:guid}/offline", async (Guid id, DriverRepository repo) =>
        {
            var driver = await repo.LoadAsync(id);
            if (driver is null)
            {
                return Results.NotFound();
            }

            try
            {
                GoOfflineHandler.Handle(driver);
            }
            catch (DriverDomainException ex)
            {
                return MapDomainError(ex);
            }

            await repo.SaveAsync(driver);
            return Results.Ok(ToDto(driver));
        });

        app.MapPost("/drivers/{id:guid}/location", (Guid id, LocationRequest req, LocationAcceptor acceptor) =>
        {
            LocationSample sample;
            try
            {
                sample = new LocationSample(id, req.DeviceSessionId, req.SequenceNo,
                    req.CapturedAt, req.Latitude, req.Longitude);
                var result = acceptor.Accept(sample);
                if (result.Outcome == LocationAcceptOutcome.Stale)
                {
                    return Results.Conflict(new
                    {
                        code = DriverErrors.StaleLocation,
                        current = result.Current,
                    });
                }

                return Results.Accepted(value: new { accepted = result.Current });
            }
            catch (DriverDomainException ex)
            {
                return MapDomainError(ex);
            }
        });
    }

    private static IResult MapDomainError(DriverDomainException ex) => ex.Code switch
    {
        DriverErrors.InvalidCoordinate or DriverErrors.InvalidLocation =>
            Results.BadRequest(new { code = ex.Code, message = ex.Message }),
        _ => Results.Conflict(new { code = ex.Code, message = ex.Message }),
    };

    private static object ToDto(Driver d) => new
    {
        driverId = d.DriverId,
        userId = d.UserId,
        status = DriverRepository.ToDb(d.Status),
        version = d.Version,
        reservedTripId = d.ReservedTripId,
        reservationId = d.ReservationId,
        activeTripId = d.ActiveTripId,
        vehicleId = d.VehicleId,
        updatedAt = d.UpdatedAt,
    };

    public sealed record CreateDriverRequest(Guid? DriverId, Guid UserId, Guid? VehicleId);

    public sealed record LocationRequest(
        string DeviceSessionId, long SequenceNo, DateTimeOffset CapturedAt,
        double Latitude, double Longitude);
}
