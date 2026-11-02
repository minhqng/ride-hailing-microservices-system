using System.Text.Json;
using RideHailing.DriverService.Infrastructure.Persistence;
using RideHailing.Security;

namespace RideHailing.DriverService.Api;

public static class DriverAccessGuard
{
    public static void UseDriverAccessGuard(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path;
            if (!path.StartsWithSegments("/drivers") && !path.StartsWithSegments("/internal/drivers"))
            {
                await next(context);
                return;
            }
            if (context.User.Identity?.IsAuthenticated != true)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            var segments = path.Value!.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var internalRead = path.StartsWithSegments("/internal/drivers");
            var action = (internalRead ? "reconcile" : segments.Length > 2 ? segments[2] : "").ToLowerInvariant();
            var matching = RideAuthentication.HasServicePermission(context.User, "matching", "matching:reserve");
            var trip = RideAuthentication.HasServicePermission(context.User, "trip", "trip:release");
            if (action is "reserve" or "reconcile")
            {
                if (matching) await next(context);
                else context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            if (action == "release")
            {
                if (trip) await next(context);
                else context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            if (HttpMethods.IsGet(context.Request.Method) && (matching || trip))
            {
                await next(context);
                return;
            }
            if (!context.User.IsInRole("Driver") || !RideAuthentication.TryActorId(context.User, out var actorId))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            if (segments.Length == 1 && HttpMethods.IsPost(context.Request.Method))
            {
                context.Request.EnableBuffering();
                DriverEndpoints.CreateDriverRequest? request;
                try { request = await context.Request.ReadFromJsonAsync<DriverEndpoints.CreateDriverRequest>(context.RequestAborted); }
                catch (JsonException)
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }
                finally { context.Request.Body.Position = 0; }
                if (request?.UserId != actorId)
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }
            }
            else
            {
                if (segments.Length < 2 || !Guid.TryParse(segments[1], out var driverId))
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }
                var driver = await context.RequestServices.GetRequiredService<DriverRepository>().LoadAsync(driverId);
                if (driver is null)
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }
                if (driver.UserId != actorId)
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }
            }
            await next(context);
        });
    }
}
