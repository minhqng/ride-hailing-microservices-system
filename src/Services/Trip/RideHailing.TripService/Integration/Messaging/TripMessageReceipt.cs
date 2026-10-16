using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace RideHailing.TripService.Integration.Messaging;

// Trip-local durable consumer result. PendingWorkflow/NeedsReview retains the
// complete envelope for reconciliation; ACK never means the business succeeded.
public sealed class TripMessageReceipt
{
    public string Consumer { get; set; } = "";
    public string MessageId { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public Guid TripId { get; set; }
    public string MessageType { get; set; } = "";
    public string CorrelationId { get; set; } = "";
    public string EnvelopeJson { get; set; } = "";
    public string Disposition { get; set; } = "Processing";
    public string Reason { get; set; } = "";
    public DateTimeOffset RecordedAt { get; set; }
}

internal sealed class TripMessageReceiptConfiguration : IEntityTypeConfiguration<TripMessageReceipt>
{
    public void Configure(EntityTypeBuilder<TripMessageReceipt> b)
    {
        b.ToTable("trip_message_receipts");
        b.HasKey(x => new { x.Consumer, x.MessageId });
        b.Property(x => x.Consumer).HasColumnName("consumer").HasMaxLength(80);
        b.Property(x => x.MessageId).HasColumnName("message_id").HasMaxLength(256);
        b.Property(x => x.Fingerprint).HasColumnName("fingerprint").HasMaxLength(64);
        b.Property(x => x.TripId).HasColumnName("trip_id");
        b.Property(x => x.MessageType).HasColumnName("message_type").HasMaxLength(100);
        b.Property(x => x.CorrelationId).HasColumnName("correlation_id").HasMaxLength(256);
        b.Property(x => x.EnvelopeJson).HasColumnName("envelope_json").HasColumnType("jsonb");
        b.Property(x => x.Disposition).HasColumnName("disposition").HasMaxLength(40);
        b.Property(x => x.Reason).HasColumnName("reason").HasMaxLength(100);
        b.Property(x => x.RecordedAt).HasColumnName("recorded_at");
        b.HasIndex(x => new { x.Disposition, x.RecordedAt });
        b.HasIndex(x => x.TripId);
        // No FK: an out-of-order fact for a missing Trip must remain visible.
    }
}
