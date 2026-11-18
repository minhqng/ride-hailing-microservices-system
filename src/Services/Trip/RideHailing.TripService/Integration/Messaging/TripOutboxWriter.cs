// Atomic Trip Outbox staging.
//
// Every Trip transition that creates an external obligation stages the
// corresponding pending Outbox row on the SAME DbContext and lets the single
// SaveChangesAsync commit both. That is the whole point: there is no window
// in which the business row is committed without its
// outgoing intent, and no window in which an intent exists for an uncommitted
// transition.
//
// EF's SaveChangesAsync wraps all tracked changes in one database
// transaction; the domain's application-managed Version token provides the
// optimistic-concurrency fence that makes a losing writer roll back both its
// transition and its staged intents.

using RideHailing.Messaging.Outbox;
using RideHailing.TripService.Domain.Trips;
using RideHailing.TripService.Infrastructure.Persistence;

namespace RideHailing.TripService.Integration.Messaging;

/// <summary>Stages Trip-owned pending intents into the current unit of work.</summary>
public static class TripOutboxWriter
{
    /// <summary>Captures the cleanup identities already durable before a transition.</summary>
    public static IReadOnlySet<string> CleanupKeys(Trip trip)
    {
        ArgumentNullException.ThrowIfNull(trip);
        return trip.CleanupIntents.Select(i => i.Key).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Stages one validated pending intent; never publishes.</summary>
    public static void Enqueue(TripDbContext db, OutboxMessage message)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(message);
        db.OutboxMessages.Add(TripOutboxMessage.From(message));
    }

    /// <summary>
    /// Stages the wire obligations newly added by this transition. Payment
    /// reconciliation stays Trip-durable but is not published here.
    /// </summary>
    public static void EnqueueNewCleanup(
        TripDbContext db, Trip trip, IReadOnlySet<string> preexistingKeys)
    {
        ArgumentNullException.ThrowIfNull(db);
        TripOutboxIntents.EnqueueNewCleanup(trip, preexistingKeys, message => Enqueue(db, message));
    }
}
