// Trip Outbox intent identity.
//
// The relay may republish after an uncertain confirm or a crash. Identity
// therefore comes from the committed business fact, never from time, retries
// or process lifetime: the same logical transition always yields the same
// messageId and the same serialized intent.

using System.Text.Json;
using RideHailing.Messaging.Outbox;
using RideHailing.Messaging.Topology;
using RideHailing.TripService.Domain.Trips;
using RideHailing.TripService.Integration.Matching;
using RideHailing.TripService.Integration.Messaging;

namespace RideHailing.TripService.UnitTests;

public sealed class TripOutboxIntentTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 1, 0, 0, TimeSpan.Zero);

    private static Trip Matching(Guid? tripId = null)
    {
        var trip = Trip.Create(tripId ?? Guid.NewGuid(), Guid.NewGuid(), new(10.7, 106.6),
            new(10.8, 106.7), new("VND", 50000m, "w9", Now), Now);
        Assert.True(trip.StartMatching(Now).IsSuccess);
        return trip;
    }

    [Fact]
    public void RideRequested_IdentityIsStableAcrossCalls_AndBoundToTheOpenRound()
    {
        var trip = Matching();
        var first = TripOutboxIntents.RideRequested(trip, Now);
        var repeated = TripOutboxIntents.RideRequested(trip, Now);
        var later = TripOutboxIntents.RideRequested(trip, Now.AddSeconds(30));

        // Identity never depends on time, process lifetime or retry count.
        Assert.Equal(first.MessageId, repeated.MessageId);
        Assert.Equal(first.MessageId, later.MessageId);
        Assert.Equal($"trip:{trip.TripId:N}:ride-requested:1", first.MessageId);
        Assert.Equal(TripOutboxIntents.RideRequestedMessageId(trip), first.MessageId);
        // Production passes the persisted transition timestamp (trip.UpdatedAt),
        // so a rebuild after restart reproduces the identical payload too.
        Assert.Equal(first.PayloadJson, repeated.PayloadJson);
        Assert.Equal(trip.TripId.ToString("D"), first.CorrelationId);
        Assert.Equal(RideTopology.EventsExchange, first.Exchange);
        Assert.Equal("trip.ride-requested.v1", first.RoutingKey);
        Assert.Equal(trip.Version, first.AggregateVersion);

        // A different Trip is a different logical message.
        Assert.NotEqual(first.MessageId, TripOutboxIntents.RideRequested(Matching(), Now).MessageId);
    }

    [Fact]
    public void RideRequested_IsOnlyDerivedFromAMatchingTrip()
    {
        var created = Trip.Create(Guid.NewGuid(), Guid.NewGuid(), new(10.7, 106.6),
            new(10.8, 106.7), new("VND", 50000m, "w9", Now), Now);

        Assert.Throws<InvalidOperationException>(() => TripOutboxIntents.RideRequested(created, Now));
    }

    [Fact]
    public void CancelAndRelease_UseExactCommittedCleanupIdentity()
    {
        var trip = Matching();
        var driverId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        Assert.True(trip.ApplyDriverReserved(driverId, reservationId, 1, Now.AddSeconds(1)).IsSuccess);
        Assert.True(trip.Cancel(Now.AddSeconds(2)).IsSuccess);

        var cancelIntent = trip.CleanupIntents.Single(i => i.Kind == TripCleanupKind.CancelMatching);
        var releaseIntent = trip.CleanupIntents.Single(i => i.Kind == TripCleanupKind.ReleaseReservation);

        var cancel = TripOutboxIntents.CancelMatchingRound(trip, cancelIntent, cancelIntent.CreatedAt);
        Assert.Equal($"trip:{trip.TripId:N}:cancel-round:1", cancel.MessageId);
        Assert.Equal(RideTopology.CommandsExchange, cancel.Exchange);
        Assert.Equal("matching.cancel-round.v1", cancel.RoutingKey);
        var cancelPayload = JsonDocument.Parse(cancel.PayloadJson).RootElement;
        Assert.Equal(trip.TripId, cancelPayload.GetProperty("tripId").GetGuid());
        Assert.Equal(1, cancelPayload.GetProperty("matchingRound").GetInt32());

        var release = TripOutboxIntents.ReleaseReservation(trip, releaseIntent, releaseIntent.CreatedAt);
        Assert.Equal($"trip:{trip.TripId:N}:release:{reservationId:N}", release.MessageId);
        Assert.Equal(RideTopology.CommandsExchange, release.Exchange);
        Assert.Equal("driver.release-reservation.v1", release.RoutingKey);
        var releasePayload = JsonDocument.Parse(release.PayloadJson).RootElement;
        Assert.Equal(trip.TripId, releasePayload.GetProperty("tripId").GetGuid());
        Assert.Equal(driverId, releasePayload.GetProperty("driverId").GetGuid());
        Assert.Equal(reservationId, releasePayload.GetProperty("reservationId").GetGuid());
        Assert.Equal(1, releasePayload.GetProperty("matchingRound").GetInt32());

        Assert.NotEqual(cancel.MessageId, release.MessageId);
    }

    [Fact]
    public void NewCleanup_EnqueuesWireCommandsOnly_AndSkipsPaymentReconciliation()
    {
        var trip = Matching();
        var driverId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        Assert.True(trip.ApplyDriverReserved(driverId, reservationId, 1, Now.AddSeconds(1)).IsSuccess);
        Assert.True(trip.Cancel(Now.AddSeconds(2)).IsSuccess);
        // Authorization is only requested at accept. Cancelling a
        // DRIVER_RESERVED ride owns no Payment effect to reconcile.
        Assert.DoesNotContain(trip.CleanupIntents, i => i.Kind == TripCleanupKind.ReconcilePayment);
        Assert.Equal(2, trip.CleanupIntents.Count);

        var messages = new List<OutboxMessage>();
        TripOutboxIntents.EnqueueNewCleanup(trip, new HashSet<string>(StringComparer.Ordinal), messages.Add);

        Assert.Equal(2, messages.Count);
        Assert.Contains(messages, m => m.MessageType == TripOutboxIntents.CancelMatchingRoundType);
        Assert.Contains(messages, m => m.MessageType == TripOutboxIntents.ReleaseReservationType);
        Assert.DoesNotContain(messages, m => m.MessageType.Contains("Payment", StringComparison.Ordinal));

        // Idempotent staging: an already durable obligation is not re-enqueued.
        var repeated = new List<OutboxMessage>();
        TripOutboxIntents.EnqueueNewCleanup(trip,
            trip.CleanupIntents.Select(i => i.Key).ToHashSet(StringComparer.Ordinal), repeated.Add);
        Assert.Empty(repeated);
    }

    [Fact]
    public void CancellationAfterAccept_RequiresPaymentReconciliation_ButNoPaymentWireCommand()
    {
        var trip = Matching();
        var driverId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        Assert.True(trip.ApplyDriverReserved(driverId, reservationId, 1, Now.AddSeconds(1)).IsSuccess);
        Assert.True(trip.RecordDriverAccepted(driverId, reservationId, Now.AddSeconds(2)).IsSuccess);
        Assert.True(trip.Cancel(Now.AddSeconds(3)).IsSuccess);
        Assert.Contains(trip.CleanupIntents, i => i.Kind == TripCleanupKind.ReconcilePayment);

        var messages = new List<OutboxMessage>();
        TripOutboxIntents.EnqueueNewCleanup(trip, new HashSet<string>(StringComparer.Ordinal), messages.Add);

        Assert.Equal(2, messages.Count);
        Assert.Contains(messages, m => m.MessageType == TripOutboxIntents.CancelMatchingRoundType);
        Assert.Contains(messages, m => m.MessageType == TripOutboxIntents.ReleaseReservationType);
        Assert.DoesNotContain(messages, m => m.MessageType.Contains("Payment", StringComparison.Ordinal));
    }

    [Fact]
    public void EfRowMapping_PreservesEverySharedContractField()
    {
        var trip = Matching();
        var intent = TripOutboxIntents.RideRequested(trip, Now);
        var row = TripOutboxMessage.From(intent);

        Assert.Equal(intent.MessageId, row.MessageId);
        Assert.Equal(intent.MessageType, row.MessageType);
        Assert.Equal(intent.SchemaVersion, row.SchemaVersion);
        Assert.Equal(intent.CorrelationId, row.CorrelationId);
        Assert.Equal(intent.AggregateType, row.AggregateType);
        Assert.Equal(intent.AggregateId, row.AggregateId);
        Assert.Equal(intent.AggregateVersion, row.AggregateVersion);
        Assert.Equal(intent.PayloadJson, row.PayloadJson);
        Assert.Equal(intent.HeadersJson, row.HeadersJson);
        Assert.Equal(intent.Exchange, row.Exchange);
        Assert.Equal(intent.RoutingKey, row.RoutingKey);
        Assert.Equal(intent.OccurredAt, row.OccurredAt);
        Assert.Equal("PENDING", row.Status);
        Assert.Equal(intent.NextAttemptAt, row.NextAttemptAt);
        Assert.Null(row.PublishedAt);

        var published = intent with { Status = OutboxStatus.Published, PublishedAt = Now };
        Assert.Throws<ArgumentException>(() => TripOutboxMessage.From(published));
    }
}
