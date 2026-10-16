// B-W07-05/06/08/10: serial Matching reserve orchestration
// (Doc 02 §2.3 serial algorithm + §12.5 reserve sequence, DSI-015/017/018/022/031).
//
// One round, one candidate at a time, always decided on durable state:
//   persist IN_FLIGHT (write-ahead identity, Doc 02 §12.4) → read Driver
//   version → persist max-dispatched fence BEFORE remote call → reserve with
//   same R (retry same R on STALE_VERSION, bounded) → Reserved (round follows)
//   / Rejected (next driver) / Unknown (STOP, no advance) → reconcile same R
//   with version fencing (bounded; exhaustion is a visible terminal, never
//   silent) → advance only on conclusive outcomes.
//
// Fencing rule (DSI-031, Doc 01 §19 clarified): current AVAILABLE alone never
// proves an earlier R cannot commit later (pre-lock and pre-commit races:
// query sees last-committed AVAILABLE while R holds the row lock uncommitted
// or has not yet acquired it, then R commits). AVAILABLE is conclusive only
// when the Driver version Vx exceeds every dispatched fence (Vx > max):
// versions only increase, the row lock serializes writers, so any future R
// read sees >= Vx > its fence and fails StaleVersion. Otherwise stay UNKNOWN.
// ReservedByOther/Offline/OnTrip/NotFound are conclusive without version:
// any future R read sees incompatible state or missing row (no DELETE in
// production; lock exclusion proves R is not holding) and cannot commit.
// Legacy rows without fence provenance (NULL) stay UNKNOWN on AVAILABLE.
//
// What this never does: generic identity-changing retry (same R always,
// Doc 02 §19.3), candidate advance on UNKNOWN (R-11), NO_DRIVER terminal
// marking (needs W9 Outbox atomicity, Doc 02 §12.8), broker calls (W8).
// Cancellation by the caller propagates; only transport uncertainty becomes
// UNKNOWN. Programming/contract defects stay loud.
using RideHailing.MatchingService.Infrastructure.DriverClient;
using RideHailing.MatchingService.Infrastructure.Persistence;

namespace RideHailing.MatchingService.Application.Reserve;

public enum AttemptOutcome
{
    Reserved,
    Rejected,
    Unknown,
    NeedsReview,
    Blocked,
}

public sealed record AttemptResult(
    AttemptOutcome Outcome,
    Guid ReservationId,
    string? Code);

public enum RoundOutcome
{
    Reserved,
    Exhausted,
    BlockedOnUnknown,
    NeedsReview,
    Refused,
}

public sealed record RoundReserveResult(
    RoundOutcome Outcome,
    Guid? DriverId,
    Guid? ReservationId,
    string? Code);

public sealed class MatchingReserveOrchestrator
{
    private readonly MatchingAttemptRepository _attempts;
    private readonly IDriverClient _driver;
    private readonly int _maxReserveAttempts;
    private readonly int _maxReconcileAttempts;

    public MatchingReserveOrchestrator(
        MatchingAttemptRepository attempts,
        IDriverClient driver,
        int maxReserveAttempts = 3,
        int maxReconcileAttempts = 3)
    {
        _attempts = attempts;
        _driver = driver;
        _maxReserveAttempts = maxReserveAttempts;
        _maxReconcileAttempts = maxReconcileAttempts;
    }

