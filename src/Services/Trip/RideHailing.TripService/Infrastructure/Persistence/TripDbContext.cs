// Trip-owned EF Core persistence: aggregate, request idempotency,
// active-Rider defense, Trip-owned Outbox intent and Trip-owned RideSaga.
//
// SCOPE: Trip aggregate, the Trip-owned ride saga, Trip-local
// request-idempotency bookkeeping, durable message receipts and pending
// outgoing Outbox intents. Transport DTOs in Api/ are NOT entities and MUST
// NOT be mapped here. Driver/Matching/Payment/Inbox tables belong to their
// owning services. Version/StepVersion are the application-managed
// optimistic-concurrency tokens: the domain advances them, persistence only
// compares the original value. A deterministic Outbox messageId is the second
// fence for the same logical transition, and an identity clash is only
// treated as a lost race after the committed winner is read back and proven
// to carry the same message contract. Concurrency failures are translated to
// TripConcurrencyConflictException at this Trip-specific boundary; no generic
// repository, no blind retry.

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Npgsql;
using RideHailing.TripService.Domain.Sagas;
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

    /// <summary>Authoritative Trip aggregate set.</summary>
    public DbSet<Trip> Trips => Set<Trip>();

    /// <summary>
    /// Trip-owned persisted ride workflow: exactly one authoritative
    /// current saga per Trip.
    /// </summary>
    public DbSet<RideSaga> RideSagas => Set<RideSaga>();

    public DbSet<TripMessageReceipt> MessageReceipts => Set<TripMessageReceipt>();

    /// <summary>
    /// Trip-owned pending outgoing intents. Rows are appended atomically with
    /// the business transition; the shared relay owns delivery.
    /// </summary>
    public DbSet<TripOutboxMessage> OutboxMessages => Set<TripOutboxMessage>();

    /// <summary>Request-idempotency bookkeeping set (not a domain aggregate).</summary>
    public DbSet<RequestIdempotencyRecord> RequestIdempotency => Set<RequestIdempotencyRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new TripConfiguration());
        modelBuilder.ApplyConfiguration(new RideSagaConfiguration());
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
            if (SameLogicalIntentRace(ex.Entries))
            {
                throw TranslateOutboxIdentityConflict(ex, conflict);
            }

            throw;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: "UX_ride_sagas_one_per_trip",
        })
        {
            throw TranslateSagaIdentityConflict(ex.Entries);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: "PK_trip_cleanup_intents",
        })
        {
            throw TranslateCleanupIdentityConflict(ex.Entries);
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
            if (await SameLogicalIntentRaceAsync(ex.Entries, cancellationToken).ConfigureAwait(false))
            {
                throw TranslateOutboxIdentityConflict(ex, conflict);
            }

            throw;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: "UX_ride_sagas_one_per_trip",
        })
        {
            throw TranslateSagaIdentityConflict(ex.Entries);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: "PK_trip_cleanup_intents",
        })
        {
            throw TranslateCleanupIdentityConflict(ex.Entries);
        }
    }

    /// <summary>
    /// The UNIQUE(trip_id) fence means another writer committed this Trip's
    /// saga first — the same logical workflow, never a foreign one (two
    /// different trips cannot collide on this index). Report it like a
    /// Version conflict so a racing first acceptance reconciles by replay
    /// instead of surfacing as a server fault.
    /// </summary>
    private static TripConcurrencyConflictException TranslateSagaIdentityConflict(
        IEnumerable<EntityEntry> entries)
    {
        var tripId = Guid.Empty;
        foreach (var entry in entries)
        {
            if (entry.Entity is RideSaga saga)
            {
                tripId = saga.TripId;
                break;
            }
        }

        return new TripConcurrencyConflictException(tripId, expectedVersion: null,
            new InvalidOperationException("RideSaga identity already committed by another writer."));
    }

    /// <summary>
    /// Two racing writers (for example Reject vs Rider Cancel) can derive the
    /// same exact cleanup obligation. The primary-key collision means another
    /// writer committed that Trip's obligation first; report it like a Version
    /// conflict so the handler retries/reloads instead of surfacing a fault.
    /// </summary>
    private static TripConcurrencyConflictException TranslateCleanupIdentityConflict(
        IEnumerable<EntityEntry> entries)
    {
        var tripId = Guid.Empty;
        foreach (var entry in entries)
        {
            if (entry.Entity is TripCleanupIntent intent)
            {
                tripId = intent.TripId;
                break;
            }
        }

        return new TripConcurrencyConflictException(tripId, expectedVersion: null,
            new InvalidOperationException("Trip cleanup obligation already committed by another writer."));
    }

    /// <summary>
    /// A deterministic Outbox identity is the second concurrency fence for the
    /// same logical transition: if the row already exists, another writer has
    /// committed that transition. Report it like a Version conflict so callers
    /// reread the winner instead of treating a lost race as a server fault.
    /// Only callers of this translation have already proven the persisted
    /// winner carries the staged message contract.
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

    /// <summary>
    /// A duplicate outbox_messages key is a legitimate lost race only when the
    /// committed winner carries the same message contract (type, exchange,
    /// routing key) as the staged intent. The winner is read back outside the
    /// rolled-back save: a foreign or corrupt row occupying a Trip intent
    /// identity must surface as a database fault, never replay as concurrency,
    /// and an unverifiable winner is treated as a fault rather than a race.
    /// </summary>
    private bool SameLogicalIntentRace(IReadOnlyList<EntityEntry> entries)
    {
        var staged = entries.Select(e => e.Entity).OfType<TripOutboxMessage>().ToList();
        if (staged.Count == 0) return false;

        foreach (var message in staged)
        {
            var winner = OutboxMessages.AsNoTracking()
                .SingleOrDefault(m => m.MessageId == message.MessageId);
            if (!WinnerMatchesContract(winner, message)) return false;
        }

        return true;
    }

    private async Task<bool> SameLogicalIntentRaceAsync(
        IReadOnlyList<EntityEntry> entries, CancellationToken cancellationToken)
    {
        var staged = entries.Select(e => e.Entity).OfType<TripOutboxMessage>().ToList();
        if (staged.Count == 0) return false;

        foreach (var message in staged)
        {
            var winner = await OutboxMessages.AsNoTracking()
                .SingleOrDefaultAsync(m => m.MessageId == message.MessageId, cancellationToken)
                .ConfigureAwait(false);
            if (!WinnerMatchesContract(winner, message)) return false;
        }

        return true;
    }

    private static bool WinnerMatchesContract(TripOutboxMessage? winner, TripOutboxMessage staged) =>
        winner is not null &&
        winner.MessageType == staged.MessageType &&
        winner.Exchange == staged.Exchange &&
        winner.RoutingKey == staged.RoutingKey;

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

            // The saga row shares the same transaction and the same OCC
            // discipline; a losing saga writer must surface as the same
            // transient conflict so redelivery reconciles it.
            if (entry.Entity is RideSaga saga)
            {
                int? expected = null;
                try
                {
                    expected = entry.Property("StepVersion").OriginalValue as int?
                        ?? (entry.Property("StepVersion").OriginalValue is int v ? v : null);
                }
                catch
                {
                    expected = null;
                }

                return new TripConcurrencyConflictException(saga.TripId, expected, ex);
            }
        }

        return null;
    }
}
