// Trip-owned EF Core persistence (A-W02-02 foundation, A-W03-07 aggregate,
// A-W04-02 request idempotency, A-W04-04 active-Rider defense).
//
// SCOPE (W4): Trip aggregate plus Trip-local request-idempotency
// bookkeeping. Transport DTOs in Api/ are NOT entities and MUST NOT be
// mapped here. Driver/Matching/Payment/Saga/Outbox/Inbox tables belong to
// later work. Version is the application-managed optimistic-concurrency
// token: the domain advances it, persistence only compares the original
// value. Concurrency failures are translated to
// TripConcurrencyConflictException at this Trip-specific boundary; no generic
// repository, no blind retry.

using Microsoft.EntityFrameworkCore;
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

    /// <summary>W4 request-idempotency bookkeeping set (not a domain aggregate).</summary>
    public DbSet<RequestIdempotencyRecord> RequestIdempotency => Set<RequestIdempotencyRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new TripConfiguration());
        modelBuilder.ApplyConfiguration(new TripMessageReceiptConfiguration());
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