    /// <summary>
    /// Serial round executor (B-W07-10 guard lives here): at most one
    /// in-flight/unknown attempt per round, conclusive outcomes only advance.
    /// </summary>
    public async Task<RoundReserveResult> ExecuteRoundAsync(
        Guid tripId,
        int round,
        IReadOnlyList<Guid> candidates,
        Guid inputMessageId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (tripId == Guid.Empty)
        {
            throw new ArgumentException("tripId must not be empty.", nameof(tripId));
        }

        if (round < 1)
        {
            throw new ArgumentException("round must be >= 1.", nameof(round));
        }

        var roundRow = await _attempts.EnsureRoundAsync(tripId, round, inputMessageId, ct);
        if (roundRow.Status == MatchingRoundStatuses.Reserved)
        {
            return new RoundReserveResult(
                RoundOutcome.Reserved, roundRow.SelectedDriverId, roundRow.SelectedReservationId, null);
        }

        if (roundRow.Status != MatchingRoundStatuses.Processing)
        {
            return new RoundReserveResult(RoundOutcome.Refused, null, null, roundRow.Status);
        }

        for (var ordinal = 0; ordinal < candidates.Count; ordinal++)
        {
            var driverId = candidates[ordinal];
            if (await _attempts.HasOtherUnresolvedAsync(tripId, round, ordinal, driverId, ct))
            {
                return new RoundReserveResult(RoundOutcome.BlockedOnUnknown, null, null, null);
            }

            var attempt = await ExecuteAttemptAsync(tripId, round, ordinal, driverId, ct);
            switch (attempt.Outcome)
            {
                case AttemptOutcome.Reserved:
                    // Report the DURABLE row's identity, not the caller's
                    // list: under divergent candidate lists the winner may be
                    // a different driver than the one requested at this index.
                    // Converge the round mark too: a crash between the
                    // attempt commit and the round mark must heal on replay
                    // (idempotent consequence of durable attempt truth).
                    var durable = await _attempts.LoadAttemptAsync(tripId, round, ordinal, ct);
                    if (durable is not null && durable.State == MatchingAttemptStates.Reserved)
                    {
                        await _attempts.TryMarkRoundReservedAsync(
                            tripId, round, durable.DriverId, durable.ReservationId, ct);
                    }

                    return new RoundReserveResult(
                        RoundOutcome.Reserved,
                        durable?.DriverId ?? driverId,
                        attempt.ReservationId,
                        null);
                case AttemptOutcome.Rejected:
                    continue;
                case AttemptOutcome.Unknown:
                    return new RoundReserveResult(RoundOutcome.BlockedOnUnknown, null, null, attempt.Code);
                case AttemptOutcome.NeedsReview:
                    return new RoundReserveResult(RoundOutcome.NeedsReview, null, null, attempt.Code);
                case AttemptOutcome.Blocked:
                    return new RoundReserveResult(RoundOutcome.BlockedOnUnknown, null, null, attempt.Code);
                default:
                    throw new ArgumentOutOfRangeException(nameof(attempt));
            }
        }

        return new RoundReserveResult(RoundOutcome.Exhausted, null, null, null);
    }

    /// <summary>
    /// Single-attempt lifecycle: ensure write-ahead row (converging with
    /// duplicate workers on the PK), then drive the remote call. Terminal
    /// rows replay their outcome without new effects.
    /// </summary>
    public async Task<AttemptResult> ExecuteAttemptAsync(
        Guid tripId,
        int round,
        int ordinal,
        Guid driverId,
        CancellationToken ct = default)
    {
        // Public entry: the round row must exist first (FK). Fresh message
        // ids converge on PK conflict, so duplicate workers share the round.
        await _attempts.EnsureRoundAsync(tripId, round, Guid.NewGuid(), ct);
        if (await _attempts.HasOtherUnresolvedAsync(tripId, round, ordinal, driverId, ct))
        {
            return new AttemptResult(AttemptOutcome.Blocked, Guid.Empty, "UNRESOLVED_ATTEMPT");
        }

        await _attempts.EnsureInflightAttemptAsync(
            tripId, round, ordinal, driverId, Guid.NewGuid(), ct);
        var row = (await _attempts.LoadAttemptAsync(tripId, round, ordinal, ct))!;

        return row.State switch
        {
            MatchingAttemptStates.Reserved => await RepairRoundAndReportAsync(tripId, round, row, ct),
            MatchingAttemptStates.Rejected => new AttemptResult(
                AttemptOutcome.Rejected, row.ReservationId, row.LastErrorCode),
            _ => await CallReserveAsync(tripId, round, ordinal, row, ct),
        };
    }

    /// <summary>
    /// F2 convergence: a durably RESERVED attempt implies a RESERVED round.
    /// The conditional mark is idempotent (PROCESSING-only) and converges
    /// duplicate workers; it never invents a winner (row is the authority).
    /// </summary>
    private async Task<AttemptResult> RepairRoundAndReportAsync(
        Guid tripId,
        int round,
        MatchingAttemptRow row,
        CancellationToken ct)
    {
        await _attempts.TryMarkRoundReservedAsync(tripId, round, row.DriverId, row.ReservationId, ct);
        return new AttemptResult(AttemptOutcome.Reserved, row.ReservationId, null);
    }

