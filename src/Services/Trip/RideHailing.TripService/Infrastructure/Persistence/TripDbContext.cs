// Trip-owned EF Core persistence (A-W02-02 foundation, A-W03-07 aggregate,
// A-W04-02 request idempotency, A-W04-04 active-Rider defense, A-W09-01
// Trip-owned Outbox intent).
//
// SCOPE: Trip aggregate, Trip-local request-idempotency bookkeeping, durable
// message receipts and pending outgoing Outbox intents. Transport DTOs in
// Api/ are NOT entities and MUST NOT be mapped here. Driver/Matching/Payment/
// Saga/Inbox tables belong to their owning services. Version is the
// application-managed optimistic-concurrency token: the domain advances it,
// persistence only compares the original value. A deterministic Outbox
// messageId is the second fence for the same logical transition. Concurrency
// failures are translated to TripConcurrencyConflictException at this
// Trip-specific boundary; no generic repository, no blind retry.

using Microsoft.EntityFrameworkCore;
using Npgsql;
using RideHailing.TripService.Integration.Messaging;
using RideHailing.TripService.Domain.Trips;
using RideHailing.TripService.Infrastructure.Persistence.Configurations;

namespace RideHailing.TripService.Infrastructure.Persistence;

/// <summary>
/// Trip-owned PostgreSQL context. Trip database only; no Driver/Matching/
/// Payment database access.
/// </summary>
public sealed class TripDbContext : DbContext
{
    public TripDbContext(DbContextOptions<TripDbContext> options)
        : base(options)
    {
    }

    /// <summary>Authoritative W3 Trip aggregate set.</summary>
    public DbSet<Trip> Trips => Set<Trip>();
    public DbSet<TripMessageReceipt> MessageReceipts => Set<TripMessageReceipt>();

    /// <summary>
    /// W9 Trip-owned pending outgoing intents. Rows are appended atomically
    /// with the business transition; the shared C-owned relay owns delivery.
    /// </summary>
    public DbSet<TripOutboxMessage> OutboxMessages => Set<TripOutboxMessage>();

    /// <summary>W4 request-idempotency bookkeeping set (not a domain aggregate).</summary>
    public DbSet<RequestIdempotencyRecord> RequestIdempotency => Set<RequestIdempotencyRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new TripConfiguration());
        modelBuilder.ApplyConfiguration(new TripMessageReceiptConfiguration());
        modelBuilder.ApplyConfiguration(new TripOutboxMessageConfiguration());
        modelBuilder.ApplyConfiguration(new RequestIdempotencyConfiguration());
    }

    public override int SaveChanges()
    {
        try
        {
            return base.SaveChanges();
        }
        catch (DbUpdateConcurrencyException ex)
        {
            var conflict = TryTranslateConcurrencyConflict(ex);
            if (conflict is not null)
            {
                throw conflict;
            }

            throw;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: "outbox_messages_pk",
        } conflict)
        {
            throw TranslateOutboxIdentityConflict(ex, conflict);
        }
    }

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await base.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            var conflict = TryTranslateConcurrencyConflict(ex);
            if (conflict is not null)
            {
                throw conflict;
            }

            throw;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: "outbox_messages_pk",
        } conflict)
        {
            throw TranslateOutboxIdentityConflict(ex, conflict);
        }
    }

    /// <summary>
    /// A deterministic Outbox identity is the second concurrency fence for the
    /// same logical transition: if the row already exists, another writer has
    /// committed that transition. Report it like a Version conflict so callers
    /// reread the winner instead of treating a lost race as a server fault.
    /// </summary>
    private static TripConcurrencyConflictException TranslateOutboxIdentityConflict(
        DbUpdateException ex, PostgresException uniqueViolation)
    {
        var tripId = Guid.Empty;
        foreach (var entry in ex.Entries)
        {
            if (entry.Entity is TripOutboxMessage message &&
                Guid.TryParse(message.AggregateId, out var aggregateId))
            {
                tripId = aggregateId;
                break;
            }
        }

        return new TripConcurrencyConflictException(tripId, expectedVersion: null,
            new InvalidOperationException("Outbox identity already committed by another writer.", uniqueViolation));
    }

    private static TripConcurrencyConflictException? TryTranslateConcurrencyConflict(DbUpdateConcurrencyException ex)
    {
        foreach (var entry in ex.Entries)
        {
            if (entry.Entity is Trip trip)
            {
                int? expected = null;
                try
                {
                    expected = entry.Property("Version").OriginalValue as int?
                        ?? (entry.Property("Version").OriginalValue is int v ? v : null);
                }
                catch
                {
                    expected = null;
                }

                return new TripConcurrencyConflictException(trip.TripId, expected, ex);
            }
        }

        return null;
    }
}
