// Trip-owned EF Core persistence foundation (A-W02-02).
//
// SCOPE (W2): DbContext shell ONLY — DI registration + Npgsql provider wiring
// against the Trip-owned connection string. There are deliberately NO DbSet
// members: the W2 model contains no entities. Transport DTOs in Api/ are NOT
// entities and MUST NOT be mapped here. The authoritative Trip aggregate and
// its persistence mapping arrive in W3; Outbox/Inbox/Saga tables belong to
// later workflow issues. An (empty) migration baseline proves the pipeline,
// not future schema.

using Microsoft.EntityFrameworkCore;

namespace RideHailing.TripService.Infrastructure.Persistence;

/// <summary>
/// Trip-owned PostgreSQL context. Trip database only; no Driver/Matching/
/// Payment database access. No business DbSet members in W2.
/// </summary>
public sealed class TripDbContext : DbContext
{
    public TripDbContext(DbContextOptions<TripDbContext> options)
        : base(options)
    {
    }
}
