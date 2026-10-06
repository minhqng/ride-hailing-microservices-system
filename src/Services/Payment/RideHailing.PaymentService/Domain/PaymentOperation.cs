namespace RideHailing.PaymentService.Domain;

public enum PaymentOperationType
{
    Authorize,
    Capture,
    Void
}

public enum PaymentOperationState
{
    Pending,
    Processing,
    Succeeded,
    Declined,
    Unknown,
    RequiresReview
}

public enum PaymentSimulatorMode
{
    Success,
    Decline,
    TransientFailure,
    TimeoutBeforeEffect,
    TimeoutAfterEffect,
    SlowSuccess
}

public sealed record PaymentOperation
{
    public Guid Id { get; private init; }
    public string OperationKey { get; private init; }
    public Guid TripId { get; private init; }
    public PaymentOperationType Type { get; private init; }
    public long AmountMinor { get; private init; }
    public string Currency { get; private init; }
    public PaymentOperationState State { get; private init; }
    public bool ProviderEffectOccurred { get; private init; }
    public string? ProviderEffectId { get; private init; }
    public string? ResultCode { get; private init; }
    public int AttemptCount { get; private init; }
    public DateTimeOffset CreatedAt { get; private init; }
    public DateTimeOffset UpdatedAt { get; private init; }
    public DateTimeOffset? CompletedAt { get; private init; }

    private PaymentOperation(Guid id, string operationKey, Guid tripId, PaymentOperationType type, long amountMinor, string currency, PaymentOperationState state, bool providerEffectOccurred, string? providerEffectId, string? resultCode, int attemptCount, DateTimeOffset createdAt, DateTimeOffset updatedAt, DateTimeOffset? completedAt)
    {
        Id = id;
        OperationKey = operationKey;
        TripId = tripId;
        Type = type;
        AmountMinor = amountMinor;
        Currency = currency;
        State = state;
        ProviderEffectOccurred = providerEffectOccurred;
        ProviderEffectId = providerEffectId;
        ResultCode = resultCode;
        AttemptCount = attemptCount;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
        CompletedAt = completedAt;
    }

    // Storage-only entry point. Reject corrupt persisted state before exposing the aggregate.
    internal static PaymentOperation Restore(Guid id, string operationKey, Guid tripId, PaymentOperationType type, long amountMinor, string currency, PaymentOperationState state, bool providerEffectOccurred, string? providerEffectId, string? resultCode, int attemptCount, DateTimeOffset createdAt, DateTimeOffset updatedAt, DateTimeOffset? completedAt)
    {
        if (id == Guid.Empty || tripId == Guid.Empty || string.IsNullOrWhiteSpace(operationKey)
            || !Enum.IsDefined(type) || !Enum.IsDefined(state) || amountMinor <= 0
            || currency is null || currency.Length != 3 || currency.Any(c => !char.IsAsciiLetterUpper(c))
            || attemptCount < 0 || createdAt == default || updatedAt < createdAt
            || (state != PaymentOperationState.Pending && attemptCount == 0)
            || providerEffectOccurred != !string.IsNullOrWhiteSpace(providerEffectId)
            || (state == PaymentOperationState.Succeeded && !providerEffectOccurred)
            || ((state is PaymentOperationState.Pending or PaymentOperationState.Processing or PaymentOperationState.Declined) && providerEffectOccurred)
            || ((state is PaymentOperationState.Succeeded or PaymentOperationState.Declined) != completedAt.HasValue)
            || (completedAt.HasValue && (completedAt < createdAt || completedAt > updatedAt)))
            throw new ArgumentException("Invalid persisted Payment operation.");
        return new PaymentOperation(id, operationKey, tripId, type, amountMinor, currency, state, providerEffectOccurred, providerEffectId, resultCode, attemptCount, createdAt, updatedAt, completedAt);
    }