    /// <summary>
    /// Reconciles an UNKNOWN (or never-called IN_FLIGHT/READY) attempt with
    /// the same reservation identity (B-W07-08, DSI-018/DSL-001). Conclusive
    /// truth resolves durably; transport uncertainty stays UNKNOWN; bounded
    /// exhaustion is a visible terminal, never silent drift.
    /// </summary>
    public async Task<AttemptResult> ReconcileAsync(
        Guid tripId,
        int round,
        int ordinal,
        CancellationToken ct = default)
    {
        var row = await _attempts.LoadAttemptAsync(tripId, round, ordinal, ct);
        if (row is null)
        {
            throw new ArgumentException("No such attempt.", nameof(ordinal));
        }

        if (row.State == MatchingAttemptStates.Reserved)
        {
            await _attempts.TryMarkRoundReservedAsync(tripId, round, row.DriverId, row.ReservationId, ct);
            return new AttemptResult(AttemptOutcome.Reserved, row.ReservationId, null);
        }

        if (row.State == MatchingAttemptStates.Rejected)
        {
            return new AttemptResult(AttemptOutcome.Rejected, row.ReservationId, row.LastErrorCode);
        }

        if (row.State is MatchingAttemptStates.Ready or MatchingAttemptStates.InFlight)
        {
            // Possibly dispatched but crashed before recording the outcome
            // (IN_FLIGHT is write-ahead identity, not proof of never-called).
            // Resume with the SAME reservation identity; same-R replay on the
            // Driver is idempotent, never a new logical attempt (DSI-018).
            return await CallReserveAsync(tripId, round, ordinal, row, ct);
        }

        // UNKNOWN: poll authoritative truth with the same R.
        for (var i = 0; i < _maxReconcileAttempts; i++)
        {
            ReservationReconcileResult truth;
            try
            {
                truth = await _driver.GetReservationAsync(row.DriverId, row.ReservationId, ct);
            }
            catch (Exception ex) when (IsTransport(ex, ct))
            {
                continue;
            }

            switch (truth.Outcome)
            {
                case ReservationReconcileOutcome.OwnedByThisReservation:
                    var concluded = await TryConcludeAsync(
                        tripId, round, ordinal,
                        [MatchingAttemptStates.Unknown], MatchingAttemptStates.Reserved, null, ct);
                    if (concluded is null)
                    {
                        // Our transition won: this observation concluded the
                        // attempt, so converge the round. A lost race returns
                        // the durable outcome instead and must NOT mark (the
                        // remap path marks only for durably RESERVED rows).
                        await _attempts.TryMarkRoundReservedAsync(
                            tripId, round, row.DriverId, row.ReservationId, ct);
                    }

                    return concluded
                        ?? new AttemptResult(AttemptOutcome.Reserved, row.ReservationId, null);
                case ReservationReconcileOutcome.NotCommittedOrAvailable:
                    // Fail-closed fencing: current AVAILABLE alone never proves
                    // an earlier R cannot commit later (pre-lock: R has not yet
                    // acquired the row lock; pre-commit: R holds the lock
                    // uncommitted while the query sees last-committed AVAILABLE).
                    // Conclusive only when Vx exceeds every dispatched fence:
                    // versions only increase and the row lock serializes writers,
                    // so any future R read sees >= Vx > its fence -> StaleVersion.
                    // Otherwise poll again; exhaustion becomes visible review.
                    // Legacy rows without fence provenance (NULL) stay UNKNOWN.
                    if (row.MaxDispatchedVersion.HasValue
                        && truth.DriverVersion.HasValue
                        && truth.DriverVersion.Value > row.MaxDispatchedVersion.Value)
                    {
                        var fencedCode = ToOutcomeCode(truth.Outcome);
                        var fenced = await TryConcludeAsync(
                            tripId, round, ordinal,
                            [MatchingAttemptStates.Unknown], MatchingAttemptStates.Rejected,
                            fencedCode, ct);
                        return fenced
                            ?? new AttemptResult(AttemptOutcome.Rejected, row.ReservationId, fencedCode);
                    }

                    continue;
                case ReservationReconcileOutcome.ReservedByOther:
                case ReservationReconcileOutcome.Offline:
                case ReservationReconcileOutcome.OnTrip:
                case ReservationReconcileOutcome.NotFound:
                    var code = ToOutcomeCode(truth.Outcome);
                    var rejected = await TryConcludeAsync(
                        tripId, round, ordinal,
                        [MatchingAttemptStates.Unknown], MatchingAttemptStates.Rejected,
                        code, ct);
                    return rejected
                        ?? new AttemptResult(AttemptOutcome.Rejected, row.ReservationId, code);
                default:
                    throw new ArgumentOutOfRangeException(nameof(truth));
            }
        }

        // Bounded exhaustion: the row stays UNKNOWN (durable truth), but the
        // review signal is persisted so it survives restart (DSL-001
        // visibility). A durable REVIEW terminal state itself belongs to W9.
        await _attempts.SetErrorCodeAsync(tripId, round, ordinal, "RECONCILE_EXHAUSTED", ct);
        return new AttemptResult(AttemptOutcome.NeedsReview, row.ReservationId, "RECONCILE_EXHAUSTED");
    }

