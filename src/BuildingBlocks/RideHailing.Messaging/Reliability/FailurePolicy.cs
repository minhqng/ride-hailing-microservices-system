namespace RideHailing.Messaging.Reliability;

public enum FailureKind { Validation, Authorization, DomainConflict, Transient, UnknownEffect, Poison, ResourcePressure, Configuration }
public enum RecoveryAction { Reject, Retry, Reconcile, DeadLetter }

public sealed record RetryMetadata(int Attempt, int MaxAttempts = 3)
{
    public void Validate()
    {
        if (Attempt < 0 || MaxAttempts is < 1 or > 10 || Attempt > MaxAttempts)
            throw new ArgumentException("Invalid bounded retry metadata");
    }
}

public static class FailurePolicy
{
    public static RecoveryAction Decide(FailureKind kind, RetryMetadata retry)
    {
        retry.Validate();
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        return kind switch
        {
            FailureKind.UnknownEffect => RecoveryAction.Reconcile,
            FailureKind.Poison or FailureKind.Configuration => RecoveryAction.DeadLetter,
            FailureKind.Validation or FailureKind.Authorization or FailureKind.DomainConflict => RecoveryAction.Reject,
            _ => retry.Attempt < retry.MaxAttempts ? RecoveryAction.Retry : RecoveryAction.DeadLetter
        };
    }
    public static TimeSpan Delay(int attempt) => TimeSpan.FromMilliseconds(100 * Math.Pow(2, Math.Clamp(attempt, 0, 5)));
}

// Safe metadata only: never serialize raw payloads, secrets or exception stack traces to error queues.
public sealed record DeliveryError(string OriginalMessageId, string CorrelationId, string MessageType,
    string ErrorCode, FailureKind Kind, int Attempts, DateTimeOffset OccurredAt);

public sealed class DeliveryFailureException(FailureKind kind, string code) : Exception(code)
{
    public FailureKind Kind { get; } = kind;
    public string Code { get; } = code;
}