    public static PaymentOperation Create(
        string operationKey,
        Guid tripId,
        PaymentOperationType type,
        long amountMinor,
        string currency,
        DateTimeOffset now)
    {
        if (now == default) throw new ArgumentException("Timestamp is required", nameof(now));
        if (string.IsNullOrWhiteSpace(operationKey)) throw new ArgumentException("operationKey is required", nameof(operationKey));
        if (tripId == Guid.Empty) throw new ArgumentException("tripId is required", nameof(tripId));
        if (!Enum.IsDefined(type)) throw new ArgumentOutOfRangeException(nameof(type));
        if (amountMinor <= 0) throw new ArgumentOutOfRangeException(nameof(amountMinor));
        if (string.IsNullOrWhiteSpace(currency) || currency.Length != 3 || currency.Any(c => !char.IsAsciiLetterUpper(c)))
            throw new ArgumentException("currency must be a three-letter uppercase code", nameof(currency));

        return new PaymentOperation(Guid.NewGuid(), operationKey, tripId, type, amountMinor, currency,
            PaymentOperationState.Pending, false, null, null, 0, now, now, null);
    }

    public PaymentOperation ObserveUnknown(DateTimeOffset now)
    {
        if (State != PaymentOperationState.Processing || now < UpdatedAt) throw new InvalidOperationException("Only an active attempt can become UNKNOWN");
        return this with { State = PaymentOperationState.Unknown, ResultCode = "PROVIDER_RESPONSE_LOST", UpdatedAt = now };
    }

    public PaymentOperation Reconcile(string? effectId, DateTimeOffset now)
    {
        if (State is not (PaymentOperationState.Unknown or PaymentOperationState.Processing) || now < UpdatedAt)
            throw new InvalidOperationException("Only uncertain operations can be reconciled");
        if (effectId is not null && string.IsNullOrWhiteSpace(effectId)) throw new ArgumentException("Invalid provider reference");
        return effectId is null
            ? this with { State = PaymentOperationState.Pending, ResultCode = "NO_PROVIDER_EFFECT", UpdatedAt = now }
            : this with { State = PaymentOperationState.Succeeded, ProviderEffectOccurred = true,
                ProviderEffectId = effectId, ResultCode = "APPROVED", CompletedAt = now, UpdatedAt = now };
    }

    public PaymentOperation RequireReview(DateTimeOffset now)
    {
        if (State is not (PaymentOperationState.Pending or PaymentOperationState.Unknown) || AttemptCount == 0 || now < UpdatedAt)
            throw new InvalidOperationException("Only unresolved attempted operations may require review");
        return this with { State = PaymentOperationState.RequiresReview, ResultCode = "RETRY_EXHAUSTED", UpdatedAt = now };
    }

    public PaymentOperation BeginAttempt(DateTimeOffset now)
    {
        if (now < UpdatedAt) throw new ArgumentException("Time cannot move backwards", nameof(now));
        if (State != PaymentOperationState.Pending)
            throw new InvalidOperationException($"Only a pending operation can start execution; {State} requires its own resolution path.");

        return this with { State = PaymentOperationState.Processing, AttemptCount = AttemptCount + 1, UpdatedAt = now };
    }

    public PaymentOperation Resolve(PaymentSimulatorMode mode, DateTimeOffset now)
    {
        if (now < UpdatedAt) throw new ArgumentException("Time cannot move backwards", nameof(now));
        if (State != PaymentOperationState.Processing)
            throw new InvalidOperationException("An operation must be processing before it can be resolved.");

        return mode switch
        {
            PaymentSimulatorMode.Success or PaymentSimulatorMode.SlowSuccess => this with
            {
                State = PaymentOperationState.Succeeded,
                ProviderEffectOccurred = true,
                ProviderEffectId = ProviderEffectId ?? $"sim-{Id:N}",
                ResultCode = "APPROVED",
                CompletedAt = now,
                UpdatedAt = now
            },
            PaymentSimulatorMode.Decline => this with
            {
                State = PaymentOperationState.Declined,
                ResultCode = "DECLINED",
                CompletedAt = now,
                UpdatedAt = now
            },
            PaymentSimulatorMode.TransientFailure => this with
            {
                State = PaymentOperationState.Pending,
                ResultCode = "TRANSIENT_FAILURE",
                UpdatedAt = now
            },
            PaymentSimulatorMode.TimeoutBeforeEffect => this with
            {
                State = PaymentOperationState.Unknown,
                ResultCode = "TIMEOUT_BEFORE_EFFECT",
                UpdatedAt = now
            },
            PaymentSimulatorMode.TimeoutAfterEffect => this with
            {
                State = PaymentOperationState.Unknown,
                ProviderEffectOccurred = true,
                ProviderEffectId = ProviderEffectId ?? $"sim-{Id:N}",
                ResultCode = "TIMEOUT_AFTER_EFFECT",
                UpdatedAt = now
            },
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };
    }
}
