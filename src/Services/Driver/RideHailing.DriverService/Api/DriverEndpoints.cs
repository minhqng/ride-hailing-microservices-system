// B-W03-08: FUN-02 minimal API. Maps DomainException codes to HTTP (chốt ②).
// Domain never knows HTTP. Invalid* -> 400, conflicts/stale -> 409.
using Microsoft.Extensions.Options;
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
            if (req.UserId == Guid.Empty)
            {
                return Results.BadRequest(new
                {
                    code = DriverErrors.InvalidRequest,
                    message = "userId must not be empty.",
                });
            }

            var id = req.DriverId ?? Guid.NewGuid();

            Driver driver;
            try
            {
                driver = Driver.Create(id, req.UserId, req.VehicleId);
            }
            catch (DriverDomainException ex)
            {
                return MapDomainError(ex);
            }

            // Insert-only: concurrent duplicate creates linearize on the
            // primary key instead of check-then-upsert losing the conflict.
            if (!await repo.TryInsertAsync(driver))
            {
                // Never silently reset existing state (could wipe a RESERVED driver).
                return Results.Conflict(new
                {
                    code = DriverErrors.AlreadyExists,
                    message = "Driver already exists.",
                });
            }

            return Results.Created($"/drivers/{driver.DriverId}", ToDto(driver));
        });

        app.MapGet("/drivers/{id:guid}", async (Guid id, DriverRepository repo) =>
        {
            var driver = await repo.LoadAsync(id);
            return driver is null ? Results.NotFound() : Results.Ok(ToDto(driver));
        });

        // B-W07-04 Race-B closure: version-fenced online/offline. The blind
        // LoadAsync + SaveAsync shape could overwrite a newer RESERVED row
        // with stale state (lost reservation, DSI-003); the guarded
        // TryGoOnline/TryGoOffline row-lock transaction is the only writer now.
        app.MapPost("/drivers/{id:guid}/online", async (
            Guid id,
            StateVersionRequest req,
            DriverRepository repo,
            CancellationToken ct) =>
        {
            if (id == Guid.Empty || req.ExpectedVersion < 1)
            {
                return Results.BadRequest(new
                {
                    code = DriverErrors.InvalidRequest,
                    message = "driverId must not be empty and expectedVersion must be >= 1.",
                });
            }

            try
            {
                var result = await repo.TryGoOnlineAsync(id, req.ExpectedVersion, ct);
                return DriverStateResults.ToHttpResult(result, "online");
            }
            catch (Exception ex) when (ex is Npgsql.NpgsqlException
                or System.Net.Sockets.SocketException
                or TimeoutException)
            {
                return Results.Json(
                    new { code = "DEPENDENCY_UNAVAILABLE", message = "A backing dependency is unavailable." },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        app.MapPost("/drivers/{id:guid}/offline", async (
            Guid id,
            StateVersionRequest req,
            DriverRepository repo,
            CancellationToken ct) =>
        {
            if (id == Guid.Empty || req.ExpectedVersion < 1)
            {
                return Results.BadRequest(new
                {
                    code = DriverErrors.InvalidRequest,
                    message = "driverId must not be empty and expectedVersion must be >= 1.",
                });
            }

            try
            {
                var result = await repo.TryGoOfflineAsync(id, req.ExpectedVersion, ct);
                return DriverStateResults.ToHttpResult(result, "offline");
            }
            catch (Exception ex) when (ex is Npgsql.NpgsqlException
                or System.Net.Sockets.SocketException
                or TimeoutException)
            {
                return Results.Json(
                    new { code = "DEPENDENCY_UNAVAILABLE", message = "A backing dependency is unavailable." },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        // B-W07-01/02: exact-token release (DSI-004/033, R-06). Exact token on
        // a live reservation frees (200); coherent AVAILABLE is an idempotent
        // cleanup no-op (200); a live foreign reservation conflicts (409) and
        // is never freed. Cancellation is never caught; dependency failure
        // without proven effect is a static 503.
        app.MapPost("/drivers/{id:guid}/release", async (
            Guid id,
            ReleaseDriverRequest req,
            DriverRepository repo,
            CancellationToken ct) =>
        {
            if (id == Guid.Empty || req.TripId == Guid.Empty || req.ReservationId == Guid.Empty)
            {
                return Results.BadRequest(new
                {
                    code = DriverErrors.InvalidRequest,
                    message = "driverId/tripId/reservationId must not be empty.",
                });
            }

            try
            {
                var result = await repo.TryReleaseAsync(id, req.TripId, req.ReservationId, ct);
                return DriverStateResults.ToHttpResult(result, "released");
            }
            catch (Exception ex) when (ex is Npgsql.NpgsqlException
                or System.Net.Sockets.SocketException
                or TimeoutException)
            {
                return Results.Json(
                    new { code = "DEPENDENCY_UNAVAILABLE", message = "A backing dependency is unavailable." },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        // B-W07-07: read-only reservation truth for reconciliation
        // (Doc 02 §12.6). Always 200 with the canonical outcome value
        // (NOT_FOUND included); only transport failure is 503, which the
        // caller treats as stay-UNKNOWN. Cancellation is never caught.
        app.MapGet("/internal/drivers/{id:guid}/reservations/{reservationId:guid}", async (
            Guid id,
            Guid reservationId,
            DriverRepository repo,
            CancellationToken ct) =>
        {
            if (id == Guid.Empty || reservationId == Guid.Empty)
            {
                return Results.BadRequest(new
                {
                    code = DriverErrors.InvalidRequest,
                    message = "driverId/reservationId must not be empty.",
                });
            }

            try
            {
                var query = await repo.QueryReservationAsync(id, reservationId, ct);
                return DriverStateResults.ToHttpResult(query, id);
            }
            catch (Exception ex) when (ex is Npgsql.NpgsqlException
                or System.Net.Sockets.SocketException
                or TimeoutException)
            {
                return Results.Json(
                    new { code = "DEPENDENCY_UNAVAILABLE", message = "A backing dependency is unavailable." },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        // B-W06-01: OCC reserve. Sync authority decision (Doc 00 §5.3, 02 §2.2-2.3):
        // the guarded Driver DB write inside TryReserveAsync is the
        // linearization point. 200 carries the canonical reservation on both
        // win and same-reservation replay; conflicts are 409 domain branches,
        // never system errors. Dependency failure without proven effect is a
        // static 503; cancellation is never caught.
        app.MapPost("/drivers/{id:guid}/reserve", async (
            Guid id,
            ReserveDriverRequest req,
            DriverRepository repo,
            CancellationToken ct) =>
        {
            if (req.TripId == Guid.Empty || req.ReservationId == Guid.Empty || req.ExpectedVersion < 1
                || id == Guid.Empty)
            {
                return Results.BadRequest(new
                {
                    code = DriverErrors.InvalidRequest,
                    message = "driverId/tripId/reservationId must not be empty and expectedVersion must be >= 1.",
                });
            }

            try
            {
                var result = await repo.TryReserveAsync(
                    id, req.TripId, req.ReservationId, req.ExpectedVersion, ct);
                return DriverReserveResults.ToHttpResult(result);
            }
            catch (Exception ex) when (ex is Npgsql.NpgsqlException
                or System.Net.Sockets.SocketException
                or TimeoutException)
            {
                // No proven effect: static message, never exception text.
                // OperationCanceledException is deliberately NOT caught.
                return Results.Json(
                    new { code = "DEPENDENCY_UNAVAILABLE", message = "A backing dependency is unavailable." },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        app.MapPost("/drivers/{id:guid}/location", (
            Guid id,
            LocationRequest req,
            LocationAcceptor acceptor,
            IOptions<LocationIngressOptions> ingressOptions) =>
        {
            LocationSample sample;
            try
            {
                sample = new LocationSample(id, req.DeviceSessionId, req.SequenceNo,
                    req.CapturedAt, req.Latitude, req.Longitude);
                var result = acceptor.Accept(sample, null, ingressOptions.Value);
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
        DriverErrors.InvalidCoordinate or DriverErrors.InvalidLocation or DriverErrors.InvalidRequest or DriverErrors.InvalidIdentity =>
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

    public sealed record ReserveDriverRequest(Guid TripId, Guid ReservationId, int ExpectedVersion);

    public sealed record StateVersionRequest(int ExpectedVersion);

    public sealed record ReleaseDriverRequest(Guid TripId, Guid ReservationId);

    public sealed record LocationRequest(
        string DeviceSessionId, long SequenceNo, DateTimeOffset CapturedAt,
        double Latitude, double Longitude);
}
