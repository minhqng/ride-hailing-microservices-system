// Saga.AfterPersistBeforeDispatch deterministic gate.
//
// FROZEN INTEGRATION CONTRACT — until the shared FaultPointProvider lands,
// this module is the agreed shape of the
// Trip-side integration, designed so the shared abstraction can adopt it
// without a Trip rewrite:
//
//  1. NAME "Saga.AfterPersistBeforeDispatch" (spelling).
//   2. LOCATION  The owning service's Outbox relay pre-publish seam (the same
//                boundary as the platform's Outbox.AfterPublishBeforeMark, one step
//                earlier). The relay only sees rows a committed transaction
//                produced, so the faultpoint is necessarily after-commit; it
//                never wraps business code, never opens a transaction and
//  never mutates domain state.
//   3. SCOPE     The saga's outbound payment dispatches (Authorize/Capture/
//                Void). Non-saga dispatches are not gated by this name.
//   4. ACTIVATION Double-gated and default-off: configuration
//                TripFaultPoints:Enabled (+ explicit Activated list) AND a
//                non-Production host environment. Enabling it in Production
//                fails startup. No HTTP surface exists at all — the
//                faultpoint can never be activated by an untrusted request.
//   5. SIGNALING Observable "reached": a marker file
//                <SignalDirectory>/faultpoint-<name>.reached (atomic
//                write-through, content = faultPoint, processId, messageId,
//                reachedAtUtc). Continue: <name>.release appears. The test
//                harness owns the directory; both paths are inert when the
//                faultpoint is disabled.
//   6. WAIT      Bounded (TripFaultPoints:WaitTimeoutSeconds, default 30,
//                capped at 600): a held dispatch is released by the deadline
//                with a warning even if no release signal arrives. The
//                faultpoint never blocks forever and never requires
//                debugger timing or human keypresses.
//
// When the shared platform lands, items 4-6 move behind its provider; the
// name, location and scope above are the frozen semantics.

using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace RideHailing.TripService.Integration.FaultPoints;

/// <summary>Test-only faultpoint configuration bound from TripFaultPoints:.</summary>
public sealed record TripFaultPointSettings
{
    public bool Enabled { get; init; }

    /// <summary>Directory the reached/release marker files live in (test-owned).</summary>
    public string SignalDirectory { get; init; } = string.Empty;

    /// <summary>Canonical names activated for this host (see TripFaultPointNames.All).</summary>
    public IReadOnlyList<string> Activated { get; init; } = [];

    /// <summary>Bounded hold; a held dispatch is released at the deadline.</summary>
    public int WaitTimeoutSeconds { get; init; } = 30;

    public int PollIntervalMs { get; init; } = 100;

    public static TripFaultPointSettings Disabled { get; } = new();

    /// <summary>
    /// Binds and validates the TripFaultPoints section. Misconfiguration is
    /// a startup fault, never a silent no-op: an unknown activated name or a
    /// missing/invalid setting must fail the test run loudly.
    /// </summary>
    public static TripFaultPointSettings FromConfiguration(IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var section = config.GetSection("TripFaultPoints");
        if (!section.GetValue<bool>("Enabled"))
        {
            return Disabled;
        }

        var signalDirectory = section["SignalDirectory"];
        if (string.IsNullOrWhiteSpace(signalDirectory))
        {
            throw new InvalidOperationException(
                "TripFaultPoints:SignalDirectory is required when TripFaultPoints:Enabled=true.");
        }

        var activated = section.GetSection("Activated").Get<string[]>() ?? [];
        if (activated.Length == 0)
        {
            throw new InvalidOperationException(
                "TripFaultPoints:Activated must list at least one canonical faultpoint name when enabled.");
        }

        var unknown = activated.FirstOrDefault(n => !TripFaultPointNames.All.Contains(n));
        if (unknown is not null)
        {
            throw new InvalidOperationException(
                $"TripFaultPoints:Activated contains unknown faultpoint '{unknown}'; known: " +
                string.Join(", ", TripFaultPointNames.All.Order()) + ".");
        }

        var timeoutSeconds = section.GetValue("WaitTimeoutSeconds", 30);
        if (timeoutSeconds is < 1 or > 600)
        {
            throw new InvalidOperationException(
                "TripFaultPoints:WaitTimeoutSeconds must be between 1 and 600 (bounded wait is mandatory).");
        }

        var pollMs = section.GetValue("PollIntervalMs", 100);
        if (pollMs is < 10 or > 5000)
        {
            throw new InvalidOperationException(
                "TripFaultPoints:PollIntervalMs must be between 10 and 5000.");
        }

        return new TripFaultPointSettings
        {
            Enabled = true,
            SignalDirectory = signalDirectory,
            Activated = activated,
            WaitTimeoutSeconds = timeoutSeconds,
            PollIntervalMs = pollMs,
        };
    }
}

