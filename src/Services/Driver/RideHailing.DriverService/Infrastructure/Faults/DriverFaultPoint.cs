// Deterministic fault seam for focused tests. The adapter keeps the frozen
// fault-point name and a matching HitAsync shape so it can delegate to the
// shared IFaultPoint when that contract lands.
//
// Safety: enabled only under the explicit Testing environment, only for
// known names, never via an HTTP activation endpoint, and every hold is
// bounded. Non-Testing processes are inert even if the variable is set.
using Microsoft.Extensions.Hosting;

namespace RideHailing.DriverService.Infrastructure.Faults;

public static class DriverFaultPointNames
{
    public const string AfterReserveCommitBeforeResponse = "Driver.AfterReserveCommitBeforeResponse";
}

public sealed record DriverFaultSnapshot(
    bool Enabled,
    IReadOnlyList<string> EnabledNames,
    long ReachedCount,
    int PendingCount,
    DateTimeOffset? LastReachedAtUtc);

public sealed class DriverFaultPoint : RideHailing.Messaging.IFaultPoint
{
    public const string EnabledNamesVariable = "RIDE_FAULTPOINTS";
    public const string MaxHoldSecondsVariable = "RIDE_FAULT_MAX_HOLD_SECONDS";
    public const string ControlTokenVariable = "RIDE_FAULT_CONTROL_TOKEN";

    private static readonly string[] AllNames =
    [
        DriverFaultPointNames.AfterReserveCommitBeforeResponse,
    ];

    private static readonly TimeSpan DefaultMaxHold = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MinMaxHold = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxMaxHold = TimeSpan.FromSeconds(120);

    private readonly HashSet<string> _enabled;
    private readonly TimeSpan _maxHold;
    private readonly ILogger<DriverFaultPoint>? _logger;
    private readonly object _gate = new();
    private readonly List<TaskCompletionSource> _pending = [];
    private readonly TaskCompletionSource _firstReached =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _reachedCount;
    private DateTimeOffset? _lastReachedAtUtc;

    private DriverFaultPoint(
        HashSet<string> enabled, TimeSpan maxHold, ILogger<DriverFaultPoint>? logger)
    {
        _enabled = enabled;
        _maxHold = maxHold;
        _logger = logger;
    }

    public bool Enabled => _enabled.Count > 0;

    public static DriverFaultPoint Disabled(ILogger<DriverFaultPoint>? logger = null) =>
        new([], DefaultMaxHold, logger);

    /// <summary>Test-only constructor; callers must pass an explicit name set.</summary>
    public static DriverFaultPoint ForTesting(
        IEnumerable<string> names, TimeSpan maxHold, ILogger<DriverFaultPoint>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(names);
        if (maxHold <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maxHold));
        }

        var enabled = names
            .Where(name => AllNames.Contains(name, StringComparer.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
        return new DriverFaultPoint(enabled, maxHold, logger);
    }

    public static DriverFaultPoint FromEnvironment(
        IHostEnvironment environment, ILogger<DriverFaultPoint>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(environment);

        // Production is always inert. Testing is the only explicit opt-in.
        if (!environment.IsEnvironment("Testing"))
        {
            return Disabled(logger);
        }

        var requested = (Environment.GetEnvironmentVariable(EnabledNamesVariable) ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var enabled = requested
            .Where(name => AllNames.Contains(name, StringComparer.Ordinal))
            .ToHashSet(StringComparer.Ordinal);

        // An enabled hold without a control channel would be releasable only by
        // the timeout. Fail closed instead: a misconfigured Testing process is
        // inert rather than silently parking committed reserves.
        if (enabled.Count > 0
            && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ControlTokenVariable)))
        {
            logger?.LogWarning(
                "Driver fault point requested without {Variable}; fault seam disabled.",
                ControlTokenVariable);
            return Disabled(logger);
        }

        var rawHold = Environment.GetEnvironmentVariable(MaxHoldSecondsVariable);
        var maxHold = int.TryParse(rawHold, out var seconds)
            ? TimeSpan.FromSeconds(Math.Clamp(seconds, (int)MinMaxHold.TotalSeconds, (int)MaxMaxHold.TotalSeconds))
            : DefaultMaxHold;

        return new DriverFaultPoint(enabled, maxHold, logger);
    }

    public bool IsEnabledFor(string name) => _enabled.Contains(name);

    /// <summary>
    /// Blocks the caller at a named fault point until <see cref="Release"/> or
    /// the bounded hold expires. Must only be called after the authoritative
    /// transaction has committed.
    /// </summary>
    public async Task HitAsync(string name, CancellationToken cancellationToken = default)
    {
        if (!IsEnabledFor(name))
        {
            return;
        }

        // The request token is deliberately not linked: the proof needs the
        // committed effect to stay held after the caller times out or dies.
        // The bounded hold is the fail-safe, not client cancellation.
        _ = cancellationToken;

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _pending.Add(completion);
            _reachedCount++;
            _lastReachedAtUtc = DateTimeOffset.UtcNow;
        }

        _firstReached.TrySetResult();
        _logger?.LogWarning(
            "Driver fault point {FaultPoint} reached; holding post-commit response for at most {MaxHold}.",
            name, _maxHold);

        try
        {
            await completion.Task.WaitAsync(_maxHold);
        }
        catch (TimeoutException)
        {
            lock (_gate)
            {
                _pending.Remove(completion);
            }

            _logger?.LogWarning(
                "Driver fault point {FaultPoint} bounded hold expired after {MaxHold}; releasing response.",
                name, _maxHold);
        }
    }

    public bool Release(string name)
    {
        if (!IsEnabledFor(name))
        {
            return false;
        }

        TaskCompletionSource completion;
        lock (_gate)
        {
            if (_pending.Count == 0)
            {
                return false;
            }

            completion = _pending[0];
            _pending.RemoveAt(0);
        }

        completion.TrySetResult();
        _logger?.LogWarning("Driver fault point {FaultPoint} released by test control.", name);
        return true;
    }

    public DriverFaultSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new DriverFaultSnapshot(
                Enabled,
                _enabled.OrderBy(name => name, StringComparer.Ordinal).ToArray(),
                _reachedCount,
                _pending.Count,
                _lastReachedAtUtc);
        }
    }

    public async Task WaitForReachedAsync(
        TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _reachedCount) > 0)
        {
            return;
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        await _firstReached.Task.WaitAsync(timeoutSource.Token);
    }
}
