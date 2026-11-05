// DS03 Rider notification outbox proofs over real PostgreSQL (Doc 02 §43):
// the recipient-addressed intent commits atomically with the business effect,
// uses the Trip-owned Rider identity and is never duplicated by a replay.

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RideHailing.Messaging.Contracts;
using RideHailing.TripService.Application.Trips.Lifecycle;
using RideHailing.TripService.Domain.Trips;
using RideHailing.TripService.Infrastructure.Persistence;
using RideHailing.TripService.Integration.Messaging;

namespace RideHailing.TripService.IntegrationTests;

[Collection(TripPostgresCollection.Name)]
public sealed class TripW18NotificationOutboxTests(TripPostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 9, 0, 0, TimeSpan.Zero);
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }

    private TripDbContext Db() => new(
        new DbContextOptionsBuilder<TripDbContext>().UseNpgsql(fixture.ConnectionString).Options);

    private async Task<(Guid TripId, Guid RiderId)> SeedMatching()
    {
        await using var db = Db();
        await db.Database.MigrateAsync();
        var trip = Trip.Create(Guid.NewGuid(), Guid.NewGuid(), new(10.7, 106.6), new(10.8, 106.7),
            new("VND", 50000m, "ds03-notify", Now), Now);
        db.Trips.Add(trip);
        await db.SaveChangesAsync();
        Assert.True((await new StartMatchingHandler(db, new Clock(), NullLogger<StartMatchingHandler>.Instance)
            .HandleAsync(trip.TripId, trip.RiderId)).IsSuccess);
        return (trip.TripId, trip.RiderId);
    }

    private async Task<List<NotificationRequestedV1>> Notifications(Guid tripId)
    {
        await using var db = Db();
        var messages = await db.OutboxMessages.AsNoTracking()
            .Where(m => m.AggregateId == tripId.ToString("D") &&
                        m.MessageType == NotificationMessage.MessageType)
            .OrderBy(m => m.OccurredAt).ToListAsync();
        return messages
            .Select(m => JsonSerializer.Deserialize<NotificationRequestedV1>(m.PayloadJson)!)
            .ToList();
    }

    [Fact]
    public async Task RideRequested_CommitsRiderAddressedIntentAtomically()
    {
        var (tripId, riderId) = await SeedMatching();

        var rows = await Notifications(tripId);
        var row = Assert.Single(rows);
        Assert.Equal(riderId, row.RecipientUserId);
        Assert.Equal(NotificationMessage.Roles.Rider, row.RecipientRole);
        Assert.Equal(NotificationMessage.Types.RiderRideRequested, row.NotificationType);
        Assert.Equal(tripId, row.TripId);
    }

    [Fact]
    public async Task ReservationRejectAndReplay_ProduceOneExactIntentEach()
    {
        var (tripId, riderId) = await SeedMatching();
        var driver = Guid.NewGuid();
        var reservation = Guid.NewGuid();

        await using (var db = Db())
        {
            Assert.True((await new RecordDriverReservationHandler(db, new Clock(),
                    NullLogger<RecordDriverReservationHandler>.Instance)
                .HandleAsync(tripId, driver, reservation, 1)).IsSuccess);
        }

        var assigned = Assert.Single((await Notifications(tripId))
            .Where(n => n.NotificationType == NotificationMessage.Types.RiderDriverAssigned));
        Assert.Equal(riderId, assigned.RecipientUserId);
        Assert.Equal(tripId, assigned.TripId);

        await using (var db = Db())
        {
            Assert.True((await new RecordDriverRejectedHandler(db, new Clock(),
                    NullLogger<RecordDriverRejectedHandler>.Instance)
                .HandleAsync(tripId, driver, reservation)).IsSuccess);
        }

        var rejected = Assert.Single((await Notifications(tripId))
            .Where(n => n.NotificationType == NotificationMessage.Types.RiderDriverRejected));
        Assert.Equal(riderId, rejected.RecipientUserId);
        Assert.Equal("DRIVER_REJECTED", rejected.ReasonCode);

        var before = (await Notifications(tripId)).Count;
        await using (var db = Db())
        {
            Assert.True((await new RecordDriverRejectedHandler(db, new Clock(),
                    NullLogger<RecordDriverRejectedHandler>.Instance)
                .HandleAsync(tripId, driver, reservation)).IsReplay);
        }
        Assert.Equal(before, (await Notifications(tripId)).Count);
    }

    [Fact]
    public async Task RiderCancellation_ProducesCancelledIntent()
    {
        var (tripId, riderId) = await SeedMatching();
        await using (var db = Db())
        {
            Assert.True((await new CancelRideHandler(db, new Clock(),
                    NullLogger<CancelRideHandler>.Instance)
                .HandleAsync(tripId, riderId)).IsSuccess);
        }

        var cancelled = Assert.Single((await Notifications(tripId))
            .Where(n => n.NotificationType == NotificationMessage.Types.RiderRideCancelled));
        Assert.Equal(riderId, cancelled.RecipientUserId);
    }

    [Fact]
    public async Task NoDriverFailure_ProducesNoDriverIntent()
    {
        var (tripId, riderId) = await SeedMatching();
        await using (var db = Db())
        {
            Assert.True((await new MarkMatchingFailedHandler(db, new Clock(),
                    NullLogger<MarkMatchingFailedHandler>.Instance)
                .HandleAsync(tripId, 1)).IsSuccess);
        }

        var noDriver = Assert.Single((await Notifications(tripId))
            .Where(n => n.NotificationType == NotificationMessage.Types.RiderNoDriver));
        Assert.Equal(riderId, noDriver.RecipientUserId);
        Assert.Equal("NO_DRIVER_AVAILABLE", noDriver.ReasonCode);
    }

    [Fact]
    public async Task IntentEnvelope_IsStableAndBounded()
    {
        var (tripId, _) = await SeedMatching();
        await using var db = Db();
        var message = await db.OutboxMessages.AsNoTracking()
            .SingleAsync(m => m.AggregateId == tripId.ToString("D") &&
                              m.MessageType == NotificationMessage.MessageType);

        Assert.Equal(NotificationMessage.TripRoutingKey, message.RoutingKey);
        Assert.Equal("ride.events", message.Exchange);
        Assert.Equal("Trip", message.AggregateType);
        Assert.Equal(message.MessageId, message.OperationKey);
        Assert.Equal(tripId.ToString("D"), message.CorrelationId);
        Assert.DoesNotContain("Bearer", message.PayloadJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", message.PayloadJson, StringComparison.OrdinalIgnoreCase);
    }
}
