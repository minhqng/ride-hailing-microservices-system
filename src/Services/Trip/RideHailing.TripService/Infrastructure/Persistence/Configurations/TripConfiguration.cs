// Trip aggregate EF mapping.
//
// Authoritative persistence for the Trip aggregate only, plus the partial
// unique index (one active Trip per Rider) and the lifecycle columns
// (current matching round, last-transition timestamp) with
// round/assignment database defense. Value objects
// map into the same trips table as ComplexProperty columns (relational, not
// JSON); no separate value-object tables, no entity keys for value objects,
// no Driver/Matching/Payment/Saga/Outbox/Inbox tables. Request-idempotency
// bookkeeping lives in RequestIdempotencyConfiguration, not here.
// State persists as canonical wire text via an infrastructure-only converter;
// the Domain layer carries no persistence parsing concern.

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using RideHailing.TripService.Domain.Trips;

namespace RideHailing.TripService.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF configuration for the Trip aggregate root.
/// </summary>
internal sealed class TripConfiguration : IEntityTypeConfiguration<Trip>
{
    public void Configure(EntityTypeBuilder<Trip> builder)
    {
        builder.ToTable("trips");

        builder.OwnsMany(t => t.CleanupIntents, cleanup =>
        {
            cleanup.ToTable("trip_cleanup_intents", table =>
            {
                table.HasCheckConstraint("CK_cleanup_round", "matching_round >= 1");
                table.HasCheckConstraint("CK_cleanup_identity", """
                    (kind = 'ReleaseReservation' AND driver_id IS NOT NULL
                     AND driver_id <> '00000000-0000-0000-0000-000000000000'::uuid
                     AND reservation_id IS NOT NULL
                     AND reservation_id <> '00000000-0000-0000-0000-000000000000'::uuid
                     AND key = 'release:' || reservation_id::text)
                    OR (kind = 'CancelMatching' AND driver_id IS NULL AND reservation_id IS NULL
                        AND key = 'matching:' || matching_round::text)
                    OR (kind = 'ReconcilePayment' AND driver_id IS NULL AND reservation_id IS NULL AND key = 'payment')
                    """);
            });
            cleanup.WithOwner().HasForeignKey(i => i.TripId);
            cleanup.HasKey(i => new { i.TripId, i.Key });
            cleanup.Property(i => i.TripId).HasColumnName("trip_id");
            cleanup.Property(i => i.Key).HasColumnName("key").HasMaxLength(80).ValueGeneratedNever();
            cleanup.Property(i => i.Kind).HasColumnName("kind").HasConversion<string>().HasMaxLength(32);
            cleanup.Property(i => i.DriverId).HasColumnName("driver_id");
            cleanup.Property(i => i.ReservationId).HasColumnName("reservation_id");
            cleanup.Property(i => i.MatchingRound).HasColumnName("matching_round");
            cleanup.Property(i => i.CreatedAt).HasColumnName("created_at");
            cleanup.Property(i => i.CompletedAt).HasColumnName("completed_at");
            cleanup.HasIndex(i => i.CreatedAt).HasFilter("completed_at IS NULL");
        });
        builder.Navigation(t => t.CleanupIntents).HasField("_cleanupIntents")
            .UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasKey(e => e.TripId);
        builder.Property(e => e.TripId)
            .HasColumnName("trip_id")
            .ValueGeneratedNever()
            .IsRequired();

        builder.Property(e => e.RiderId)
            .HasColumnName("rider_id")
            .IsRequired();

        builder.Property(e => e.State)
            .HasColumnName("state")
            .HasMaxLength(32)
            .IsRequired()
            .HasConversion(StateConverter());

        // Race-safe defense: at most one active-state Trip row per
        // Rider. The state set comes from the domain policy
        // (TripStatePolicy.ActiveForRiderBooking) mapped through the canonical
        // wire names, so the index predicate cannot drift from the domain
        // rule; a dedicated test proves the generated predicate matches.
        // States are ordered for stable migration SQL.
        var activeNames = TripStatePolicy.ActiveForRiderBooking
            .Select(s => s.ToCanonicalName())
            .OrderBy(n => n, StringComparer.Ordinal)
            .Select(n => $"'{n}'");
        builder.HasIndex(e => e.RiderId)
            .HasDatabaseName("UX_trips_one_active_per_rider")
            .HasFilter($"state IN ({string.Join(", ", activeNames)})")
            .IsUnique();

        // Application-managed optimistic-concurrency token: the domain
        // advances Version once per committed transition; persistence only
        // compares the original value and never increments it.
        builder.Property(e => e.Version)
            .HasColumnName("version")
            .IsRequired()
            .ValueGeneratedNever()
            .IsConcurrencyToken();

        // Driver Reject: a bounded durable reason for
        // MATCH_FAILED. Null is valid for every non-terminal state and for
        // historical MATCH_FAILED rows created before this amendment.
        builder.Property(e => e.FailureReason)
            .HasColumnName("failure_reason")
            .HasMaxLength(32)
            .HasConversion(
                value => value == null ? null : value.Value.ToCanonicalName(),
                value => value == null ? (TripFailureReason?)null : TripFailureReasonRules.FromCanonicalName(value));

        builder.Property(e => e.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        // Lifecycle durability: the current matching
        // generation and the last-transition timestamp. The round starts at 0
        // (no round opened) and StartMatching advances it to 1; UpdatedAt
        // starts equal to CreatedAt and advances once per committed local
        // transition. Both are domain-advanced; persistence only stores them.
        builder.Property(e => e.CurrentMatchingRound)
            .HasColumnName("current_matching_round")
            .IsRequired();

        builder.Property(e => e.UpdatedAt)
            .HasColumnName("updated_at")
            .IsRequired();

        builder.ComplexProperty(e => e.Pickup, pickup =>
        {
            pickup.IsRequired();
            pickup.Property(p => p.Latitude)
                .HasColumnName("pickup_latitude")
                .IsRequired();
            pickup.Property(p => p.Longitude)
                .HasColumnName("pickup_longitude")
                .IsRequired();
        });

        builder.ComplexProperty(e => e.Dropoff, dropoff =>
        {
            dropoff.IsRequired();
            dropoff.Property(p => p.Latitude)
                .HasColumnName("dropoff_latitude")
                .IsRequired();
            dropoff.Property(p => p.Longitude)
                .HasColumnName("dropoff_longitude")
                .IsRequired();
        });

        builder.ComplexProperty(e => e.Fare, fare =>
        {
            fare.IsRequired();
            fare.Property(f => f.Currency)
                .HasColumnName("fare_currency")
                .IsRequired();
            fare.Property(f => f.Amount)
                .HasColumnName("fare_amount")
                .IsRequired();
            fare.Property(f => f.PricingPolicyVersion)
                .HasColumnName("fare_policy_version")
                .IsRequired();
            fare.Property(f => f.CreatedAt)
                .HasColumnName("fare_created_at")
                .IsRequired();
        });

        // Optional coherent assignment reference: all NULL when absent,
        // all NOT NULL when present (defense-in-depth). The complex
        // itself is optional; its scalar columns are nullable in the table
        // because the parent is optional even though the CLR scalars are
        // non-nullable value types. Do NOT mark the inner scalars optional:
        // EF rejects optional non-nullable value types.
        builder.ComplexProperty(e => e.Assignment, assignment =>
        {
            assignment.IsRequired(false);
            assignment.Property(a => a.DriverId)
                .HasColumnName("assigned_driver_id");
            assignment.Property(a => a.ReservationId)
                .HasColumnName("reservation_id");
            assignment.Property(a => a.MatchingRound)
                .HasColumnName("matching_round");
        });

        builder.ToTable(t =>
        {
            t.HasCheckConstraint(
                "CK_trips_assignment_coherence",
                "(assigned_driver_id IS NULL AND reservation_id IS NULL AND matching_round IS NULL) " +
                "OR (assigned_driver_id IS NOT NULL AND reservation_id IS NOT NULL AND matching_round IS NOT NULL)");
            // Structural state/assignment coherence (mirrors Trip.Reconstitute):
            // post-assignment states require a full assignment, pre-assignment
            // states require no assignment, CANCELLED allows either form.
            // Written as implications so unknown state values are not
            // separately policed here; partial assignments remain rejected
            // by CK_trips_assignment_coherence.
            t.HasCheckConstraint(
                "CK_trips_state_assignment_coherence",
                "(state NOT IN ('DRIVER_RESERVED', 'DRIVER_ACCEPTED', 'READY_TO_START', 'IN_PROGRESS', 'COMPLETED') " +
                "OR (assigned_driver_id IS NOT NULL AND reservation_id IS NOT NULL AND matching_round IS NOT NULL)) " +
                "AND (state NOT IN ('CREATED', 'MATCHING', 'MATCH_FAILED') " +
                "OR (assigned_driver_id IS NULL AND reservation_id IS NULL AND matching_round IS NULL))");
            t.HasCheckConstraint(
                "CK_trips_fare_amount_non_negative",
                "fare_amount >= 0");
            // Round durability: the current generation
            // never goes negative; CREATED never carries a round; every other
            // non-cancelled state went through StartMatching so it carries an
            // open round (CANCELLED is excluded so a future pre-matching
            // cancellation policy stays representable); an attached
            // reservation generation is always positive and always agrees
            // with the current round, so a stale generation can never be
            // persisted as current.
            t.HasCheckConstraint(
                "CK_trips_current_round_non_negative",
                "current_matching_round >= 0");
            t.HasCheckConstraint(
                "CK_trips_created_without_round",
                "(state <> 'CREATED' OR current_matching_round = 0)");
            t.HasCheckConstraint(
                "CK_trips_post_created_have_round",
                "(state NOT IN ('MATCHING', 'DRIVER_RESERVED', 'DRIVER_ACCEPTED', 'READY_TO_START', 'IN_PROGRESS', 'COMPLETED', 'MATCH_FAILED') " +
                "OR current_matching_round >= 1)");
            t.HasCheckConstraint(
                "CK_trips_assignment_round_positive",
                "(matching_round IS NULL OR matching_round >= 1)");
            t.HasCheckConstraint(
                "CK_trips_assignment_round_agreement",
                "(assigned_driver_id IS NULL OR matching_round = current_matching_round)");
            t.HasCheckConstraint(
                "CK_trips_failure_reason_valid",
                "(failure_reason IS NULL OR failure_reason IN ('NO_DRIVER_AVAILABLE', 'DRIVER_REJECTED'))");
            t.HasCheckConstraint(
                "CK_trips_failure_reason_only_match_failed",
                "(failure_reason IS NULL OR state = 'MATCH_FAILED')");
        });
    }

    private static ValueConverter<TripState, string> StateConverter() =>
        new(
            v => ToProvider(v),
            v => FromProvider(v));

    private static string ToProvider(TripState state) => state.ToCanonicalName();

    private static TripState FromProvider(string value) => value switch
    {
        "CREATED" => TripState.Created,
        "MATCHING" => TripState.Matching,
        "DRIVER_RESERVED" => TripState.DriverReserved,
        "DRIVER_ACCEPTED" => TripState.DriverAccepted,
        "READY_TO_START" => TripState.ReadyToStart,
        "IN_PROGRESS" => TripState.InProgress,
        "COMPLETED" => TripState.Completed,
        "CANCELLED" => TripState.Cancelled,
        "MATCH_FAILED" => TripState.MatchFailed,
        _ => throw new InvalidOperationException($"Unknown persisted TripState '{value}'."),
    };
}