/// <summary>
/// Holds the relay immediately before dispatching a gated saga intent, after
/// the reached marker is observable. Inert unless activated; never blocks
/// past the configured deadline.
/// </summary>
public sealed class TripFaultPointGate
{
    private readonly TripFaultPointSettings _settings;
    private readonly ILogger<TripFaultPointGate> _log;
    private readonly TimeProvider _clock;

    public TripFaultPointGate(TripFaultPointSettings settings, ILogger<TripFaultPointGate> log,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(log);
        if (settings.Enabled && string.IsNullOrWhiteSpace(settings.SignalDirectory))
        {
            throw new ArgumentException("An enabled gate requires SignalDirectory.", nameof(settings));
        }

        _settings = settings;
        _log = log;
        _clock = clock ?? TimeProvider.System;
    }

    public static string ReachedPath(string signalDirectory, string faultPoint) =>
        Path.Combine(signalDirectory, $"faultpoint-{FileSafe(faultPoint)}.reached");

    public static string ReleasePath(string signalDirectory, string faultPoint) =>
        Path.Combine(signalDirectory, $"faultpoint-{FileSafe(faultPoint)}.release");

    /// <summary>
    /// The relay's pre-publish seam: holds a gated saga dispatch after commit
    /// and before publication. Called for every claimed message; returns
    /// immediately for anything this faultpoint does not gate.
    /// </summary>
    public async Task HoldBeforePublishAsync(
        string faultPoint, string messageType, string messageId, CancellationToken token)
    {
        if (!_settings.Enabled || !_settings.Activated.Contains(faultPoint) ||
            !TripFaultPointNames.IsSagaDispatch(messageType))
        {
            return;
        }

        Directory.CreateDirectory(_settings.SignalDirectory);
        var marker = new
        {
            faultPoint,
            processId = Environment.ProcessId,
            messageId,
            messageType,
            reachedAtUtc = _clock.GetUtcNow(),
        };
        // Marker existence is the reached signal; write through a temp file so
        // an observer can never see a partially written marker.
        var temp = ReachedPath(_settings.SignalDirectory, faultPoint) + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(marker), token);
        File.Move(temp, ReachedPath(_settings.SignalDirectory, faultPoint), overwrite: true);
        // Never log payloads; the stable message identity is the diagnostic.
        _log.LogInformation(
            "Faultpoint {FaultPoint} holding dispatch {MessageId} ({MessageType}) before publication.",
            faultPoint, messageId, messageType);

        var release = ReleasePath(_settings.SignalDirectory, faultPoint);
        var waited = TimeSpan.Zero;
        var poll = TimeSpan.FromMilliseconds(_settings.PollIntervalMs);
        var deadline = TimeSpan.FromSeconds(_settings.WaitTimeoutSeconds);
        while (!File.Exists(release))
        {
            if (token.IsCancellationRequested)
            {
                _log.LogWarning("Faultpoint {FaultPoint} wait abandoned: host is stopping.", faultPoint);
                return;
            }

            if (waited >= deadline)
            {
                _log.LogWarning(
                    "Faultpoint {FaultPoint} bounded wait of {Seconds}s expired; dispatch continues.",
                    faultPoint, _settings.WaitTimeoutSeconds);
                return;
            }

            await Task.Delay(poll, token);
            waited += poll;
        }

        _log.LogInformation("Faultpoint {FaultPoint} released; dispatch continues.", faultPoint);
    }

    private static string FileSafe(string faultPoint)
    {
        var chars = faultPoint.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (!char.IsAsciiLetterOrDigit(chars[i]) && chars[i] != '-' && chars[i] != '.')
            {
                chars[i] = '_';
            }
        }

        return new string(chars);
    }
}
