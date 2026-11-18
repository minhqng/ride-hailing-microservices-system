// Authenticated notification inbox API.
//
// Ownership comes exclusively from the validated JWT actor id (`sub`). A
// client-supplied userId is never accepted, and a foreign/unknown id shares
// the same not-found response so existence is not disclosed.

using Microsoft.AspNetCore.Mvc;
using RideHailing.Messaging.Contracts;
using RideHailing.Security;

namespace RideHailing.NotificationWorker;

public sealed record NotificationInboxItem(
    Guid NotificationId,
    Guid TripId,
    string NotificationType,
    string RecipientRole,
    string Content,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ReadAt,
    string Status)
{
    public static NotificationInboxItem From(NotificationInboxEntry entry) =>
        new(entry.NotificationId, entry.TripId, entry.NotificationType, entry.RecipientRole,
            entry.Content, entry.CreatedAt, entry.ReadAt, entry.DeliveryStatus);
}

public static class NotificationInboxEndpoints
{
    public static void MapNotificationInboxEndpoints(this WebApplication app)
    {
        app.MapGet("/notifications/me", async (
            HttpContext context,
            NotificationProjectionStore store,
            [FromQuery] int? limit,
            CancellationToken token) =>
        {
            if (!RideAuthentication.TryActorId(context.User, out var recipient))
                return Unauthorized();
            var take = Math.Clamp(limit ?? 50, 1, 100);
            var rows = await store.ListForRecipientAsync(recipient, take, token);
            return Results.Ok(rows.Select(NotificationInboxItem.From));
        }).RequireAuthorization();

        app.MapGet("/notifications/{notificationId:guid}", async (
            Guid notificationId,
            HttpContext context,
            NotificationProjectionStore store,
            CancellationToken token) =>
        {
            if (!RideAuthentication.TryActorId(context.User, out var recipient))
                return Unauthorized();
            if (notificationId == Guid.Empty) return NotFound();
            var entry = await store.GetForRecipientAsync(recipient, notificationId, token);
            return entry is null
                ? NotFound()
                : Results.Ok(Item(entry));
        }).RequireAuthorization();

        app.MapPost("/notifications/{notificationId:guid}/read", async (
            Guid notificationId,
            HttpContext context,
            NotificationProjectionStore store,
            TimeProvider time,
            CancellationToken token) =>
        {
            if (!RideAuthentication.TryActorId(context.User, out var recipient))
                return Unauthorized();
            if (notificationId == Guid.Empty) return NotFound();
            var entry = await store.MarkReadAsync(recipient, notificationId, time.GetUtcNow(), token);
            return entry is null ? NotFound() : Results.Ok(Item(entry));
        }).RequireAuthorization();
    }

    private static NotificationInboxItem Item(NotificationInboxEntry entry) =>
        NotificationInboxItem.From(entry);

    private static IResult Unauthorized() => Results.Problem(
        statusCode: StatusCodes.Status401Unauthorized,
        title: "A single authenticated recipient identity is required.",
        extensions: new Dictionary<string, object?> { ["code"] = "UNAUTHORIZED" });

    private static IResult NotFound() => Results.Problem(
        statusCode: StatusCodes.Status404NotFound,
        title: "The notification was not found.",
        extensions: new Dictionary<string, object?> { ["code"] = "NOT_FOUND" });
}