    /// <summary>
    /// Canonical wire vocabulary for reconcile outcomes (Doc 02 §12.6),
    /// persisted as last_error_code and returned to callers.
    /// </summary>
    public static string ToOutcomeCode(ReservationReconcileOutcome truth) => truth switch
    {
        ReservationReconcileOutcome.OwnedByThisReservation => "OWNED_BY_THIS_RESERVATION",
        ReservationReconcileOutcome.NotCommittedOrAvailable => "NOT_COMMITTED_OR_AVAILABLE",
        ReservationReconcileOutcome.ReservedByOther => "RESERVED_BY_OTHER",
        ReservationReconcileOutcome.Offline => "OFFLINE",
        ReservationReconcileOutcome.OnTrip => "ON_TRIP",
        ReservationReconcileOutcome.NotFound => "NOT_FOUND",
        _ => throw new ArgumentOutOfRangeException(nameof(truth)),
    };

    private async Task<AttemptResult> CallReserveAsync(
        Guid tripId,
        int round,
        int ordinal,
        MatchingAttemptRow row,
        CancellationToken ct)
    {
        // F1 gate: once this logical attempt is durably UNKNOWN, a later bare
        // 404 (proxy/wrong-route/deleted-row) must NOT become conclusive
        // proof of no-effect. Only a fresh attempt (never ambiguous) may
        // treat absence as conclusive. Derived from durable row state, never
        // process memory, so restart preserves it.
        var alreadyAmbiguous = row.State == MatchingAttemptStates.Unknown;
        for (var i = 0; i < _maxReserveAttempts; i++)
        {
            DriverVersionRead? observed;
            try
            {
                observed = await _driver.TryGetDriverVersionAsync(row.DriverId, ct);
            }
            catch (Exception ex) when (IsTransport(ex, ct))
            {
                // Pre-effect failure: nothing could have been sent yet for
                // this call, but the attempt is unresolved — stay IN_FLIGHT
                // (the guard treats it like UNKNOWN) and stop.
                return new AttemptResult(AttemptOutcome.Unknown, row.ReservationId, "VERSION_READ_FAILED");
            }

            if (observed is null)
            {
                if (alreadyAmbiguous)
                {
                    return new AttemptResult(AttemptOutcome.Unknown, row.ReservationId, "PRIOR_AMBIGUITY");
                }

                // Conclusive: an unknown driver cannot hold an effect
                // (W6 reserve returns NotFound without writing).
                var missing = await TryConcludeAsync(
                    tripId, round, ordinal,
                    [MatchingAttemptStates.InFlight, MatchingAttemptStates.Unknown, MatchingAttemptStates.Ready],
                    MatchingAttemptStates.Rejected, "DRIVER_NOT_FOUND", ct);
                return missing
                    ?? new AttemptResult(AttemptOutcome.Rejected, row.ReservationId, "DRIVER_NOT_FOUND");
            }

            DriverReserveResult call;
            try
            {
                // Write-ahead fence: persist the version we are about to use
                // BEFORE the remote call can be sent. If this write fails, do
                // NOT dispatch (an undocumented outstanding fence would break
                // later AVAILABLE fencing). Crash between fence write and
                // dispatch only over-approximates max (conservative UNKNOWN).
                try
                {
                    await _attempts.RecordDispatchAsync(tripId, round, ordinal, observed.Version, ct);
                }
                catch (Exception fenceEx) when (!ct.IsCancellationRequested || fenceEx is not OperationCanceledException)
                {
                    return new AttemptResult(AttemptOutcome.Unknown, row.ReservationId, "DISPATCH_FENCE_FAILED");
                }

                call = await _driver.ReserveAsync(
                    new DriverReserveRequest(row.DriverId, tripId, row.ReservationId, observed.Version), ct);
            }
            catch (Exception ex) when (IsTransport(ex, ct))
            {
                // Possibly sent: the effect is genuinely ambiguous.
                var ambiguous = await TryConcludeAsync(
                    tripId, round, ordinal,
                    [MatchingAttemptStates.InFlight, MatchingAttemptStates.Ready],
                    MatchingAttemptStates.Unknown, "RESERVE_TRANSPORT", ct);
                return ambiguous
                    ?? new AttemptResult(AttemptOutcome.Unknown, row.ReservationId, "RESERVE_TRANSPORT");
            }

            if (call.Kind == DriverReserveOutcomeKind.Reserved)
            {
                var won = await TryConcludeAsync(
                    tripId, round, ordinal,
                    [MatchingAttemptStates.InFlight, MatchingAttemptStates.Unknown, MatchingAttemptStates.Ready],
                    MatchingAttemptStates.Reserved, null, ct);
                if (won is null)
                {
                    // Our transition won: converge the round. A lost race
                    // returns the durable outcome instead and must NOT mark
                    // (the remap path marks only for durably RESERVED rows).
                    await _attempts.TryMarkRoundReservedAsync(tripId, round, row.DriverId, row.ReservationId, ct);
                }

                return won
                    ?? new AttemptResult(AttemptOutcome.Reserved, row.ReservationId, null);
            }

            if (!string.Equals(call.Code, "STALE_VERSION", StringComparison.Ordinal))
            {
                if (alreadyAmbiguous
                    && string.Equals(call.Code, "DRIVER_NOT_FOUND", StringComparison.Ordinal))
                {
                    // Same rule as the version-read path: after ambiguity, a
                    // bare absence is not proof of no-effect (proxy/wrong
                    // route/deleted row are indistinguishable on the wire).
                    return new AttemptResult(AttemptOutcome.Unknown, row.ReservationId, "PRIOR_AMBIGUITY");
                }

                var lost = await TryConcludeAsync(
                    tripId, round, ordinal,
                    [MatchingAttemptStates.InFlight, MatchingAttemptStates.Unknown, MatchingAttemptStates.Ready],
                    MatchingAttemptStates.Rejected, call.Code ?? "DRIVER_NOT_AVAILABLE", ct);
                return lost
                    ?? new AttemptResult(
                        AttemptOutcome.Rejected, row.ReservationId, call.Code ?? "DRIVER_NOT_AVAILABLE");
            }

            // STALE_VERSION with budget left: same logical attempt, fresh
            // fence value (DSI-018: retry reuses identity, never mints).
            if (i + 1 < _maxReserveAttempts)
            {
                continue;
            }

            // Contention exhaustion (repeated STALE_VERSION): our R provably
            // never committed (every fence rejected it), but no conclusive
            // outcome either — stay UNKNOWN, never advance (fail safe).
            var exhausted = await TryConcludeAsync(
                tripId, round, ordinal,
                [MatchingAttemptStates.InFlight, MatchingAttemptStates.Ready],
                MatchingAttemptStates.Unknown, "RESERVE_CONTENTION", ct);
            return exhausted
                ?? new AttemptResult(AttemptOutcome.Unknown, row.ReservationId, "RESERVE_CONTENTION");
        }

        // Unreachable: the loop above always returns (reserved / rejected /
        // contention-unknown). Kept as a fail-safe, never silent.
        throw new InvalidOperationException("Reserve attempt loop fell through without a terminal outcome.");
    }

