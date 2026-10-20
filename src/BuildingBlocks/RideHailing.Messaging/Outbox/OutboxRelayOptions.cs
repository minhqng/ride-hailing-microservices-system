namespace RideHailing.Messaging.Outbox;

public sealed class OutboxRelayOptions
{
    public string RelayId { get; init; } = $"{Environment.MachineName}:{Guid.NewGuid():N}";
    public int BatchSize { get; init; } = 8;
    public TimeSpan ClaimLease { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);
    public int MaxAttempts { get; init; } = 5;
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Deterministic test seam for REL-18: runs after claim and before publish.
    /// A throw simulates process death, leaving the row claimed until lease
    /// expiry. Production leaves this null.
    /// </summary>
    public Func<OutboxMessage, CancellationToken, Task>? BeforePublish { get; init; }

    /// <summary>
    /// Deterministic test seam for REL-05: runs after the publisher confirm and
    /// before the local mark. A throw simulates a lost mark, leaving the row
    /// pending for a safe same-identity republish. Production leaves this null.
    /// </summary>
    public Func<OutboxMessage, CancellationToken, Task>? AfterPublishBeforeMark { get; init; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(RelayId)) throw new ArgumentException("relayId is required");
        if (BatchSize is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(BatchSize));
        if (ClaimLease < TimeSpan.Zero || ClaimLease > TimeSpan.FromDays(1))
            throw new ArgumentOutOfRangeException(nameof(ClaimLease));
        if (PollInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(PollInterval));
        if (MaxAttempts is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(MaxAttempts));
        if (RetryDelay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(RetryDelay));
    }
}
