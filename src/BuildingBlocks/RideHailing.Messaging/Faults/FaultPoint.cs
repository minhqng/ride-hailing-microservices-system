using System.Collections.Concurrent;

namespace RideHailing.Messaging
{
    public interface IFaultPoint
    {
        Task HitAsync(string name, CancellationToken cancellationToken = default);
    }
}

namespace RideHailing.Messaging.Faults
{
    public interface IFaultPoint : RideHailing.Messaging.IFaultPoint { }
}

namespace RideHailing.Messaging.Testing.FaultPoints
{
    public interface IFaultPoint : RideHailing.Messaging.IFaultPoint { }
}

namespace RideHailing.Messaging.Faults
{
    public sealed class FaultPoint : RideHailing.Messaging.IFaultPoint,
        RideHailing.Messaging.Faults.IFaultPoint,
        RideHailing.Messaging.Testing.FaultPoints.IFaultPoint
    {
        public static readonly string[] KnownNames =
        [
            "Consumer.AfterBusinessCommitBeforeAck",
            "Outbox.AfterPublishBeforeMark",
            "Payment.AfterProviderEffectBeforeResponse",
            "Saga.AfterPersistBeforeDispatch",
            "Driver.AfterReserveCommitBeforeResponse"
        ];

        private readonly HashSet<string> enabled;
        private readonly ConcurrentDictionary<string, TaskCompletionSource> hits = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, TaskCompletionSource> releases = new(StringComparer.Ordinal);
        private readonly TimeSpan defaultTimeout;

        public FaultPoint(IEnumerable<string>? enabledNames = null, TimeSpan? defaultTimeout = null)
        {
            enabled = new HashSet<string>(enabledNames ?? EnabledFromEnvironment(), StringComparer.Ordinal);
            this.defaultTimeout = defaultTimeout ?? TimeSpan.FromSeconds(30);
        }

        public static FaultPoint FromEnvironment(TimeSpan? defaultTimeout = null) => new(EnabledFromEnvironment(), defaultTimeout);

        public static IReadOnlyCollection<string> EnabledFromEnvironment()
        {
            var raw = Environment.GetEnvironmentVariable("RIDE_FAULTPOINTS");
            if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();
            return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).ToArray();
        }

        public bool IsEnabled(string name) => enabled.Contains(name);

        public async Task HitAsync(string name, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(name) || !enabled.Contains(name)) return;
            var hit = hits.GetOrAdd(name, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            hit.TrySetResult();
            var release = releases.GetOrAdd(name, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(defaultTimeout);
            try { await release.Task.WaitAsync(timeout.Token); }
            finally { releases.TryRemove(name, out _); }
        }

        public async Task WaitForHitAsync(string name, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            var hit = hits.GetOrAdd(name, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout ?? defaultTimeout);
            await hit.Task.WaitAsync(cts.Token);
        }

        public void Release(string name)
        {
            var release = releases.GetOrAdd(name, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            release.TrySetResult();
        }

        public void Reset()
        {
            foreach (var hit in hits.Values) hit.TrySetCanceled();
            foreach (var release in releases.Values) release.TrySetCanceled();
            hits.Clear();
            releases.Clear();
        }
    }
}

namespace RideHailing.Messaging
{
    public static class FaultPoints
    {
        public static RideHailing.Messaging.IFaultPoint Current { get; set; } = new RideHailing.Messaging.Faults.FaultPoint();
    }

    public static class FaultPointNames
    {
        public const string ConsumerAfterBusinessCommitBeforeAck = "Consumer.AfterBusinessCommitBeforeAck";
        public const string OutboxAfterPublishBeforeMark = "Outbox.AfterPublishBeforeMark";
        public const string PaymentAfterProviderEffectBeforeResponse = "Payment.AfterProviderEffectBeforeResponse";
        public const string SagaAfterPersistBeforeDispatch = "Saga.AfterPersistBeforeDispatch";
        public const string DriverAfterReserveCommitBeforeResponse = "Driver.AfterReserveCommitBeforeResponse";
    }
}
