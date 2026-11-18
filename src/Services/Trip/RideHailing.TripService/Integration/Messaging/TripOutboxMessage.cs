// Trip-owned durable outgoing-intent row.
//
// The physical table is the SHARED Outbox contract (the relay layer owns
// claim/publish/confirm/mark/retry/metrics). Trip owns the
// semantic decision to create an intent and MUST create it in the same local
// transaction as the business transition it belongs to.
//
// Mapping rule: every column, type, constraint name and partial index below
// mirrors src/BuildingBlocks/RideHailing.Messaging/Outbox/Schema/
// 001_outbox_messages.sql exactly. The shared NpgsqlOutboxStore reads and
// mutates these rows with raw SQL, so any drift would silently break the
// relay. A compatibility test inserts through this EF mapping and reads
// through the shared store (and vice versa).
//
// This type is persistence bookkeeping, never a domain aggregate: the Trip
// service appends pending intents only. Status transition, claiming and
// retry are the relay's responsibility.

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RideHailing.Messaging.Outbox;

namespace RideHailing.TripService.Integration.Messaging;

/// <summary>One Trip-created pending Outbox intent.</summary>
public sealed class TripOutboxMessage
{
    public string MessageId { get; set; } = string.Empty;
    public string MessageType { get; set; } = string.Empty;
    public int SchemaVersion { get; set; }
    public string CorrelationId { get; set; } = string.Empty;
    public string? CausationId { get; set; }
    public string? AggregateType { get; set; }
    public string? AggregateId { get; set; }
    public long? AggregateVersion { get; set; }
    public string? SagaId { get; set; }
    public string? OperationKey { get; set; }
    public string PayloadJson { get; set; } = string.Empty;
    public string HeadersJson { get; set; } = "{}";
    public string Exchange { get; set; } = string.Empty;
    public string RoutingKey { get; set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; set; }
    public string Status { get; set; } = "PENDING";
    public int AttemptCount { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public string? ClaimedBy { get; set; }
    public DateTimeOffset? ClaimedAt { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public string? LastErrorCode { get; set; }
    public DateTimeOffset? LastErrorAt { get; set; }

    /// <summary>
    /// Maps the canonical shared intent (already serialized and validated by
    /// shared <see cref="OutboxMessage.Create"/>) onto the Trip EF row. Only
    /// fresh pending intents may be written here.
    /// </summary>
    public static TripOutboxMessage From(OutboxMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Status != OutboxStatus.Pending)
        {
            throw new ArgumentException("Trip may only enqueue pending Outbox intents.", nameof(message));
        }

        return new TripOutboxMessage
        {
            MessageId = message.MessageId,
            MessageType = message.MessageType,
            SchemaVersion = message.SchemaVersion,
            CorrelationId = message.CorrelationId,
            CausationId = message.CausationId,
            AggregateType = message.AggregateType,
            AggregateId = message.AggregateId,
            AggregateVersion = message.AggregateVersion,
            SagaId = message.SagaId,
            OperationKey = message.OperationKey,
            PayloadJson = message.PayloadJson,
            HeadersJson = message.HeadersJson,
            Exchange = message.Exchange,
            RoutingKey = message.RoutingKey,
            OccurredAt = message.OccurredAt,
            Status = OutboxMessage.ToDatabase(message.Status),
            AttemptCount = message.AttemptCount,
            NextAttemptAt = message.NextAttemptAt,
            ClaimedBy = message.ClaimedBy,
            ClaimedAt = message.ClaimedAt,
            PublishedAt = message.PublishedAt,
            LastErrorCode = message.LastErrorCode,
            LastErrorAt = message.LastErrorAt,
        };
    }
}

internal sealed class TripOutboxMessageConfiguration : IEntityTypeConfiguration<TripOutboxMessage>
{
    public void Configure(EntityTypeBuilder<TripOutboxMessage> b)
    {
        b.ToTable("outbox_messages", table =>
        {
            table.HasCheckConstraint(
                "outbox_messages_status_check",
                "status IN ('PENDING', 'PUBLISHED', 'FAILED_VISIBLE')");
            table.HasCheckConstraint(
                "outbox_messages_schema_version_check",
                "schema_version >= 1");
            table.HasCheckConstraint(
                "outbox_messages_attempt_count_check",
                "attempt_count >= 0");
            table.HasCheckConstraint(
                "outbox_messages_identity_nonempty",
                "btrim(message_id) <> '' AND btrim(message_type) <> '' AND " +
                "btrim(correlation_id) <> '' AND btrim(exchange) <> '' AND btrim(routing_key) <> ''");
        });

        // Shared contract primary key name (not EF's default PK_outbox_messages).
        b.HasKey(x => x.MessageId).HasName("outbox_messages_pk");

        b.Property(x => x.MessageId).HasColumnName("message_id").HasColumnType("text")
            .ValueGeneratedNever().IsRequired();
        b.Property(x => x.MessageType).HasColumnName("message_type").HasColumnType("text").IsRequired();
        b.Property(x => x.SchemaVersion).HasColumnName("schema_version").IsRequired();
        b.Property(x => x.CorrelationId).HasColumnName("correlation_id").HasColumnType("text").IsRequired();
        b.Property(x => x.CausationId).HasColumnName("causation_id").HasColumnType("text");
        b.Property(x => x.AggregateType).HasColumnName("aggregate_type").HasColumnType("text");
        b.Property(x => x.AggregateId).HasColumnName("aggregate_id").HasColumnType("text");
        b.Property(x => x.AggregateVersion).HasColumnName("aggregate_version");
        b.Property(x => x.SagaId).HasColumnName("saga_id").HasColumnType("text");
        b.Property(x => x.OperationKey).HasColumnName("operation_key").HasColumnType("text");
        b.Property(x => x.PayloadJson).HasColumnName("payload_json").HasColumnType("jsonb").IsRequired();
        b.Property(x => x.HeadersJson).HasColumnName("headers_json").HasColumnType("jsonb")
            .HasDefaultValueSql("'{}'::jsonb").IsRequired();
        b.Property(x => x.Exchange).HasColumnName("exchange").HasColumnType("text").IsRequired();
        b.Property(x => x.RoutingKey).HasColumnName("routing_key").HasColumnType("text").IsRequired();
        b.Property(x => x.OccurredAt).HasColumnName("occurred_at").HasColumnType("timestamp with time zone").IsRequired();
        b.Property(x => x.Status).HasColumnName("status").HasColumnType("text")
            .HasDefaultValue("PENDING").IsRequired();
        b.Property(x => x.AttemptCount).HasColumnName("attempt_count").HasDefaultValue(0).IsRequired();
        b.Property(x => x.NextAttemptAt).HasColumnName("next_attempt_at")
            .HasColumnType("timestamp with time zone").IsRequired();
        b.Property(x => x.ClaimedBy).HasColumnName("claimed_by").HasColumnType("text");
        b.Property(x => x.ClaimedAt).HasColumnName("claimed_at").HasColumnType("timestamp with time zone");
        b.Property(x => x.PublishedAt).HasColumnName("published_at").HasColumnType("timestamp with time zone");
        b.Property(x => x.LastErrorCode).HasColumnName("last_error_code").HasColumnType("text");
        b.Property(x => x.LastErrorAt).HasColumnName("last_error_at").HasColumnType("timestamp with time zone");

        // Relay claim scan and operator failure view (shared contract names).
        b.HasIndex(x => new { x.NextAttemptAt, x.OccurredAt, x.MessageId })
            .HasDatabaseName("ix_outbox_messages_claim")
            .HasFilter("status = 'PENDING'");
        b.HasIndex(x => new { x.OccurredAt, x.MessageId })
            .HasDatabaseName("ix_outbox_messages_failed_visible")
            .HasFilter("status = 'FAILED_VISIBLE'");
    }
}