    /// <summary>
    /// Guarded conclusion with duplicate-worker convergence: transitions the
    /// row when it is still in an expected state; when another worker already
    /// moved it, reloads and maps the DURABLE row instead of reporting our
    /// possibly-stale observation (a stale Rejected must never advance past
    /// a durably RESERVED round).
    /// Returns null when our transition won (caller reports its own outcome).
    /// </summary>
    private async Task<AttemptResult?> TryConcludeAsync(
        Guid tripId,
        int round,
        int ordinal,
        string[] from,
        string to,
        string? error,
        CancellationToken ct)
    {
        if (await _attempts.TryTransitionAsync(tripId, round, ordinal, from, to, error, ct))
        {
            return null;
        }

        var fresh = await _attempts.LoadAttemptAsync(tripId, round, ordinal, ct);
        if (fresh is not null && fresh.State == MatchingAttemptStates.Reserved)
        {
            await _attempts.TryMarkRoundReservedAsync(
                tripId, round, fresh.DriverId, fresh.ReservationId, ct);
            return new AttemptResult(AttemptOutcome.Reserved, fresh.ReservationId, null);
        }

        if (fresh is not null && fresh.State == MatchingAttemptStates.Rejected)
        {
            return new AttemptResult(AttemptOutcome.Rejected, fresh.ReservationId, fresh.LastErrorCode);
        }

        return new AttemptResult(
            AttemptOutcome.Unknown,
            fresh?.ReservationId ?? Guid.Empty,
            fresh is null ? "ATTEMPT_VANISHED" : "UNRESOLVED");
    }

    private static bool IsTransport(Exception ex, CancellationToken ct)
    {
        if (ex is OperationCanceledException && ct.IsCancellationRequested)
        {
            return false;
        }

        return ex is HttpRequestException
            or TaskCanceledException
            or TimeoutException
            or System.Net.Sockets.SocketException
            or IOException;
    }
}
