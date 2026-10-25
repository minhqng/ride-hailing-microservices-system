// RideSaga EF mapping (A-W10-01, Doc 05A §4.3/§18.3).
//
// One authoritative current saga per Trip: UNIQUE(trip_id). Status/step
// persist as canonical text; the storage CHECK constraints mirror the domain
// transition guards as a persisted-state backstop, including the frozen
// per-trip payment operation-key lineage (DSI-034) that the shared
// ride_sagas lineage CHECK enforces: a key, when present, must equal
// trip_id::text || ':<kind>:v1'. StepVersion is the application-managed
// optimistic-concurrency token (mirrors trips.version).

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using RideHailing.TripService.Domain.Sagas;

namespace RideHailing.TripService.Infrastructure.Persistence.Configurations;

internal sealed class RideSagaConfiguration : IEntityTypeConfiguration<RideSaga>
{
    public void Configure(EntityTypeBuilder<RideSaga> builder)
    {
        builder.ToTable("ride_sagas", t =>
        {
            t.HasCheckConstraint(
                "CK_ride_sagas_status_domain",
                "status IN ('ACTIVE', 'COMPENSATING', 'COMPLETED', 'COMPENSATED', 'REQUIRES_REVIEW')");
            t.HasCheckConstraint(
                "CK_ride_sagas_step_domain",
                "current_step IN ('AUTHORIZE_PENDING', 'AUTHORIZED', 'CAPTURE_PENDING', 'CAPTURED', 'RELEASE_PENDING', 'VOID_PENDING')");
            t.HasCheckConstraint(
                "CK_ride_sagas_step_version_positive",
                "step_version >= 1");
            // Frozen operation-key lineage (Doc 05A §18.4): one stable logical
            // identity per Payment operation, bound to the owning trip row.
            t.HasCheckConstraint(
                "CK_ride_sagas_operation_lineage",
                "(authorize_operation_key IS NULL OR authorize_operation_key = trip_id::text || ':authorize:v1') " +
                "AND (capture_operation_key IS NULL OR capture_operation_key = trip_id::text || ':capture:v1') " +
                "AND (void_operation_key IS NULL OR void_operation_key = trip_id::text || ':void:v1')");
            // A saga always fences exactly one reservation identity.
            t.HasCheckConstraint(
                "CK_ride_sagas_reservation_present",
                "reservation_id IS NOT NULL AND reservation_id <> '00000000-0000-0000-0000-000000000000'::uuid");
            // Structural step/operation agreement (mirrors RideSaga.Reconstitute).
            t.HasCheckConstraint(
                "CK_ride_sagas_step_key_agreement",
                "((current_step NOT IN ('CAPTURE_PENDING', 'CAPTURED')) OR capture_operation_key IS NOT NULL) " +
                "AND (current_step <> 'VOID_PENDING' OR void_operation_key IS NOT NULL) " +
                "AND (status <> 'COMPLETED' OR current_step = 'CAPTURED')");
        });

        builder.HasKey(e => e.SagaId);
        builder.Property(e => e.SagaId)
            .HasColumnName("saga_id")
            .ValueGeneratedNever()
            .IsRequired();

        // One authoritative current saga per Trip (A-W10-01).
        builder.Property(e => e.TripId)
            .HasColumnName("trip_id")
            .IsRequired();
        builder.HasIndex(e => e.TripId)
            .HasDatabaseName("UX_ride_sagas_one_per_trip")
            .IsUnique();

        builder.Property(e => e.Status)
            .HasColumnName("status")
            .HasMaxLength(32)
            .IsRequired()
            .HasConversion(new ValueConverter<RideSagaStatus, string>(
                v => v.ToCanonicalName(),
                v => RideSagaStatusRules.FromCanonicalName(v)));

        builder.Property(e => e.CurrentStep)
            .HasColumnName("current_step")
            .HasMaxLength(32)
            .IsRequired()
            .HasConversion(new ValueConverter<RideSagaStep, string>(
                v => v.ToCanonicalName(),
                v => RideSagaStepRules.FromCanonicalName(v)));

        // Application-managed optimistic-concurrency token: the domain advances
        // StepVersion once per committed transition; persistence only compares.
        builder.Property(e => e.StepVersion)
            .HasColumnName("step_version")
            .IsRequired()
            .ValueGeneratedNever()
            .IsConcurrencyToken();

        builder.Property(e => e.ReservationId)
            .HasColumnName("reservation_id");

        builder.Property(e => e.AuthorizeOperationKey)
            .HasColumnName("authorize_operation_key")
            .HasMaxLength(128);

        builder.Property(e => e.CaptureOperationKey)
            .HasColumnName("capture_operation_key")
            .HasMaxLength(128);

        builder.Property(e => e.VoidOperationKey)
            .HasColumnName("void_operation_key")
            .HasMaxLength(128);

        builder.Property(e => e.LastErrorCode)
            .HasColumnName("last_error_code")
            .HasMaxLength(128);

        builder.Property(e => e.RequiresReviewReason)
            .HasColumnName("requires_review_reason")
            .HasMaxLength(512);

        builder.Property(e => e.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(e => e.UpdatedAt)
            .HasColumnName("updated_at")
            .IsRequired();

        // Saga restart (Doc 02 §14.7) scans non-terminal saga rows.
        builder.HasIndex(e => e.Status)
            .HasDatabaseName("ix_ride_sagas_status");
    }
}
