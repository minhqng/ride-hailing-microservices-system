// Authoritative test-scope invariant auditor.
//
// This is verification tooling, not business code. It reads ONLY through
// read-only PostgreSQL transactions; it never mutates a service database.
// A PASS requires the authority that owns the invariant to be inspected.
// A missing authority is NOT RUN (owner dependency) or BLOCKED (unreachable),
// never a silent PASS; LOCAL_PROXY_PASS is reserved for supporting evidence
// that cannot prove the distributed invariant by itself.
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Npgsql;

namespace RideHailing.InvariantAudit.Tests;

public sealed record AuditPolicy
{
    public TimeSpan StaleUnresolvedAge { get; init; } = TimeSpan.FromMinutes(15);
    public TimeSpan PendingOutboxAge { get; init; } = TimeSpan.FromMinutes(15);
    public TimeSpan PendingCleanupAge { get; init; } = TimeSpan.FromMinutes(15);
}

public sealed class InvariantAuditor(
    string driverConnectionString,
    string? matchingConnectionString,
    string? tripConnectionString,
    AuditPolicy? policy = null,
    TimeProvider? clock = null)
{
    private static readonly string[] DriverUnavailableStates = ["AVAILABLE", "OFFLINE"];
    private static readonly string[] ActivePreStartStates = ["DRIVER_RESERVED", "DRIVER_ACCEPTED", "READY_TO_START"];
    private static readonly string[] TerminalTripStates = ["COMPLETED", "CANCELLED", "MATCH_FAILED"];
    private static readonly string[] UnresolvedAttemptStates = ["READY", "IN_FLIGHT", "UNKNOWN"];

    // Business quiescence: post-scenario observations require every Driver/Matching
    // release obligation to be complete. Mid-flight observation is the only
    // mode in which a fresh recognized cleanup is not counted as a leak.
    private static bool IsBusinessQuiesced(string observationMode) =>
        observationMode.StartsWith("POST_SCENARIO", StringComparison.OrdinalIgnoreCase)
        && !observationMode.Contains("MID_FLIGHT", StringComparison.OrdinalIgnoreCase);

    // Relay quiescence is stricter and only claimed when the shared Outbox
    // relay actually ran for the scenario. The default audit harness does not
    // run the relay, so it uses *_NO_RELAY modes and applies the age policy.
    private static bool IsRelayQuiesced(string observationMode) =>
        observationMode.Contains("RELAY_QUIESCED", StringComparison.OrdinalIgnoreCase);

    private readonly AuditPolicy _policy = policy ?? new AuditPolicy();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public string DriverConnectionString { get; } = driverConnectionString;
    public string? MatchingConnectionString { get; } = matchingConnectionString;
    public string? TripConnectionString { get; } = tripConnectionString;

    public async Task<AuditRun> RunAsync(
        string scenarioId, string observationMode, CancellationToken cancellationToken = default)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var started = Stopwatch.GetTimestamp();
        var runId = $"AUD-{scenarioId}-{startedAt:yyyyMMddTHHmmssZ}-" +
                    Guid.NewGuid().ToString("N")[..6];

        var checks = new List<AuditCheckResult>
        {
            await ExecuteAsync(runId, scenarioId, observationMode, "AUD-01",
                "Exclusive Driver reservation: at most one current active reservation/trip per Driver",
                ["driver_db"],
                ct => RunAud01Async(ct)),
            await ExecuteAsync(runId, scenarioId, observationMode, "AUD-02",
                "Driver state coherence and lifecycle provenance",
                ["driver_db"],
                ct => RunAud02Async(ct)),
            await ExecuteAsync(runId, scenarioId, observationMode, "AUD-03",
                "One current Trip assignment",
                ["trip_db (Trip-owned)"],
                _ => Task.FromResult(new CheckOutcome(0, AuditStatus.NOT_RUN, [],
                    "Trip assignment authority is Trip-owned (TripInvariantAuditor, PR #43). " +
                    "This harness executes AUD-04 against real Trip rows but does not duplicate the Trip-only audit; " +
                    "the Trip-side evidence is bound to a different source SHA/scenario set, so it is not reused as a PASS here."))),
            await ExecuteAsync(runId, scenarioId, observationMode, "AUD-04",
                "Driver–Trip exact reservation identity agreement",
                ["driver_db", "trip_db"],
                ct => RunAud04Async(observationMode, ct)),
            await ExecuteAsync(runId, scenarioId, observationMode, "AUD-05",
                "Payment logical effect uniqueness",
                ["payment_db (C-owned)"],
                _ => Task.FromResult(new CheckOutcome(0, AuditStatus.NOT_RUN, [],
                    "Payment authority is C-owned; no Payment database is supplied to the B audit. " +
                    "Explicit C dependency: payment effect ledger inspection."))),
            await ExecuteAsync(runId, scenarioId, observationMode, "AUD-06",
                "Matching terminal uniqueness and durable result intent",
                ["matching_db", "driver_db"],
                ct => RunAud06Async(ct)),
            await ExecuteAsync(runId, scenarioId, observationMode, "AUD-07",
                "No leaked pre-start Driver reservation after quiescence",
                ["trip_db", "driver_db"],
                ct => RunAud07Async(observationMode, ct)),
            await ExecuteAsync(runId, scenarioId, observationMode, "AUD-08",
                "Outbox/attempt unresolved state and age",
                ["driver_db", "matching_db"],
                ct => RunAud08Async(observationMode, ct)),
        };

        return new AuditRun(
            RunId: runId,
            ScenarioId: scenarioId,
            ObservationMode: observationMode,
            SourceCommit: AuditEnvironment.SourceCommit,
            SourceTree: AuditEnvironment.SourceTree,
            StartedAt: startedAt,
            ElapsedTimeMs: Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            Checks: checks);
    }

    private async Task<AuditCheckResult> ExecuteAsync(
        string runId, string scenarioId, string observationMode,
        string checkId, string requirement, string[] authorities,
        Func<CancellationToken, Task<CheckOutcome>> body)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            var outcome = await body(CancellationToken.None);
            return new AuditCheckResult(
                runId, AuditEnvironment.SourceCommit, scenarioId, observationMode,
                checkId, requirement, authorities, outcome.Inspected,
                outcome.Status, outcome.Findings,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                outcome.Limitation);
        }
        catch (Exception ex)
        {
            return new AuditCheckResult(
                runId, AuditEnvironment.SourceCommit, scenarioId, observationMode,
                checkId, requirement, authorities, 0, AuditStatus.BLOCKED,
                [new AuditFinding("AUTHORITY_UNREACHABLE",
                    $"{checkId} could not inspect its authority: {ex.GetType().Name}: {ex.Message}")],
                Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                "Unreachable/invalid authority dataset; not a business verdict.");
        }
    }

    private sealed record CheckOutcome(
        long Inspected,
        AuditStatus Status,
        List<AuditFinding> Findings,
        string? Limitation = null);

    // ------------------------------------------- driver reservation coherence
    private async Task<CheckOutcome> RunAud01Async(CancellationToken ct)
    {
        await using var db = await ReadOnlySession.OpenAsync(DriverConnectionString, ct);
        var drivers = await db.QueryAsync("""
            SELECT driver_id, status, reserved_trip_id, reservation_id, active_trip_id, reserved_round
            FROM drivers ORDER BY driver_id;
            """, ReadDriver, ct);
        var indexes = await db.StringsAsync(
            "SELECT indexname FROM pg_indexes WHERE schemaname = 'public' AND tablename = 'drivers';", ct);
        var constraints = await db.StringsAsync("""
            SELECT conname FROM pg_constraint
            WHERE conrelid = 'drivers'::regclass AND contype = 'c';
            """, ct);

        var findings = new List<AuditFinding>();
        var active = drivers
            .Where(d => d.Status is "RESERVED" or "ON_TRIP")
            .ToArray();

        foreach (var driver in drivers)
        {
            if (driver.Status == "RESERVED"
                && (driver.ReservedTripId is null || driver.ReservationId is null))
            {
                findings.Add(new AuditFinding("PARTIAL_ACTIVE_RESERVATION",
                    "RESERVED Driver lacks trip/reservation identity.", driver.DriverId.ToString("D")));
            }

            if (driver.Status == "ON_TRIP" && driver.ActiveTripId is null)
            {
                findings.Add(new AuditFinding("PARTIAL_ACTIVE_TRIP",
                    "ON_TRIP Driver lacks activeTripId.", driver.DriverId.ToString("D")));
            }

            if (driver.ReservedTripId is not null && driver.ActiveTripId is not null)
            {
                findings.Add(new AuditFinding("TWO_CURRENT_ACTIVE_REFERENCES",
                    "Driver carries both a current reservation and an active trip.", driver.DriverId.ToString("D")));
            }

            if (driver.ReservationId is not null
                && driver.Status is not ("RESERVED" or "AVAILABLE" or "OFFLINE" or "ON_TRIP"))
            {
                findings.Add(new AuditFinding("UNKNOWN_STATUS",
                    $"Driver status '{driver.Status}' is outside the frozen FSM.", driver.DriverId.ToString("D")));
            }
        }

        foreach (var duplicate in drivers
                     .Where(d => d.ReservationId is not null)
                     .GroupBy(d => d.ReservationId!.Value)
                     .Where(g => g.Count() > 1))
        {
            findings.Add(new AuditFinding("DUPLICATE_RESERVATION_IDENTITY",
                "The same reservationId is current on more than one Driver.",
                $"{duplicate.Key:D} -> {string.Join(",", duplicate.Select(d => d.DriverId.ToString("D")))}"));
        }

        if (!indexes.Contains("ux_drivers_reservation_id", StringComparer.OrdinalIgnoreCase))
        {
            findings.Add(new AuditFinding("MISSING_UNIQUE_RESERVATION_INDEX",
                "ux_drivers_reservation_id is absent; cross-driver reservation uniqueness is not storage-enforced."));
        }

        if (!constraints.Contains("chk_drivers_coherence", StringComparer.OrdinalIgnoreCase))
        {
            findings.Add(new AuditFinding("MISSING_COHERENCE_CONSTRAINT",
                "chk_drivers_coherence is absent; state/reservation coherence is not storage-enforced."));
        }

        var status = drivers.Count == 0
            ? AuditStatus.NOT_RUN
            : findings.Count == 0 ? AuditStatus.PASS : AuditStatus.FAIL;
        return new CheckOutcome(
            drivers.Count + indexes.Count + constraints.Count,
            status,
            findings,
            drivers.Count == 0 ? "Empty Driver dataset; PASS would be vacuous." : null);
    }

    // -------------------------------------------- driver lifecycle coherence
    private async Task<CheckOutcome> RunAud02Async(CancellationToken ct)
    {
        await using var db = await ReadOnlySession.OpenAsync(DriverConnectionString, ct);
        var drivers = await db.QueryAsync("""
            SELECT driver_id, status, reserved_trip_id, reservation_id, active_trip_id, reserved_round
            FROM drivers ORDER BY driver_id;
            """, ReadDriver, ct);
        var constraints = await db.StringsAsync("""
            SELECT conname FROM pg_constraint
            WHERE conrelid = 'drivers'::regclass AND contype = 'c';
            """, ct);
        var lifecycleCount = await db.ScalarAsync(
            "SELECT count(*) FROM driver_lifecycle_effects;", ct);
        var onTripWithoutStart = await db.QueryAsync("""
            SELECT d.driver_id
            FROM drivers d
            LEFT JOIN driver_lifecycle_effects e
              ON e.driver_id = d.driver_id AND e.trip_id = d.active_trip_id AND e.phase = 'STARTED'
            WHERE d.status = 'ON_TRIP' AND e.driver_id IS NULL
            ORDER BY d.driver_id;
            """, r => r.GetGuid(0), ct);
        var completedIdentityReuse = await db.QueryAsync("""
            SELECT d.driver_id, e.trip_id
            FROM drivers d
            JOIN driver_lifecycle_effects e
              ON e.driver_id = d.driver_id AND e.phase = 'COMPLETED'
            WHERE d.status IN ('RESERVED', 'ON_TRIP')
              AND e.trip_id = COALESCE(d.active_trip_id, d.reserved_trip_id)
            ORDER BY d.driver_id;
            """, r => (Driver: r.GetGuid(0), Trip: r.GetGuid(1)), ct);

        var findings = new List<AuditFinding>();
        foreach (var driver in drivers)
        {
            if (!IsCoherent(driver))
            {
                findings.Add(new AuditFinding("INCOHERENT_DRIVER_STATE",
                    $"status={driver.Status}, reservedTrip={driver.ReservedTripId:D}, " +
                    $"reservation={driver.ReservationId:D}, activeTrip={driver.ActiveTripId:D}, " +
                    $"reservedRound={driver.ReservedRound?.ToString(CultureInfo.InvariantCulture) ?? "null"}.",
                    driver.DriverId.ToString("D")));
            }

            if (driver.Status == "RESERVED" && driver.ReservedRound is null)
            {
                findings.Add(new AuditFinding("LEGACY_NULL_ROUND",
                    "RESERVED row carries no matching-round provenance (allowed for legacy rows, " +
                    "but not acceptable for a modern reservation).", driver.DriverId.ToString("D")));
            }
        }

        foreach (var driverId in onTripWithoutStart)
        {
            findings.Add(new AuditFinding("ON_TRIP_WITHOUT_STARTED_PROVENANCE",
                "ON_TRIP Driver has no committed STARTED lifecycle effect for its active trip.",
                driverId.ToString("D")));
        }

        foreach (var (driverId, tripId) in completedIdentityReuse)
        {
            findings.Add(new AuditFinding("COMPLETED_IDENTITY_REUSED_AS_ACTIVE",
                "A completed lifecycle identity is again current on the Driver row.",
                $"{driverId:D}/{tripId:D}"));
        }

        foreach (var required in new[]
                 {
                     "chk_drivers_coherence", "chk_drivers_identity_nonempty",
                     "chk_drivers_reserve_identity_nonempty", "chk_drivers_reserved_round",
                 })
        {
            if (!constraints.Contains(required, StringComparer.OrdinalIgnoreCase))
            {
                findings.Add(new AuditFinding("MISSING_COHERENCE_CONSTRAINT",
                    $"Required storage constraint '{required}' is absent."));
            }
        }

        var violations = findings.Any(f => f.Code != "LEGACY_NULL_ROUND");
        var status = drivers.Count == 0
            ? AuditStatus.NOT_RUN
            : violations ? AuditStatus.FAIL : AuditStatus.PASS;
        return new CheckOutcome(
            drivers.Count + constraints.Count + lifecycleCount,
            status,
            findings,
            drivers.Count == 0
                ? "Empty Driver dataset; PASS would be vacuous."
                : "Historical lifecycle_effects rows are never treated as current reservations; only the drivers row is active authority.");
    }

    private static bool IsCoherent(DriverRow driver)
    {
        var noRefs = driver.ReservedTripId is null && driver.ReservationId is null
                     && driver.ActiveTripId is null && driver.ReservedRound is null;
        return driver.Status switch
        {
            "AVAILABLE" or "OFFLINE" => noRefs,
            "RESERVED" => driver.ReservedTripId is not null && driver.ReservationId is not null
                          && driver.ActiveTripId is null
                          && (driver.ReservedRound is null || driver.ReservedRound >= 1),
            "ON_TRIP" => driver.ActiveTripId is not null && driver.ReservedTripId is null
                         && driver.ReservationId is null && driver.ReservedRound is null,
            _ => false,
        };
    }

    // ------------------------------------------------ Driver/Trip agreement
    private async Task<CheckOutcome> RunAud04Async(string observationMode, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(TripConnectionString))
        {
            return new CheckOutcome(0, AuditStatus.NOT_RUN, [],
                "Trip database not supplied to this scenario; cross-service agreement cannot be evaluated.");
        }

        await using var driverDb = await ReadOnlySession.OpenAsync(DriverConnectionString, ct);
        await using var tripDb = await ReadOnlySession.OpenAsync(TripConnectionString, ct);
        var drivers = await driverDb.QueryAsync("""
            SELECT driver_id, status, reserved_trip_id, reservation_id, active_trip_id, reserved_round
            FROM drivers ORDER BY driver_id;
            """, ReadDriver, ct);
        var trips = await tripDb.QueryAsync("""
            SELECT trip_id, state, assigned_driver_id, reservation_id, matching_round, current_matching_round
            FROM trips ORDER BY trip_id;
            """, ReadTrip, ct);
        var cleanup = await tripDb.QueryAsync("""
            SELECT trip_id, kind, driver_id, reservation_id, matching_round, created_at, completed_at
            FROM trip_cleanup_intents ORDER BY trip_id, key;
            """, ReadCleanup, ct);
        var effects = await driverDb.QueryAsync("""
            SELECT driver_id, trip_id, reservation_id, phase
            FROM driver_lifecycle_effects ORDER BY driver_id;
            """, r => (DriverId: r.GetGuid(0), TripId: r.GetGuid(1),
                ReservationId: r.GetGuid(2), Phase: r.GetString(3)), ct);

        var findings = new List<AuditFinding>();
        var driversById = drivers.ToDictionary(d => d.DriverId);
        var tripsById = trips.ToDictionary(t => t.TripId);
        var openCleanup = cleanup.Where(c => c.CompletedAt is null).ToArray();
        var now = _clock.GetUtcNow();
        var quiesced = IsBusinessQuiesced(observationMode);

        foreach (var trip in trips.Where(t => t.AssignedDriverId is not null))
        {
            var driverId = trip.AssignedDriverId!.Value;
            var reservationId = trip.ReservationId!.Value;
            var round = trip.MatchingRound!.Value;
            driversById.TryGetValue(driverId, out var driver);

            if (ActivePreStartStates.Contains(trip.State))
            {
                if (driver is null)
                {
                    findings.Add(new AuditFinding("ASSIGNED_DRIVER_MISSING",
                        "Active assigned Trip references a Driver row that does not exist.", trip.TripId.ToString("D")));
                }
                else if (driver.Status != "RESERVED"
                         || driver.ReservedTripId != trip.TripId
                         || driver.ReservationId != reservationId
                         || driver.ReservedRound != round)
                {
                    findings.Add(new AuditFinding("DRIVER_TRIP_DISAGREEMENT",
                        $"Trip expects driver={driverId:D} trip={trip.TripId:D} reservation={reservationId:D} round={round}; " +
                        $"Driver row is status={driver.Status}, reservedTrip={driver.ReservedTripId:D}, " +
                        $"reservation={driver.ReservationId:D}, round={driver.ReservedRound?.ToString(CultureInfo.InvariantCulture) ?? "null"}.",
                        trip.TripId.ToString("D")));
                }
            }
            else if (trip.State == "IN_PROGRESS")
            {
                if (driver is null)
                {
                    findings.Add(new AuditFinding("ASSIGNED_DRIVER_MISSING",
                        "IN_PROGRESS Trip references a Driver row that does not exist.", trip.TripId.ToString("D")));
                }
                else if (driver.Status != "ON_TRIP" || driver.ActiveTripId != trip.TripId
                         || driver.ReservedTripId is not null || driver.ReservationId is not null)
                {
                    findings.Add(new AuditFinding("DRIVER_TRIP_DISAGREEMENT",
                        "IN_PROGRESS Trip does not match the Driver's ON_TRIP authority.", trip.TripId.ToString("D")));
                }
            }
            else if (TerminalTripStates.Contains(trip.State))
            {
                if (driver is not null
                    && ((driver.Status == "RESERVED" && driver.ReservedTripId == trip.TripId)
                        || (driver.Status == "ON_TRIP" && driver.ActiveTripId == trip.TripId)))
                {
                    var open = openCleanup.FirstOrDefault(c =>
                        c.TripId == trip.TripId
                        && c.Kind == "ReleaseReservation"
                        && c.DriverId == driverId
                        && c.ReservationId == reservationId);
                    if (open is null)
                    {
                        findings.Add(new AuditFinding("TERMINAL_TRIP_WITH_ACTIVE_RESERVATION",
                            "Terminal Trip still holds a live Driver reservation without a durable cleanup obligation.",
                            trip.TripId.ToString("D")));
                    }
                    else if (quiesced)
                    {
                        findings.Add(new AuditFinding("UNRESOLVED_CLEANUP_AT_QUIESCENCE",
                            "Business-quiesced observation still holds a Driver reservation with an open release obligation.",
                            trip.TripId.ToString("D")));
                    }
                    else if (now - open.CreatedAt > _policy.PendingCleanupAge)
                    {
                        findings.Add(new AuditFinding("STUCK_CLEANUP_OBLIGATION",
                            "Recognized cleanup obligation exceeded the accepted age while the Driver stayed reserved.",
                            trip.TripId.ToString("D")));
                    }
                    else
                    {
                        findings.Add(new AuditFinding("RECOGNIZED_PENDING_CLEANUP",
                            "Cleanup is durably recognized and still within policy; not counted as a leak in mid-flight observation.",
                            trip.TripId.ToString("D")));
                    }
                }

                if (trip.State == "COMPLETED" && driver is not null
                    && !effects.Any(e => e.DriverId == driverId && e.TripId == trip.TripId
                                         && e.ReservationId == reservationId && e.Phase == "COMPLETED"))
                {
                    findings.Add(new AuditFinding("COMPLETED_TRIP_WITHOUT_COMPLETED_PROVENANCE",
                        "COMPLETED Trip has no committed COMPLETED Driver lifecycle effect.",
                        trip.TripId.ToString("D")));
                }
            }
        }

        foreach (var driver in drivers.Where(d => d.Status is "RESERVED" or "ON_TRIP"))
        {
            var referenceTrip = driver.Status == "RESERVED" ? driver.ReservedTripId : driver.ActiveTripId;
            var referenceReservation = driver.ReservationId;
            if (referenceTrip is null)
            {
                continue;
            }

            if (!tripsById.TryGetValue(referenceTrip.Value, out var trip))
            {
                findings.Add(new AuditFinding("RESERVATION_REFERENCES_UNKNOWN_TRIP",
                    "Driver current reservation/trip has no Trip row.",
                    $"{driver.DriverId:D}/{referenceTrip:D}"));
                continue;
            }

            if (TerminalTripStates.Contains(trip.State))
            {
                var pending = openCleanup.FirstOrDefault(c =>
                    c.TripId == trip.TripId
                    && c.Kind == "ReleaseReservation"
                    && c.DriverId == driver.DriverId
                    && c.ReservationId == referenceReservation);
                if (pending is not null && quiesced)
                {
                    findings.Add(new AuditFinding("UNRESOLVED_CLEANUP_AT_QUIESCENCE",
                        "Business-quiesced observation still points a Driver reservation at a terminal Trip with an open release obligation.",
                        $"{driver.DriverId:D}/{referenceTrip:D}"));
                    continue;
                }

                if (pending is not null && now - pending.CreatedAt <= _policy.PendingCleanupAge)
                {
                    if (!findings.Any(f => f.Code == "RECOGNIZED_PENDING_CLEANUP"
                                           && f.Record == trip.TripId.ToString("D")))
                    {
                        findings.Add(new AuditFinding("RECOGNIZED_PENDING_CLEANUP",
                            "Cleanup is durably recognized and still within policy; not counted as a leak in mid-flight observation.",
                            trip.TripId.ToString("D")));
                    }

                    continue;
                }

                findings.Add(new AuditFinding("DRIVER_POINTS_AT_TERMINAL_TRIP",
                    "Driver current reservation/trip points at a terminal Trip without a fresh recognized cleanup.",
                    $"{driver.DriverId:D}/{referenceTrip:D}"));
                continue;
            }

            if (trip.AssignedDriverId != driver.DriverId
                || trip.ReservationId != referenceReservation
                || (driver.Status == "RESERVED" && driver.ReservedTripId != trip.TripId)
                || (driver.Status == "ON_TRIP" && driver.ActiveTripId != trip.TripId))
            {
                findings.Add(new AuditFinding("DRIVER_TRIP_DISAGREEMENT",
                    "Driver current reservation identity does not match the Trip assignment.",
                    $"{driver.DriverId:D}/{referenceTrip:D}"));
            }
        }

        var violations = findings.Any(f => f.Code != "RECOGNIZED_PENDING_CLEANUP");
        var status = trips.Count == 0 || drivers.Count == 0
            ? AuditStatus.NOT_RUN
            : violations ? AuditStatus.FAIL : AuditStatus.PASS;
        return new CheckOutcome(
            trips.Count + drivers.Count + cleanup.Count + effects.Count,
            status,
            findings,
            trips.Count == 0 || drivers.Count == 0
                ? "Empty Trip or Driver dataset; PASS would be vacuous."
                : "Driver is authoritative for reservation identity; Trip is authoritative for assignment; " +
                  "agreement is evaluated from both final states.");
    }

    // ------------------------------------------ matching terminal uniqueness
    private async Task<CheckOutcome> RunAud06Async(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(MatchingConnectionString))
        {
            return new CheckOutcome(0, AuditStatus.NOT_RUN, [],
                "Matching database not supplied; terminal uniqueness cannot be evaluated.");
        }

        await using var matching = await ReadOnlySession.OpenAsync(MatchingConnectionString, ct);
        await using var driver = await ReadOnlySession.OpenAsync(DriverConnectionString, ct);
        var rounds = await matching.QueryAsync("""
            SELECT trip_id, matching_round, status, selected_driver_id, selected_reservation_id,
                   result_message_id, cancel_requested, updated_at
            FROM matching_rounds ORDER BY trip_id, matching_round;
            """, ReadRound, ct);
        var attempts = await matching.QueryAsync("""
            SELECT trip_id, matching_round, ordinal, driver_id, reservation_id, state, updated_at
            FROM matching_attempts ORDER BY trip_id, matching_round, ordinal;
            """, ReadAttempt, ct);
        var matchingOutbox = await matching.QueryAsync(OutboxQuery, ReadOutbox, ct);
        var driverOutbox = await driver.QueryAsync(OutboxQuery, ReadOutbox, ct);
        var indexes = await matching.StringsAsync("""
            SELECT indexname FROM pg_indexes WHERE schemaname = 'public'
              AND tablename IN ('matching_rounds', 'matching_attempts');
            """, ct);

        var findings = new List<AuditFinding>();
        var attemptsByRound = attempts
            .GroupBy(a => (a.TripId, a.Round))
            .ToDictionary(g => g.Key, g => g.ToArray());

        foreach (var round in rounds)
        {
            var rowAttempts = attemptsByRound.GetValueOrDefault((round.TripId, round.Round)) ?? [];
            var matchingFailureIntents = matchingOutbox
                .Where(o => o.MessageType == "MatchingFailedV1"
                            && string.Equals(JsonString(o.PayloadJson, "tripId"), round.TripId.ToString("D"),
                                StringComparison.OrdinalIgnoreCase)
                            && JsonInt(o.PayloadJson, "matchingRound") == round.Round)
                .ToArray();
            var driverReservedIntents = driverOutbox
                .Where(o => o.MessageType == "DriverReservedV1"
                            && string.Equals(JsonString(o.PayloadJson, "tripId"), round.TripId.ToString("D"),
                                StringComparison.OrdinalIgnoreCase)
                            && JsonInt(o.PayloadJson, "matchingRound") == round.Round)
                .ToArray();

            if (round.Status == "RESERVED")
            {
                if (round.SelectedDriverId is null || round.SelectedReservationId is null)
                {
                    findings.Add(new AuditFinding("RESERVED_ROUND_MISSING_IDENTITY",
                        "RESERVED round lacks selected driver/reservation identity.",
                        $"{round.TripId:D}/{round.Round}"));
                }

                var reservedAttempts = rowAttempts.Where(a => a.State == "RESERVED").ToArray();
                if (reservedAttempts.Length != 1)
                {
                    findings.Add(new AuditFinding("MULTIPLE_RESERVED_ATTEMPTS",
                        $"RESERVED round has {reservedAttempts.Length} RESERVED attempt rows; exactly one is required.",
                        $"{round.TripId:D}/{round.Round}"));
                }
                else if (round.SelectedDriverId is not null && round.SelectedReservationId is not null
                         && (reservedAttempts[0].DriverId != round.SelectedDriverId
                             || reservedAttempts[0].ReservationId != round.SelectedReservationId))
                {
                    findings.Add(new AuditFinding("RESERVED_ROUND_ATTEMPT_MISMATCH",
                        "Persisted RESERVED attempt identity differs from the round's selected identity.",
                        $"{round.TripId:D}/{round.Round}"));
                }

                if (rowAttempts.Any(a => UnresolvedAttemptStates.Contains(a.State)))
                {
                    findings.Add(new AuditFinding("RESERVED_ROUND_HAS_UNRESOLVED_ATTEMPT",
                        "A RESERVED terminal round still carries READY/IN_FLIGHT/UNKNOWN attempts.",
                        $"{round.TripId:D}/{round.Round}"));
                }

                if (round.ResultMessageId is not null)
                {
                    findings.Add(new AuditFinding("RESERVED_ROUND_CARRIES_RESULT_IDENTITY",
                        "RESERVED round carries a failure/result message identity.",
                        $"{round.TripId:D}/{round.Round}"));
                }

                var expectedIntents = round.SelectedDriverId is null || round.SelectedReservationId is null
                    ? []
                    : driverReservedIntents.Where(o =>
                        string.Equals(o.AggregateId, round.SelectedDriverId.Value.ToString("N"),
                            StringComparison.OrdinalIgnoreCase)
                        && JsonGuid(o.PayloadJson, "reservationId") == round.SelectedReservationId).ToArray();
                if (expectedIntents.Length == 0)
                {
                    findings.Add(new AuditFinding("MISSING_DRIVER_RESERVED_INTENT",
                        "Winning reservation has no durable DriverReservedV1 Outbox intent.",
                        $"{round.TripId:D}/{round.Round}"));
                }
                else if (expectedIntents.Length > 1)
                {
                    findings.Add(new AuditFinding("DUPLICATE_DRIVER_RESERVED_INTENT",
                        "Winning reservation has more than one DriverReservedV1 Outbox intent.",
                        $"{round.TripId:D}/{round.Round}"));
                }

                if (matchingFailureIntents.Length > 0)
                {
                    findings.Add(new AuditFinding("CONTRADICTORY_TERMINAL_RESULTS",
                        "RESERVED round also has a MatchingFailedV1 terminal intent.",
                        $"{round.TripId:D}/{round.Round}"));
                }
            }
            else if (round.Status == "NO_DRIVER")
            {
                if (round.ResultMessageId is null)
                {
                    findings.Add(new AuditFinding("NO_DRIVER_MISSING_RESULT_IDENTITY",
                        "NO_DRIVER terminal has no persisted result identity.",
                        $"{round.TripId:D}/{round.Round}"));
                }

                if (rowAttempts.Any(a => a.State != "REJECTED"))
                {
                    findings.Add(new AuditFinding("NO_DRIVER_WITH_UNRESOLVED_ATTEMPTS",
                        "NO_DRIVER declared while at least one attempt is not conclusively REJECTED.",
                        $"{round.TripId:D}/{round.Round}"));
                }

                if (round.SelectedDriverId is not null || round.SelectedReservationId is not null)
                {
                    findings.Add(new AuditFinding("NO_DRIVER_WITH_SELECTED_IDENTITY",
                        "NO_DRIVER round carries a selected driver/reservation identity.",
                        $"{round.TripId:D}/{round.Round}"));
                }

                var intent = round.ResultMessageId is null
                    ? null
                    : matchingFailureIntents.FirstOrDefault(o =>
                        string.Equals(o.MessageId, round.ResultMessageId.Value.ToString("D"),
                            StringComparison.OrdinalIgnoreCase));
                if (intent is null)
                {
                    findings.Add(new AuditFinding("MISSING_OR_MISMATCHED_FAILURE_INTENT",
                        "NO_DRIVER result identity has no matching MatchingFailedV1 Outbox intent.",
                        $"{round.TripId:D}/{round.Round}"));
                }

                if (driverReservedIntents.Length > 0)
                {
                    findings.Add(new AuditFinding("CONTRADICTORY_TERMINAL_RESULTS",
                        "NO_DRIVER round also has a DriverReservedV1 intent for the same trip/round.",
                        $"{round.TripId:D}/{round.Round}"));
                }
            }
            else if (round.Status is "CANCELLED" or "FAILED")
            {
                if (rowAttempts.Any(a => a.State == "RESERVED"))
                {
                    findings.Add(new AuditFinding("TERMINAL_ROUND_HAS_RESERVED_ATTEMPT",
                        $"{round.Status} round still carries a RESERVED attempt.",
                        $"{round.TripId:D}/{round.Round}"));
                }
            }
            else if (round.Status == "PROCESSING"
                     && (round.SelectedDriverId is not null || round.SelectedReservationId is not null
                         || round.ResultMessageId is not null))
            {
                findings.Add(new AuditFinding("NONTERMINAL_CARRIES_TERMINAL_FIELDS",
                    "PROCESSING round carries terminal identity fields.",
                    $"{round.TripId:D}/{round.Round}"));
            }
        }

        foreach (var duplicate in rounds
                     .Where(r => r.ResultMessageId is not null)
                     .GroupBy(r => r.ResultMessageId!.Value)
                     .Where(g => g.Count() > 1))
        {
            findings.Add(new AuditFinding("DUPLICATE_RESULT_IDENTITY",
                "One result identity is attached to more than one Matching round.",
                duplicate.Key.ToString("D")));
        }

        if (!indexes.Contains("ux_matching_rounds_result_message_id", StringComparer.OrdinalIgnoreCase))
        {
            findings.Add(new AuditFinding("MISSING_RESULT_IDENTITY_INDEX",
                "ux_matching_rounds_result_message_id is absent; one-result-identity is not storage-enforced."));
        }

        if (!indexes.Contains("ux_matching_attempts_trip_round_driver", StringComparer.OrdinalIgnoreCase))
        {
            findings.Add(new AuditFinding("MISSING_ATTEMPT_DRIVER_INDEX",
                "ux_matching_attempts_trip_round_driver is absent; one-attempt-per-driver is not storage-enforced."));
        }

        var status = rounds.Count == 0
            ? AuditStatus.NOT_RUN
            : findings.Count == 0 ? AuditStatus.PASS : AuditStatus.FAIL;
        return new CheckOutcome(
            rounds.Count + attempts.Count + matchingOutbox.Count + driverOutbox.Count + indexes.Count,
            status,
            findings,
            rounds.Count == 0 ? "Empty Matching terminal dataset; PASS would be vacuous." : null);
    }

    // -------------------------------------------- terminal trip leak detection
    private async Task<CheckOutcome> RunAud07Async(string observationMode, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(TripConnectionString))
        {
            return new CheckOutcome(0, AuditStatus.NOT_RUN, [],
                "Trip database not supplied; terminal-trip leak detection cannot be evaluated.");
        }

        await using var tripDb = await ReadOnlySession.OpenAsync(TripConnectionString, ct);
        await using var driverDb = await ReadOnlySession.OpenAsync(DriverConnectionString, ct);
        var trips = await tripDb.QueryAsync("""
            SELECT trip_id, state, assigned_driver_id, reservation_id, matching_round, current_matching_round
            FROM trips WHERE state IN ('COMPLETED', 'CANCELLED', 'MATCH_FAILED') ORDER BY trip_id;
            """, ReadTrip, ct);
        var cleanup = await tripDb.QueryAsync("""
            SELECT trip_id, kind, driver_id, reservation_id, matching_round, created_at, completed_at
            FROM trip_cleanup_intents ORDER BY trip_id, key;
            """, ReadCleanup, ct);
        var drivers = await driverDb.QueryAsync("""
            SELECT driver_id, status, reserved_trip_id, reservation_id, active_trip_id, reserved_round
            FROM drivers ORDER BY driver_id;
            """, ReadDriver, ct);

        var findings = new List<AuditFinding>();
        var now = _clock.GetUtcNow();
        var quiesced = IsBusinessQuiesced(observationMode);
        var openCleanup = cleanup.Where(c => c.CompletedAt is null).ToArray();

        foreach (var trip in trips)
        {
            var held = drivers.Where(d =>
                    (d.Status == "RESERVED" && d.ReservedTripId == trip.TripId)
                    || (d.Status == "ON_TRIP" && d.ActiveTripId == trip.TripId))
                .ToArray();
            foreach (var driver in held)
            {
                var reservationId = driver.Status == "RESERVED" ? driver.ReservationId : trip.ReservationId;
                var open = openCleanup.FirstOrDefault(c =>
                    c.TripId == trip.TripId
                    && c.Kind == "ReleaseReservation"
                    && c.DriverId == driver.DriverId
                    && c.ReservationId == reservationId);
                if (open is null)
                {
                    findings.Add(new AuditFinding("LEAKED_RESERVATION",
                        "Terminal Trip still holds an active Driver reservation with no open cleanup obligation.",
                        $"{trip.TripId:D}/{driver.DriverId:D}"));
                }
                else if (quiesced)
                {
                    findings.Add(new AuditFinding("UNRESOLVED_CLEANUP_AT_QUIESCENCE",
                        "Business-quiesced observation still holds a Driver reservation with an open release obligation.",
                        $"{trip.TripId:D}/{driver.DriverId:D}"));
                }
                else if (now - open.CreatedAt > _policy.PendingCleanupAge)
                {
                    findings.Add(new AuditFinding("STUCK_CLEANUP_OBLIGATION",
                        "Cleanup remains open beyond the accepted age while the Driver is still reserved.",
                        $"{trip.TripId:D}/{driver.DriverId:D}"));
                }
                else
                {
                    findings.Add(new AuditFinding("RECOGNIZED_PENDING_CLEANUP",
                        "Cleanup is durably recognized and still within policy; not counted as a leak in mid-flight observation.",
                        $"{trip.TripId:D}/{driver.DriverId:D}"));
                }
            }
        }

        var violations = findings.Any(f => f.Code != "RECOGNIZED_PENDING_CLEANUP");
        var status = trips.Count == 0
            ? AuditStatus.NOT_RUN
            : violations ? AuditStatus.FAIL : AuditStatus.PASS;
        return new CheckOutcome(
            trips.Count + drivers.Count + cleanup.Count,
            status,
            findings,
            trips.Count == 0
                ? "No terminal Trip in this scenario; leak PASS would be vacuous."
                : "Only the current drivers row is reservation authority; historical lifecycle effects are not leaks. " +
                  "Fresh recognized cleanup is accepted only in mid-flight observation; post-scenario modes require completed release obligations.");
    }

    // ----------------------------------------------- outbox/attempt resolution
    private async Task<CheckOutcome> RunAud08Async(string observationMode, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(MatchingConnectionString))
        {
            return new CheckOutcome(0, AuditStatus.NOT_RUN, [],
                "Matching database not supplied; Outbox/attempt resolution cannot be evaluated.");
        }

        await using var driver = await ReadOnlySession.OpenAsync(DriverConnectionString, ct);
        await using var matching = await ReadOnlySession.OpenAsync(MatchingConnectionString, ct);
        var driverOutbox = await driver.QueryAsync(OutboxQuery, ReadOutbox, ct);
        var matchingOutbox = await matching.QueryAsync(OutboxQuery, ReadOutbox, ct);
        var attempts = await matching.QueryAsync("""
            SELECT trip_id, matching_round, ordinal, driver_id, reservation_id, state, updated_at
            FROM matching_attempts ORDER BY trip_id, matching_round, ordinal;
            """, ReadAttempt, ct);
        var rounds = await matching.QueryAsync("""
            SELECT trip_id, matching_round, status, selected_driver_id, selected_reservation_id,
                   result_message_id, cancel_requested, updated_at
            FROM matching_rounds ORDER BY trip_id, matching_round;
            """, ReadRound, ct);

        var findings = new List<AuditFinding>();
        var now = _clock.GetUtcNow();
        var relayQuiesced = IsRelayQuiesced(observationMode);

        foreach (var (source, rows) in new[] { ("driver", driverOutbox), ("matching", matchingOutbox) })
        {
            foreach (var row in rows.Where(r => r.Status == "PENDING"))
            {
                if (relayQuiesced)
                {
                    findings.Add(new AuditFinding("PENDING_OUTBOX_AT_QUIESCENCE",
                        $"{source} Outbox row is PENDING in a relay-quiesced observation (pending must be 0).",
                        $"{row.MessageId}/{row.MessageType}"));
                    continue;
                }

                var age = now - row.OccurredAt;
                if (row.ClaimedAt is not null && now - row.ClaimedAt.Value <= _policy.PendingOutboxAge)
                {
                    continue;
                }

                if (age > _policy.PendingOutboxAge)
                {
                    findings.Add(new AuditFinding("STUCK_PENDING_OUTBOX",
                        $"{source} Outbox row stayed PENDING for {age.TotalMinutes:F1} min " +
                        $"(policy {_policy.PendingOutboxAge.TotalMinutes:F0} min).",
                        $"{row.MessageId}/{row.MessageType}"));
                }
            }

            foreach (var row in rows.Where(r => r.Status == "FAILED_VISIBLE"))
            {
                findings.Add(new AuditFinding("VISIBLE_TERMINAL_OUTBOX",
                    $"{source} Outbox row is visibly failed (not silent); review policy applies.",
                    $"{row.MessageId}/{row.MessageType}"));
            }
        }

        foreach (var attempt in attempts.Where(a => UnresolvedAttemptStates.Contains(a.State)))
        {
            var age = now - attempt.UpdatedAt;
            if (age > _policy.StaleUnresolvedAge)
            {
                findings.Add(new AuditFinding("STUCK_UNRESOLVED_ATTEMPT",
                    $"Matching attempt stayed {attempt.State} for {age.TotalMinutes:F1} min " +
                    $"(policy {_policy.StaleUnresolvedAge.TotalMinutes:F0} min).",
                    $"{attempt.TripId:D}/{attempt.Round}/{attempt.Ordinal}"));
            }
        }

        foreach (var round in rounds.Where(r => r.Status == "PROCESSING"))
        {
            var age = now - round.UpdatedAt;
            if (age > _policy.StaleUnresolvedAge)
            {
                findings.Add(new AuditFinding("STUCK_PROCESSING_ROUND",
                    $"Matching round stayed PROCESSING for {age.TotalMinutes:F1} min " +
                    $"(policy {_policy.StaleUnresolvedAge.TotalMinutes:F0} min).",
                    $"{round.TripId:D}/{round.Round}"));
            }
        }

        if (!string.IsNullOrWhiteSpace(TripConnectionString))
        {
            findings.Add(new AuditFinding("TRIP_SAGA_SCOPE",
                "Trip Saga/Outbox unresolved age is Trip-owned and intentionally not inspected here; " +
                "the Trip-owned AUD-08 PASS covers the Trip authority."));
        }

        var inspected = driverOutbox.Count + matchingOutbox.Count + attempts.Count + rounds.Count;
        var violations = findings.Any(f =>
            f.Code is not ("VISIBLE_TERMINAL_OUTBOX" or "TRIP_SAGA_SCOPE"));
        var status = inspected == 0
            ? AuditStatus.NOT_RUN
            : violations ? AuditStatus.FAIL : AuditStatus.PASS;
        return new CheckOutcome(
            inspected,
            status,
            findings,
            inspected == 0
                ? "No Outbox/attempt/round rows inspected; PASS would be vacuous."
                : relayQuiesced
                    ? "Relay-quiesced policy: any PENDING Outbox row is a violation; " +
                      $"unresolved attempts/rounds <= {_policy.StaleUnresolvedAge.TotalMinutes:F0} min; " +
                      "FAILED_VISIBLE is visible-by-design and reported for review."
                    : $"Relay not executed for this observation; fresh PENDING <= {_policy.PendingOutboxAge.TotalMinutes:F0} min accepted by the age policy, " +
                      $"unresolved attempts/rounds <= {_policy.StaleUnresolvedAge.TotalMinutes:F0} min, " +
                      "FAILED_VISIBLE is visible-by-design and reported for review.");
    }

    // ------------------------------------------------------------- row readers
    private sealed record DriverRow(
        Guid DriverId,
        string Status,
        Guid? ReservedTripId,
        Guid? ReservationId,
        Guid? ActiveTripId,
        int? ReservedRound);

    private sealed record TripRow(
        Guid TripId,
        string State,
        Guid? AssignedDriverId,
        Guid? ReservationId,
        int? MatchingRound,
        int CurrentMatchingRound);

    private sealed record RoundRow(
        Guid TripId,
        int Round,
        string Status,
        Guid? SelectedDriverId,
        Guid? SelectedReservationId,
        Guid? ResultMessageId,
        bool CancelRequested,
        DateTimeOffset UpdatedAt);

    private sealed record AttemptRow(
        Guid TripId,
        int Round,
        int Ordinal,
        Guid DriverId,
        Guid ReservationId,
        string State,
        DateTimeOffset UpdatedAt);

    private sealed record CleanupRow(
        Guid TripId,
        string Kind,
        Guid? DriverId,
        Guid? ReservationId,
        int MatchingRound,
        DateTimeOffset CreatedAt,
        DateTimeOffset? CompletedAt);

    private sealed record OutboxRow(
        string MessageId,
        string MessageType,
        string? AggregateId,
        string PayloadJson,
        string Status,
        DateTimeOffset OccurredAt,
        DateTimeOffset NextAttemptAt,
        DateTimeOffset? ClaimedAt,
        int AttemptCount);

    private const string OutboxQuery = """
        SELECT message_id, message_type, aggregate_id, payload_json::text,
               status, occurred_at, next_attempt_at, claimed_at, attempt_count
        FROM outbox_messages ORDER BY occurred_at, message_id;
        """;

    private static DriverRow ReadDriver(NpgsqlDataReader r) => new(
        r.GetGuid(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetGuid(2),
        r.IsDBNull(3) ? null : r.GetGuid(3), r.IsDBNull(4) ? null : r.GetGuid(4),
        r.IsDBNull(5) ? null : r.GetInt32(5));

    private static TripRow ReadTrip(NpgsqlDataReader r) => new(
        r.GetGuid(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetGuid(2),
        r.IsDBNull(3) ? null : r.GetGuid(3), r.IsDBNull(4) ? null : r.GetInt32(4), r.GetInt32(5));

    private static RoundRow ReadRound(NpgsqlDataReader r) => new(
        r.GetGuid(0), r.GetInt32(1), r.GetString(2),
        r.IsDBNull(3) ? null : r.GetGuid(3), r.IsDBNull(4) ? null : r.GetGuid(4),
        r.IsDBNull(5) ? null : r.GetGuid(5), r.GetBoolean(6), r.GetFieldValue<DateTimeOffset>(7));

    private static AttemptRow ReadAttempt(NpgsqlDataReader r) => new(
        r.GetGuid(0), r.GetInt32(1), r.GetInt32(2), r.GetGuid(3), r.GetGuid(4),
        r.GetString(5), r.GetFieldValue<DateTimeOffset>(6));

    private static CleanupRow ReadCleanup(NpgsqlDataReader r) => new(
        r.GetGuid(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetGuid(2),
        r.IsDBNull(3) ? null : r.GetGuid(3), r.GetInt32(4),
        r.GetFieldValue<DateTimeOffset>(5), r.IsDBNull(6) ? null : r.GetFieldValue<DateTimeOffset>(6));

    private static OutboxRow ReadOutbox(NpgsqlDataReader r) => new(
        r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3),
        r.GetString(4), r.GetFieldValue<DateTimeOffset>(5), r.GetFieldValue<DateTimeOffset>(6),
        r.IsDBNull(7) ? null : r.GetFieldValue<DateTimeOffset>(7), r.GetInt32(8));

    private static string? JsonString(string json, string property)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }

    private static Guid? JsonGuid(string json, string property) =>
        Guid.TryParse(JsonString(json, property), out var value) ? value : null;

    private static int? JsonInt(string json, string property)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var number)
                ? number
                : null;
    }

    // --------------------------------------------------------- read-only access
    private sealed class ReadOnlySession : IAsyncDisposable
    {
        private readonly NpgsqlConnection _connection;
        private readonly NpgsqlTransaction _transaction;

        private ReadOnlySession(NpgsqlConnection connection, NpgsqlTransaction transaction)
        {
            _connection = connection;
            _transaction = transaction;
        }

        public static async Task<ReadOnlySession> OpenAsync(
            string connectionString, CancellationToken ct)
        {
            var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(ct);
            var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
            await using var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY;", connection, transaction);
            await readOnly.ExecuteNonQueryAsync(ct);
            return new ReadOnlySession(connection, transaction);
        }

        public async Task<List<T>> QueryAsync<T>(
            string sql, Func<NpgsqlDataReader, T> map, CancellationToken ct)
        {
            await using var command = new NpgsqlCommand(sql, _connection, _transaction);
            await using var reader = await command.ExecuteReaderAsync(ct);
            var rows = new List<T>();
            while (await reader.ReadAsync(ct))
            {
                rows.Add(map(reader));
            }

            return rows;
        }

        public async Task<List<string>> StringsAsync(string sql, CancellationToken ct)
        {
            await using var command = new NpgsqlCommand(sql, _connection, _transaction);
            await using var reader = await command.ExecuteReaderAsync(ct);
            var rows = new List<string>();
            while (await reader.ReadAsync(ct))
            {
                rows.Add(reader.GetString(0));
            }

            return rows;
        }

        public async Task<long> ScalarAsync(string sql, CancellationToken ct)
        {
            await using var command = new NpgsqlCommand(sql, _connection, _transaction);
            var value = await command.ExecuteScalarAsync(ct);
            return Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }

        public async ValueTask DisposeAsync()
        {
            await _transaction.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
