// Request-idempotency EF mapping (A-W04-02).
//
// Smallest schema: composite primary key (scope, key) named
// PK_request_idempotency satisfies UNIQUE(scope,key) with no extra surrogate
// or redundant unique index. ResourceId references trips.trip_id with
// RESTRICT so a record can never silently dangle; the relationship is
// Infrastructure-only with no domain navigation.
//
// Opaque-exact key rule: scope/key pin COLLATE "C" (bytewise) so the
// database uniqueness arbiter treats keys case-sensitively regardless of the
// database default collation. Without the pin, a case-insensitive default
// collation would fold distinct keys into one logical request (wrong replay)
// or split identical keys.

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RideHailing.TripService.Domain.Trips;

namespace RideHailing.TripService.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF configuration for the request-idempotency bookkeeping table.
/// </summary>
internal sealed class RequestIdempotencyConfiguration : IEntityTypeConfiguration<RequestIdempotencyRecord>
{
    public void Configure(EntityTypeBuilder<RequestIdempotencyRecord> builder)
    {
        builder.ToTable("request_idempotency");

        builder.HasKey(r => new { r.Scope, r.Key })
            .HasName("PK_request_idempotency");

        builder.Property(r => r.Scope)
            .HasColumnName("scope")
            .HasMaxLength(64)
            .UseCollation("C")
            .IsRequired();

        builder.Property(r => r.Key)
            .HasColumnName("key")
            .HasMaxLength(128)
            .UseCollation("C")
            .IsRequired();

        builder.Property(r => r.RequestFingerprint)
            .HasColumnName("request_fingerprint")
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(r => r.ResourceId)
            .HasColumnName("resource_id")
            .IsRequired();

        builder.Property(r => r.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.HasOne<Trip>()
            .WithMany()
            .HasForeignKey(r => r.ResourceId)
            .HasConstraintName("FK_request_idempotency_trips_resource_id")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
