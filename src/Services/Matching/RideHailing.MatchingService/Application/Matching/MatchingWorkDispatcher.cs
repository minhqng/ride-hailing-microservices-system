// Autonomous production execution for durable matching rounds.
//
// The broker intake commits the round + pickup + auto_execute before ACK.
// This hosted worker is the only production trigger: it discovers due rounds
// from PostgreSQL, takes a cross-process advisory lease, then drives the
// durable state machine:
//
//   unresolved attempt -> same-R reconcile (never a new Driver)
//   reserved attempt   -> converge the round mark
//   READY attempts     -> serial reserve orchestration
//   no stage work yet  -> discover/freeze the current stage atomically
//   stage exhausted    -> durable S0->S1->S2 advance, or final NO_DRIVER
//   cancel requested   -> query-only reconcile, then CANCELLED / RESERVED
//
// Every decision is durable, so a process kill at any point is recovered by
// the next scan. Work is never queued in memory and never dependent on a
// testing endpoint.
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RideHailing.MatchingService.Application.Reserve;
using RideHailing.MatchingService.Domain.Matching;
using RideHailing.MatchingService.Infrastructure.Persistence;

namespace RideHailing.MatchingService.Application.Matching;

public sealed class MatchingWorkDispatcher : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly MatchingAttemptRepository _attempts;
    private readonly MatchingWorkRepository _work;
    private readonly MatchingStageService _stages;
    private readonly MatchingTerminalizer _terminalizer;
    private readonly MatchingPolicy _policy;
    private readonly MatchingWorkerOptions _options;
    private readonly TimeProvider _clock;
    private readonly string _connectionString;
    private readonly ILogger<MatchingWorkDispatcher> _logger;

    public MatchingWorkDispatcher(
        IServiceScopeFactory scopes,
        MatchingAttemptRepository attempts,
        MatchingWorkRepository work,
        MatchingStageService stages,
        MatchingTerminalizer terminalizer,
        IOptions<MatchingPolicy> policy,
        IOptions<MatchingWorkerOptions> options,
        TimeProvider clock,
        string connectionString,
        ILogger<MatchingWorkDispatcher> logger)
    {
        _scopes = scopes;
        _attempts = attempts;
        _work = work;
        _stages = stages;
        _terminalizer = terminalizer;
        _policy = policy.Value;
        _options = options.Value;
        _clock = clock;
        _connectionString = connectionString;
        _logger = logger;
    }

    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        var due = await _work.ListDueRoundsAsync(
            _clock.GetUtcNow() - _options.ReconcileBackoff, _options.BatchSize, ct);
        await Parallel.ForEachAsync(
            due,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = _options.MaxConcurrentRounds,
                CancellationToken = ct,
            },
            async (row, token) => await ExecuteRoundAsync(row.TripId, row.Round, token));
        return due.Count;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Matching work dispatcher disabled by configuration.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Durable rows stay discoverable; never crash the host and
                // never spin: the pass cadence is the retry cadence.
                _logger.LogError(ex, "Matching work dispatcher pass failed; durable work will be retried.");
            }

            try
            {
                await Task.Delay(_options.PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// One leased round execution. Returns false when another instance owns
    /// the round (the lease holder will drive it) or when the round was not
    /// executable at this instant.
    /// </summary>
    public async Task<bool> ExecuteRoundAsync(
        Guid tripId, int round, CancellationToken ct = default)
    {
        await using var lease = await MatchingRoundLease.TryAcquireAsync(
            _connectionString, tripId, round, ct);
        if (lease is null)
        {
            return false;
        }

        var intake = await _attempts.LoadRoundAsync(tripId, round, ct);
        var hasParent = System.Diagnostics.ActivityContext.TryParse(intake?.IntakeTraceParent, null, out var parent);
        using var activity = hasParent
            ? MatchingTelemetry.Activities.StartActivity("matching.work.resume", System.Diagnostics.ActivityKind.Internal, parent)
            : MatchingTelemetry.Activities.StartActivity("matching.work.resume", System.Diagnostics.ActivityKind.Internal);

        string? error = null;
        try
        {
            error = await DriveRoundAsync(tripId, round, ct);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown/cancellation is not a business outcome. The durable
            // write-ahead row (if any) is recovered by the next process.
            throw;
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name;
            _logger.LogError(
                ex, "Matching round {TripId}/{Round} execution failed; durable work remains.",
                tripId, round);
            return false;
        }
        finally
        {
            try
            {
                await _attempts.SetWorkCheckAsync(
                    tripId, round, _clock.GetUtcNow(), error, CancellationToken.None);
            }
            catch (Exception ex)
            {
                // Losing this timestamp only makes the round due sooner; the
                // durable work itself is unaffected.
                _logger.LogWarning(
                    ex, "Could not record work check for {TripId}/{Round}.", tripId, round);
            }
        }
    }

    private async Task<string?> DriveRoundAsync(
        Guid tripId, int round, CancellationToken ct)
    {
        var stages = _policy.EffectiveStages();
        var finalStage = stages[^1].Name;
        var maxSteps = stages.Count + 2;
        for (var step = 0; step < maxSteps; step++)
        {
            ct.ThrowIfCancellationRequested();

            var row = await _attempts.LoadRoundAsync(tripId, round, ct);
            if (row is null
                || !row.AutoExecute
                || !string.Equals(row.Status, MatchingRoundStatuses.Processing, StringComparison.Ordinal))
            {
                return null;
            }

            if (row.CancelRequested)
            {
                return await HandleCancellationAsync(tripId, round, ct);
            }

            var attempts = await _attempts.LoadAttemptsAsync(tripId, round, ct);
            var reserved = attempts.FirstOrDefault(a =>
                string.Equals(a.State, MatchingAttemptStates.Reserved, StringComparison.Ordinal));
            if (reserved is not null)
            {
                await _attempts.TryMarkRoundReservedAsync(
                    tripId, round, reserved.DriverId, reserved.ReservationId, ct);
                return null;
            }

            var unresolved = attempts
                .Where(a => a.State is MatchingAttemptStates.InFlight or MatchingAttemptStates.Unknown)
                .OrderBy(a => a.Ordinal)
                .ToList();
            if (unresolved.Count > 0)
            {
                var pending = false;
                foreach (var attempt in unresolved)
                {
                    var reconciled = await ReconcileAsync(tripId, round, attempt.Ordinal, ct);
                    await _attempts.SetAttemptLastCheckedAsync(
                        tripId, round, attempt.Ordinal, _clock.GetUtcNow(), ct);
                    if (reconciled.Outcome is AttemptOutcome.NeedsReview or AttemptOutcome.Unknown)
                    {
                        pending = true;
                    }
                }

                if (pending)
                {
                    return "RECONCILE_PENDING";
                }

                continue;
            }

            // L7: a configuration shrink can leave a persisted current_stage
            // that the running policy no longer defines. Preserve the durable
            // round and its ambiguity (no terminal, no attempt mutation, no
            // invented stage) and surface the mismatch through the worker's
            // durable error code; the pass retries only if configuration is
            // restored.
            var stage = stages.FirstOrDefault(s =>
                string.Equals(s.Name, row.CurrentStage, StringComparison.Ordinal));
            if (stage is null)
            {
                return "STAGE_CONFIGURATION_MISMATCH";
            }
            var ready = attempts
                .Where(a => string.Equals(a.Stage, stage.Name, StringComparison.Ordinal)
                    && string.Equals(a.State, MatchingAttemptStates.Ready, StringComparison.Ordinal))
                .OrderBy(a => a.Ordinal)
                .Select(a => new AttemptTarget(a.Ordinal, a.DriverId))
                .ToList();

            if (ready.Count > 0)
            {
                var result = await ExecuteTargetsAsync(
                    tripId, round, ready, row.InputMessageId,
                    finalStage, ct);
                switch (result.Outcome)
                {
                    case RoundOutcome.Reserved:
                    case RoundOutcome.NoDriver:
                        return null;
                    case RoundOutcome.BlockedOnUnknown:
                    case RoundOutcome.NeedsReview:
                        return result.Code ?? result.Outcome.ToString();
                    case RoundOutcome.Refused:
                        // Terminal/cancel/round-gone: re-read on the next pass.
                        return null;
                    case RoundOutcome.Exhausted:
                        break; // Not final (terminalizer refused): advance below.
                }
            }
            else
            {
                var stageWork = await _stages.EnsureCurrentStageAsync(tripId, round, _policy, ct);
                switch (stageWork.Outcome)
                {
                    case StageWorkOutcome.RoundGone:
                    case StageWorkOutcome.NotWritable:
                        return stageWork.Reason;
                    case StageWorkOutcome.DependencyUnavailable:
                        return stageWork.Reason ?? "DEPENDENCY_UNAVAILABLE";
                    case StageWorkOutcome.Ready:
                        if (stageWork.Candidates.Count > 0)
                        {
                            var refreshed = await _attempts.LoadAttemptsAsync(tripId, round, ct);
                            if (refreshed.Any(a =>
                                string.Equals(a.Stage, stage.Name, StringComparison.Ordinal)
                                && string.Equals(a.State, MatchingAttemptStates.Ready, StringComparison.Ordinal)))
                            {
                                continue;
                            }
                        }

                        break;
                }
            }

            // Current stage exhaustion: all its attempts conclusive, no
            // success, no unresolved effect, and every frozen entry covered.
            row = await _attempts.LoadRoundAsync(tripId, round, ct) ?? row;
            var current = await _attempts.LoadAttemptsAsync(tripId, round, ct);
            var stageAttempts = current
                .Where(a => string.Equals(a.Stage, stage.Name, StringComparison.Ordinal))
                .ToList();
            if (stageAttempts.Any(a => a.State is MatchingAttemptStates.InFlight
                    or MatchingAttemptStates.Unknown or MatchingAttemptStates.Reserved))
            {
                return "STAGE_UNRESOLVED";
            }

            if (stageAttempts.Any(a =>
                !string.Equals(a.State, MatchingAttemptStates.Rejected, StringComparison.Ordinal)))
            {
                return "STAGE_INCOMPLETE";
            }

            var frozenCandidates = await _work.LoadStageCandidatesAsync(tripId, round, stage.Name, ct);
            var evaluated = string.Equals(row.StageEvaluated, stage.Name, StringComparison.Ordinal)
                || frozenCandidates.Count > 0;
            if (!evaluated)
            {
                return "STAGE_NOT_EVALUATED";
            }

            var rejected = stageAttempts
                .Where(a => string.Equals(a.State, MatchingAttemptStates.Rejected, StringComparison.Ordinal))
                .Select(a => (a.Ordinal, a.DriverId))
                .ToHashSet();
            if (frozenCandidates.Any(c => !rejected.Contains((c.Ordinal, c.DriverId))))
            {
                return "STAGE_INCOMPLETE";
            }

            var index = stages.ToList().FindIndex(s =>
                string.Equals(s.Name, stage.Name, StringComparison.Ordinal));
            if (index < 0)
            {
                throw new InvalidOperationException($"Unknown matching stage '{stage.Name}'.");
            }

            if (index < stages.Count - 1)
            {
                var advanced = await _work.TryAdvanceStageAsync(
                    tripId, round, stage.Name, stages[index + 1].Name, ct);
                if (advanced == StageAdvanceOutcome.Advanced)
                {
                    continue;
                }

                return advanced.ToString();
            }

            var terminal = await _terminalizer.TryTerminalizeNoDriverAsync(
                tripId, round, finalStage, ct);
            return terminal.Outcome == MatchingTerminalOutcome.NoDriver
                ? null
                : terminal.Reason ?? terminal.Outcome.ToString();
        }

        return "WORK_STEP_LIMIT";
    }

    private async Task<string?> HandleCancellationAsync(
        Guid tripId, int round, CancellationToken ct)
    {
        var attempts = await _attempts.LoadAttemptsAsync(tripId, round, ct);
        foreach (var attempt in attempts
                     .Where(a => a.State is MatchingAttemptStates.InFlight or MatchingAttemptStates.Unknown)
                     .OrderBy(a => a.Ordinal))
        {
            var reconciled = await ReconcileObservedAsync(tripId, round, attempt.Ordinal, ct);
            await _attempts.SetAttemptLastCheckedAsync(
                tripId, round, attempt.Ordinal, _clock.GetUtcNow(), ct);
            if (reconciled.Outcome is AttemptOutcome.NeedsReview or AttemptOutcome.Unknown)
            {
                return "CANCEL_RECONCILE_PENDING";
            }
        }

        var fresh = await _attempts.LoadAttemptsAsync(tripId, round, ct);
        var reserved = fresh.FirstOrDefault(a =>
            string.Equals(a.State, MatchingAttemptStates.Reserved, StringComparison.Ordinal));
        if (reserved is not null)
        {
            // An authoritative reservation outlived cancellation. The Trip
            // owns exact cleanup; Matching only reports truth.
            await _attempts.TryMarkRoundReservedAsync(
                tripId, round, reserved.DriverId, reserved.ReservationId, ct);
            return null;
        }

        if (fresh.Any(a => a.State is MatchingAttemptStates.InFlight or MatchingAttemptStates.Unknown))
        {
            return "CANCEL_RECONCILE_PENDING";
        }

        await _attempts.TryMarkRoundCancelledAsync(tripId, round, ct);
        return null;
    }

    private async Task<AttemptResult> ReconcileAsync(
        Guid tripId, int round, int ordinal, CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var orchestrator = scope.ServiceProvider.GetRequiredService<MatchingReserveOrchestrator>();
        return await orchestrator.ReconcileAsync(tripId, round, ordinal, ct);
    }

    private async Task<AttemptResult> ReconcileObservedAsync(
        Guid tripId, int round, int ordinal, CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var orchestrator = scope.ServiceProvider.GetRequiredService<MatchingReserveOrchestrator>();
        return await orchestrator.ReconcileObservedAsync(tripId, round, ordinal, ct);
    }

    private async Task<RoundReserveResult> ExecuteTargetsAsync(
        Guid tripId,
        int round,
        IReadOnlyList<AttemptTarget> targets,
        string inputMessageId,
        string? terminalStage,
        CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var orchestrator = scope.ServiceProvider.GetRequiredService<MatchingReserveOrchestrator>();
        return await orchestrator.ExecuteRoundTargetsAsync(
            tripId, round, targets, inputMessageId, terminalStage, ct);
    }
}
