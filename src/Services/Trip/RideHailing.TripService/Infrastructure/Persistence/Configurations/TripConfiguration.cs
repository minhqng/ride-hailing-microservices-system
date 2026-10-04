// Trip aggregate EF mapping (A-W03-07).
//
// Authoritative W3 persistence for the Trip aggregate only. Value objects
// map into the same trips table as ComplexProperty columns (relational, not
// JSON); no separate value-object tables, no entity keys for value objects,
// no Driver/Matching/Payment/request-idempotency/Saga/Outbox/Inbox tables.
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

        // Application-managed optimistic-concurrency token: the domain
        // advances Version once per committed transition; persistence only
        // compares the original value and never increments it.
        builder.Property(e => e.Version)
            .HasColumnName("version")
            .IsRequired()
            .ValueGeneratedNever()
            .IsConcurrencyToken();

        builder.Property(e => e.CreatedAt)
            .HasColumnName("created_at")
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
        // all NOT NULL when present (DSI-009 defense-in-depth). The complex
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
