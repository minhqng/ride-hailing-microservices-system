// Executable positive scenarios and non-vacuity controls.
//
// Positive runs drive real handlers/repositories/HTTP and then audit the
// final states read-only. Negative controls seed corruption ONLY in isolated
// databases and assert the auditor detects it. PostgreSQL-enforced guards are
// demonstrated as enforcement, never bypassed to fake a detection.
using Npgsql;
using RideHailing.DriverService.Domain.Drivers;
using RideHailing.DriverService.Infrastructure.Persistence;
using RideHailing.MatchingService.Application.Reserve;
using RideHailing.Messaging.Outbox;
using RideHailing.TripService.Application.Trips.Lifecycle;
using Xunit.Abstractions;

namespace RideHailing.InvariantAudit.Tests;

[Collection(InvariantAuditCollection.Name)]
public sealed class AuditScenarioTests
{
    private readonly InvariantAuditFixture _fixture;
    private readonly ITestOutputHelper _output;

    public AuditScenarioTests(InvariantAuditFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    // ------------------------------------------------------------ positive
    [Fact]
    public async Task ReservedAgreement_ProducesAuthoritativePasses()
    {
        var db = await _fixture.CreateDatabasesAsync("reserved");
        await using var scenario = await AuditScenario.CreateAsync(db);
        var driver = await scenario.SeedAvailableDriverAsync();
        var rider = Guid.NewGuid();
        var trip = await scenario.CreateTripAsync(rider, "audit-reserved");
        await scenario.StartMatchingAsync(trip, rider);

        var reserved = await scenario.ReserveAsync(trip, 1, [driver.DriverId]);
        Assert.Equal(RoundOutcome.Reserved, reserved.Outcome);
        await scenario.RecordReservationAsync(trip, driver.DriverId, reserved.ReservationId!.Value, 1);

        var run = await RunAuditAsync(db, "reserved-agreement");
        _output.WriteLine($"Run {run.RunId}");
        Assert.Equal(AuditStatus.PASS, Check(run, "AUD-01").Status);
        Assert.Equal(AuditStatus.PASS, Check(run, "AUD-02").Status);
        Assert.Equal(AuditStatus.PASS, Check(run, "AUD-04").Status);
        Assert.Equal(AuditStatus.PASS, Check(run, "AUD-06").Status);
        Assert.Equal(AuditStatus.PASS, Check(run, "AUD-08").Status);
        Assert.Equal(AuditStatus.NOT_RUN, Check(run, "AUD-07").Status);
        Assert.Equal(AuditStatus.NOT_RUN, Check(run, "AUD-03").Status);
        Assert.Equal(AuditStatus.NOT_RUN, Check(run, "AUD-05").Status);
    }

    [Fact]
    public async Task NoDriverTerminal_ProducesUniquenessAndReleasePasses()
    {
        var db = await _fixture.CreateDatabasesAsync("no_driver");
        await using var scenario = await AuditScenario.CreateAsync(db);
        var first = await scenario.SeedOfflineDriverAsync();
        var second = await scenario.SeedOfflineDriverAsync();
        var rider = Guid.NewGuid();
        var trip = await scenario.CreateTripAsync(rider, "audit-no-driver");
        await scenario.StartMatchingAsync(trip, rider);

        var result = await scenario.ReserveAsync(trip, 1, [first.DriverId, second.DriverId]);
        Assert.Equal(RoundOutcome.NoDriver, result.Outcome);
        await scenario.MarkMatchingFailedAsync(trip, 1);

        var run = await RunAuditAsync(db, "no-driver-terminal");
        Assert.Equal(AuditStatus.PASS, Check(run, "AUD-01").Status);
        Assert.Equal(AuditStatus.PASS, Check(run, "AUD-02").Status);
        Assert.Equal(AuditStatus.PASS, Check(run, "AUD-04").Status);
        Assert.Equal(AuditStatus.PASS, Check(run, "AUD-06").Status);
        Assert.Equal(AuditStatus.PASS, Check(run, "AUD-07").Status);
        Assert.Equal(AuditStatus.PASS, Check(run, "AUD-08").Status);
    }

    [Fact]
    public async Task CancelReleaseQuiesced_ProducesNoLeakPass()
    {
        var db = await _fixture.CreateDatabasesAsync("cancel_release");
        await using var scenario = await AuditScenario.CreateAsync(db);
        var driver = await scenario.SeedAvailableDriverAsync();
        var rider = Guid.NewGuid();
        var trip = await scenario.CreateTripAsync(rider, "audit-cancel");
        await scenario.StartMatchingAsync(trip, rider);
        var reserved = await scenario.ReserveAsync(trip, 1, [driver.DriverId]);
        Assert.Equal(RoundOutcome.Reserved, reserved.Outcome);
        var reservationId = reserved.ReservationId!.Value;
        await scenario.RecordReservationAsync(trip, driver.DriverId, reservationId, 1);

        await scenario.CancelAsync(trip, rider);
        var release = await scenario.ReleaseDriverAsync(driver.DriverId, trip, reservationId);
        Assert.Equal(DriverMutationOutcome.Applied, release.Outcome);

        var cleanup = await scenario.LoadOpenCleanupAsync(trip);
        Assert.NotEmpty(cleanup);
        // Only the Driver release obligation is executable in this harness.
        // CancelMatching stays recognized-pending and belongs to Matching
        // cancel intake; the audit therefore records a cleanup-scope
        // quiescence observation.
        var releaseIntent = Assert.Single(cleanup, c => c.Kind == "ReleaseReservation");
        await scenario.ConfirmCleanupAsync(trip, releaseIntent.Key, CleanupAuthorityResult.Released);

        var run = await RunAuditAsync(db, "cancel-release-quiesced", "POST_SCENARIO_CLEANUP_QUIESCED_NO_RELAY");
        Assert.Equal(AuditStatus.PASS, Check(run, "AUD-01").Status);
        Assert.Equal(AuditStatus.PASS, Check(run, "AUD-02").Status);
        Assert.Equal(AuditStatus.PASS, Check(run, "AUD-04").Status);
        Assert.Equal(AuditStatus.PASS, Check(run, "AUD-06").Status);
        Assert.Equal(AuditStatus.PASS, Check(run, "AUD-07").Status);
        Assert.Equal(AuditStatus.PASS, Check(run, "AUD-08").Status);
    }

    [Fact]
    public async Task DriverLifecycleOnly_ReportsAuthorityDispositions()
    {
        var db = await _fixture.CreateDriverOnlyAsync("driver_lifecycle");
        await using var dataSource = NpgsqlDataSource.Create(db.DriverConnectionString);
        var repository = new DriverRepository(dataSource, new NpgsqlOutboxStore(dataSource));

        var active = Driver.Create(Guid.NewGuid(), Guid.NewGuid());
        active.GoOnline(active.Version);
        Assert.True(await repository.TryInsertAsync(active));
        var activeTrip = Guid.NewGuid();
        var activeReservation = Guid.NewGuid();
        var reserved = await repository.TryReserveAsync(
            active.DriverId, activeTrip, activeReservation, active.Version, 1);
        Assert.Equal(ReserveOutcome.Won, reserved.Outcome);
        var started = await repository.TryMarkTripStartedAsync(
            active.DriverId, activeTrip, activeReservation);
        Assert.Equal("Applied", started.Disposition);

        var completed = Driver.Create(Guid.NewGuid(), Guid.NewGuid());
        completed.GoOnline(completed.Version);
        Assert.True(await repository.TryInsertAsync(completed));
        var completedTrip = Guid.NewGuid();
        var completedReservation = Guid.NewGuid();
        Assert.Equal(ReserveOutcome.Won, (await repository.TryReserveAsync(
            completed.DriverId, completedTrip, completedReservation, completed.Version, 1)).Outcome);
        Assert.Equal("Applied", (await repository.TryMarkTripStartedAsync(
            completed.DriverId, completedTrip, completedReservation)).Disposition);
        Assert.Equal("Applied", (await repository.TryMarkTripCompletedAsync(
            completed.DriverId, completedTrip, completedReservation)).Disposition);

        var auditor = new InvariantAuditor(
            db.DriverConnectionString, matchingConnectionString: null, tripConnectionString: null);
        var run = await auditor.RunAsync("driver-lifecycle", "POST_SCENARIO_CLEANUP_QUIESCED_NO_RELAY");
        _fixture.Report.AddRun(run);

        Assert.Equal(AuditStatus.PASS, Check(run, "AUD-01").Status);
        Assert.Equal(AuditStatus.PASS, Check(run, "AUD-02").Status);
        Assert.Equal(AuditStatus.NOT_RUN, Check(run, "AUD-04").Status);
        Assert.Equal(AuditStatus.NOT_RUN, Check(run, "AUD-06").Status);
        Assert.Equal(AuditStatus.NOT_RUN, Check(run, "AUD-07").Status);
        Assert.Equal(AuditStatus.NOT_RUN, Check(run, "AUD-08").Status);
    }

    [Fact]
    public async Task LateReleaseFenced_KeepsNewerReservation()
    {
        var db = await _fixture.CreateDatabasesAsync("late_release");
        await using var scenario = await AuditScenario.CreateAsync(db);
        var driver = await scenario.SeedAvailableDriverAsync();

        var rider1 = Guid.NewGuid();
        var trip1 = await scenario.CreateTripAsync(rider1, "audit-late-release-1");
        await scenario.StartMatchingAsync(trip1, rider1);
        var first = await scenario.ReserveAsync(trip1, 1, [driver.DriverId]);
        Assert.Equal(RoundOutcome.Reserved, first.Outcome);
        var r1 = first.ReservationId!.Value;
        await scenario.RecordReservationAsync(trip1, driver.DriverId, r1, 1);
        await scenario.CancelAsync(trip1, rider1);
        Assert.Equal(DriverMutationOutcome.Applied,
            (await scenario.ReleaseDriverAsync(driver.DriverId, trip1, r1)).Outcome);
        var cleanup1 = await scenario.LoadOpenCleanupAsync(trip1);
        var release1 = Assert.Single(cleanup1, c => c.Kind == "ReleaseReservation");
        await scenario.ConfirmCleanupAsync(trip1, release1.Key, CleanupAuthorityResult.Released);

        var rider2 = Guid.NewGuid();
        var trip2 = await scenario.CreateTripAsync(rider2, "audit-late-release-2");
        await scenario.StartMatchingAsync(trip2, rider2);
        var second = await scenario.ReserveAsync(trip2, 1, [driver.DriverId]);
        Assert.Equal(RoundOutcome.Reserved, second.Outcome);
        var r2 = second.ReservationId!.Value;
        await scenario.RecordReservationAsync(trip2, driver.DriverId, r2, 1);

        var late = await scenario.ReleaseDriverAsync(driver.DriverId, trip1, r1);
        Assert.Equal(DriverMutationOutcome.Conflict, late.Outcome);
        Assert.Equal(DriverErrors.StaleReservation, late.ConflictCode);

        var current = await scenario.Drivers.LoadAsync(driver.DriverId);
        Assert.NotNull(current);
        Assert.Equal(DriverStatus.Reserved, current!.Status);
        Assert.Equal(trip2, current.ReservedTripId);
        Assert.Equal(r2, current.ReservationId);

        var run = await RunAuditAsync(db, "late-release-fenced");
        Assert.Equal(AuditStatus.PASS, Check(run, "AUD-01").Status);
        Assert.Equal(AuditStatus.PASS, Check(run, "AUD-02").Status);
        Assert.Equal(AuditStatus.PASS, Check(run, "AUD-04").Status);
        Assert.Equal(AuditStatus.PASS, Check(run, "AUD-06").Status);
        Assert.Equal(AuditStatus.PASS, Check(run, "AUD-07").Status);
        Assert.Equal(AuditStatus.PASS, Check(run, "AUD-08").Status);
    }

    [Fact]
    public async Task RecognizedPendingCleanup_IsNotTreatedAsLeak()
    {
        var db = await _fixture.CreateDatabasesAsync("pending_cleanup");
        await using var scenario = await AuditScenario.CreateAsync(db);
        var driver = await scenario.SeedAvailableDriverAsync();
        var rider = Guid.NewGuid();
        var trip = await scenario.CreateTripAsync(rider, "audit-pending-cleanup");
        await scenario.StartMatchingAsync(trip, rider);
        var reserved = await scenario.ReserveAsync(trip, 1, [driver.DriverId]);
        Assert.Equal(RoundOutcome.Reserved, reserved.Outcome);
        await scenario.RecordReservationAsync(trip, driver.DriverId, reserved.ReservationId!.Value, 1);
        await scenario.CancelAsync(trip, rider);

        var cleanup = await scenario.LoadOpenCleanupAsync(trip);
        Assert.Contains(cleanup, c => c.Kind == "ReleaseReservation");

        var run = await RunAuditAsync(db, "recognized-pending-cleanup", "MID_FLIGHT_OBSERVATION");
        var aud04 = Check(run, "AUD-04");
        var aud07 = Check(run, "AUD-07");
        Assert.Equal(AuditStatus.PASS, aud04.Status);
        Assert.Equal(AuditStatus.PASS, aud07.Status);
        Assert.Contains(aud04.Findings, f => f.Code == "RECOGNIZED_PENDING_CLEANUP");
        Assert.Contains(aud07.Findings, f => f.Code == "RECOGNIZED_PENDING_CLEANUP");
        Assert.Equal(AuditStatus.PASS, Check(run, "AUD-01").Status);
        Assert.Equal(AuditStatus.PASS, Check(run, "AUD-06").Status);
        Assert.Equal(AuditStatus.PASS, Check(run, "AUD-08").Status);
    }

    // ------------------------------------------------------------ negative
    [Fact]
    public async Task Negative_ContradictoryMatchingTerminalFacts_FailsAud06()
    {
        var db = await _fixture.CreateDatabasesAsync("neg_contradictory");
        await using var scenario = await AuditScenario.CreateAsync(db);
        var driver = await scenario.SeedAvailableDriverAsync();
        var rider = Guid.NewGuid();
        var trip = await scenario.CreateTripAsync(rider, "audit-neg-contradictory");
        await scenario.StartMatchingAsync(trip, rider);
        var reserved = await scenario.ReserveAsync(trip, 1, [driver.DriverId]);
        await scenario.RecordReservationAsync(trip, driver.DriverId, reserved.ReservationId!.Value, 1);

        var secondDriver = Guid.NewGuid();
        await ExecuteAsync(db.MatchingConnectionString, """
            INSERT INTO matching_attempts
                (trip_id, matching_round, ordinal, driver_id, reservation_id, state)
            VALUES (@trip, 1, 1, @driver, @reservation, 'RESERVED');
            """, ("trip", trip), ("driver", secondDriver), ("reservation", Guid.NewGuid()));

        var run = await RunAuditAsync(db, "negative-contradictory-terminal", "NEGATIVE_CONTROL_SEEDED");
        var check = Check(run, "AUD-06");
        Assert.Equal(AuditStatus.FAIL, check.Status);
        Assert.Contains(check.Findings, f => f.Code == "MULTIPLE_RESERVED_ATTEMPTS");
        RecordControl("NEG-06A", "AUD-06",
            "Second RESERVED attempt added to an already RESERVED round.",
            "AUD-06 FAIL with multiple RESERVED attempts.",
            $"{check.Status} / {string.Join(",", check.Findings.Select(f => f.Code).Distinct())}",
            "isolated matching database (neg_contradictory)",
            "No production code path creates two RESERVED attempts in one round; the auditor detects ledger corruption.");
    }

    [Fact]
    public async Task Negative_MissingTerminalIntent_FailsAud06()
    {
        var db = await _fixture.CreateDatabasesAsync("neg_missing_intent");
        await using var scenario = await AuditScenario.CreateAsync(db);
        var first = await scenario.SeedOfflineDriverAsync();
        var second = await scenario.SeedOfflineDriverAsync();
        var rider = Guid.NewGuid();
        var trip = await scenario.CreateTripAsync(rider, "audit-neg-missing-intent");
        await scenario.StartMatchingAsync(trip, rider);
        var result = await scenario.ReserveAsync(trip, 1, [first.DriverId, second.DriverId]);
        Assert.Equal(RoundOutcome.NoDriver, result.Outcome);

        await ExecuteAsync(db.MatchingConnectionString,
            "DELETE FROM outbox_messages WHERE message_id = @id;", ("id", result.ResultMessageId));

        var run = await RunAuditAsync(db, "negative-missing-intent", "NEGATIVE_CONTROL_SEEDED");
        var check = Check(run, "AUD-06");
        Assert.Equal(AuditStatus.FAIL, check.Status);
        Assert.Contains(check.Findings, f => f.Code == "MISSING_OR_MISMATCHED_FAILURE_INTENT");
        RecordControl("NEG-06B", "AUD-06",
            "NO_DRIVER terminal result identity with its MatchingFailedV1 Outbox intent deleted.",
            "AUD-06 FAIL for missing durable terminal intent.",
            $"{check.Status} / {string.Join(",", check.Findings.Select(f => f.Code).Distinct())}",
            "isolated matching database (neg_missing_intent)",
            "The real terminalizer commits state and intent atomically; deletion simulates storage corruption outside the code path.");
    }

    [Fact]
    public async Task Negative_LeakedReservationAfterFalseCleanup_FailsAud07()
    {
        var db = await _fixture.CreateDatabasesAsync("neg_leak");
        await using var scenario = await AuditScenario.CreateAsync(db);
        var driver = await scenario.SeedAvailableDriverAsync();
        var rider = Guid.NewGuid();
        var trip = await scenario.CreateTripAsync(rider, "audit-neg-leak");
        await scenario.StartMatchingAsync(trip, rider);
        var reserved = await scenario.ReserveAsync(trip, 1, [driver.DriverId]);
        await scenario.RecordReservationAsync(trip, driver.DriverId, reserved.ReservationId!.Value, 1);
        await scenario.CancelAsync(trip, rider);

        // Simulate a falsely confirmed cleanup: obligations are marked done
        // while the authoritative Driver row is still reserved.
        await ExecuteAsync(db.TripConnectionString!,
            "UPDATE trip_cleanup_intents SET completed_at = now() WHERE trip_id = @trip;",
            ("trip", trip));

        var run = await RunAuditAsync(db, "negative-false-cleanup", "NEGATIVE_CONTROL_SEEDED");
        var check = Check(run, "AUD-07");
        Assert.Equal(AuditStatus.FAIL, check.Status);
        Assert.Contains(check.Findings, f => f.Code == "LEAKED_RESERVATION");
        RecordControl("NEG-07A", "AUD-07",
            "Cancelled Trip, cleanup intents marked complete, Driver row still RESERVED.",
            "AUD-07 FAIL: a sent/confirmed cleanup is not proof the reservation was released.",
            $"{check.Status} / {string.Join(",", check.Findings.Select(f => f.Code).Distinct())}",
            "isolated Trip + Driver databases (neg_leak)",
            "Directly exercises the mission rule that release completion must be read from the Driver authority.");
    }

    [Fact]
    public async Task Negative_StuckCleanupObligation_FailsAud07()
    {
        var db = await _fixture.CreateDatabasesAsync("neg_stuck_cleanup");
        await using var scenario = await AuditScenario.CreateAsync(db);
        var driver = await scenario.SeedAvailableDriverAsync();
        var rider = Guid.NewGuid();
        var trip = await scenario.CreateTripAsync(rider, "audit-neg-stuck-cleanup");
        await scenario.StartMatchingAsync(trip, rider);
        var reserved = await scenario.ReserveAsync(trip, 1, [driver.DriverId]);
        await scenario.RecordReservationAsync(trip, driver.DriverId, reserved.ReservationId!.Value, 1);
        await scenario.CancelAsync(trip, rider);

        await ExecuteAsync(db.TripConnectionString!, """
            UPDATE trip_cleanup_intents
            SET created_at = now() - interval '2 hours'
            WHERE trip_id = @trip AND completed_at IS NULL;
            """, ("trip", trip));

        var run = await RunAuditAsync(db, "negative-stuck-cleanup", "NEGATIVE_CONTROL_SEEDED");
        var check = Check(run, "AUD-07");
        Assert.Equal(AuditStatus.FAIL, check.Status);
        Assert.Contains(check.Findings, f => f.Code == "STUCK_CLEANUP_OBLIGATION");
        RecordControl("NEG-07B", "AUD-07",
            "Recognized release obligation aged 2 h while the Driver stayed RESERVED.",
            "AUD-07 FAIL beyond the accepted cleanup policy.",
            $"{check.Status} / {string.Join(",", check.Findings.Select(f => f.Code).Distinct())}",
            "isolated Trip + Driver databases (neg_stuck_cleanup)",
            "Distinguishes recognized-pending cleanup from an unbounded leak.");
    }

    [Fact]
    public async Task Negative_DriverTripDisagreement_FailsAud04()
    {
        var db = await _fixture.CreateDatabasesAsync("neg_disagreement");
        await using var scenario = await AuditScenario.CreateAsync(db);
        var driver = await scenario.SeedAvailableDriverAsync();
        var rider = Guid.NewGuid();
        var trip = await scenario.CreateTripAsync(rider, "audit-neg-disagreement");
        await scenario.StartMatchingAsync(trip, rider);
        var reserved = await scenario.ReserveAsync(trip, 1, [driver.DriverId]);
        await scenario.RecordReservationAsync(trip, driver.DriverId, reserved.ReservationId!.Value, 1);

        var forgedReservation = Guid.NewGuid();
        await ExecuteAsync(db.TripConnectionString!, """
            UPDATE trips SET reservation_id = @reservation WHERE trip_id = @trip;
            """, ("reservation", forgedReservation), ("trip", trip));

        var run = await RunAuditAsync(db, "negative-disagreement", "NEGATIVE_CONTROL_SEEDED");
        var check = Check(run, "AUD-04");
        Assert.Equal(AuditStatus.FAIL, check.Status);
        Assert.Contains(check.Findings, f => f.Code == "DRIVER_TRIP_DISAGREEMENT");
        RecordControl("NEG-04A", "AUD-04",
            "Trip assignment reservationId changed while the Driver kept the original reservation.",
            "AUD-04 FAIL for exact identity disagreement.",
            $"{check.Status} / {string.Join(",", check.Findings.Select(f => f.Code).Distinct())}",
            "isolated Trip + Driver databases (neg_disagreement)",
            "Trip row remains internally coherent; only the cross-service comparison can catch it.");
    }

    [Fact]
    public async Task Negative_StuckUnresolvedRecords_FailsAud08()
    {
        var db = await _fixture.CreateDatabasesAsync("neg_stuck");
        await ExecuteAsync(db.DriverConnectionString, """
            INSERT INTO outbox_messages
                (message_id, message_type, schema_version, correlation_id, payload_json,
                 exchange, routing_key, occurred_at, status, attempt_count, next_attempt_at)
            VALUES ('stuck-driver-intent', 'DriverReservedV1', 1, 'neg-corr', '{}'::jsonb,
                    'ride.events', 'driver.stuck', now() - interval '2 hours', 'PENDING', 0,
                    now() - interval '2 hours');
            """);
        var trip = Guid.NewGuid();
        await ExecuteAsync(db.MatchingConnectionString, """
            INSERT INTO matching_rounds
                (trip_id, matching_round, input_message_id, status, created_at, updated_at)
            VALUES (@trip, 1, 'neg-stuck-input', 'PROCESSING',
                    now() - interval '2 hours', now() - interval '2 hours');
            INSERT INTO matching_attempts
                (trip_id, matching_round, ordinal, driver_id, reservation_id, state,
                 created_at, updated_at)
            VALUES (@trip, 1, 0, @driver, @reservation, 'UNKNOWN',
                    now() - interval '2 hours', now() - interval '2 hours');
            """, ("trip", trip), ("driver", Guid.NewGuid()), ("reservation", Guid.NewGuid()));

        var run = await RunAuditAsync(db, "negative-stuck-unresolved", "NEGATIVE_CONTROL_SEEDED");
        var check = Check(run, "AUD-08");
        Assert.Equal(AuditStatus.FAIL, check.Status);
        Assert.Contains(check.Findings, f => f.Code == "STUCK_PENDING_OUTBOX");
        Assert.Contains(check.Findings, f => f.Code == "STUCK_UNRESOLVED_ATTEMPT");
        Assert.Contains(check.Findings, f => f.Code == "STUCK_PROCESSING_ROUND");
        RecordControl("NEG-08A", "AUD-08",
            "2 h-old PENDING Outbox row, UNKNOWN attempt and PROCESSING round.",
            "AUD-08 FAIL for every stale unresolved category.",
            $"{check.Status} / {string.Join(",", check.Findings.Select(f => f.Code).Distinct())}",
            "isolated Driver + Matching databases (neg_stuck)",
            "Age policy: 15 min for pending outbox, unresolved attempts and PROCESSING rounds.");
    }

    [Fact]
    public async Task Negative_FreshCleanupUnderQuiescence_Fails()
    {
        var db = await _fixture.CreateDatabasesAsync("neg_fresh_cleanup");
        await using var scenario = await AuditScenario.CreateAsync(db);
        var driver = await scenario.SeedAvailableDriverAsync();
        var rider = Guid.NewGuid();
        var trip = await scenario.CreateTripAsync(rider, "audit-neg-fresh-cleanup");
        await scenario.StartMatchingAsync(trip, rider);
        var reserved = await scenario.ReserveAsync(trip, 1, [driver.DriverId]);
        await scenario.RecordReservationAsync(trip, driver.DriverId, reserved.ReservationId!.Value, 1);
        await scenario.CancelAsync(trip, rider);

        // The release obligation is fresh but still open while the Driver is
        // reserved; a business-quiesced observation must not accept that.
        var run = await RunAuditAsync(db, "negative-fresh-cleanup-quiesced",
            "POST_SCENARIO_CLEANUP_QUIESCED_NO_RELAY");
        var aud04 = Check(run, "AUD-04");
        var aud07 = Check(run, "AUD-07");
        Assert.Equal(AuditStatus.FAIL, aud04.Status);
        Assert.Equal(AuditStatus.FAIL, aud07.Status);
        Assert.Contains(aud04.Findings, f => f.Code == "UNRESOLVED_CLEANUP_AT_QUIESCENCE");
        Assert.Contains(aud07.Findings, f => f.Code == "UNRESOLVED_CLEANUP_AT_QUIESCENCE");
        RecordControl("NEG-07C", "AUD-04/AUD-07",
            "Fresh open release obligation and a still-RESERVED Driver at business quiescence.",
            "AUD-04 and AUD-07 FAIL; recognized pending is only acceptable in mid-flight observation.",
            $"AUD-04={aud04.Status}, AUD-07={aud07.Status} / UNRESOLVED_CLEANUP_AT_QUIESCENCE",
            "isolated Trip + Driver databases (neg_fresh_cleanup)",
            "Proves the observation mode is enforced, not merely recorded.");
    }

    [Fact]
    public async Task Negative_FreshPendingUnderRelayQuiescence_Fails()
    {
        var db = await _fixture.CreateDatabasesAsync("neg_relay_pending");
        await ExecuteAsync(db.DriverConnectionString, """
            INSERT INTO outbox_messages
                (message_id, message_type, schema_version, correlation_id, payload_json,
                 exchange, routing_key, occurred_at, status, attempt_count, next_attempt_at)
            VALUES ('fresh-relay-pending', 'DriverReservedV1', 1, 'neg-corr', '{}'::jsonb,
                    'ride.events', 'driver.test', now(), 'PENDING', 0, now());
            """);

        var run = await RunAuditAsync(db, "negative-fresh-pending-relay-quiesced",
            "POST_SCENARIO_RELAY_QUIESCED");
        var check = Check(run, "AUD-08");
        Assert.Equal(AuditStatus.FAIL, check.Status);
        Assert.Contains(check.Findings, f => f.Code == "PENDING_OUTBOX_AT_QUIESCENCE");
        RecordControl("NEG-08B", "AUD-08",
            "Fresh PENDING Outbox row in a relay-quiesced observation.",
            "AUD-08 FAIL: quiesced relay state requires pending = 0.",
            $"{check.Status} / PENDING_OUTBOX_AT_QUIESCENCE",
            "isolated Driver database (neg_relay_pending)",
            "Complements the age-policy control: no-relay modes accept fresh pending within policy, relay-quiesced modes do not.");
    }

    // ------------------------------------------------------------ controls
    [Fact]
    public async Task Control_DriverCoherenceAndIdentity_AreStorageEnforced()
    {
        var db = await _fixture.CreateDatabasesAsync("control_constraints");
        var coherence = await CapturePostgresErrorAsync(db.DriverConnectionString, """
            INSERT INTO drivers (driver_id, user_id, status, version, reserved_trip_id, reservation_id)
            VALUES (@driver, @user, 'AVAILABLE', 1, @trip, @reservation);
            """, ("driver", Guid.NewGuid()), ("user", Guid.NewGuid()),
            ("trip", Guid.NewGuid()), ("reservation", Guid.NewGuid()));
        Assert.Equal("23514", coherence.SqlState);
        Assert.Equal("chk_drivers_coherence", coherence.ConstraintName);
        RecordControl("CTRL-02A", "AUD-02",
            "Direct SQL: AVAILABLE with reservation references.",
            "PostgreSQL rejects the incoherent row.",
            $"ENFORCED_BY_CONSTRAINT ({coherence.SqlState} {coherence.ConstraintName})",
            "isolated Driver database (control_constraints)",
            "Constraint enforcement demonstrated instead of bypassing the guard to fabricate a detection.");

        var sharedReservation = Guid.NewGuid();
        var duplicate = await CapturePostgresErrorAsync(db.DriverConnectionString, """
            INSERT INTO drivers (driver_id, user_id, status, version, reserved_trip_id, reservation_id)
            VALUES (@d1, @u1, 'RESERVED', 2, @t1, @r), (@d2, @u2, 'RESERVED', 2, @t2, @r);
            """, ("d1", Guid.NewGuid()), ("u1", Guid.NewGuid()), ("t1", Guid.NewGuid()),
            ("d2", Guid.NewGuid()), ("u2", Guid.NewGuid()), ("t2", Guid.NewGuid()),
            ("r", sharedReservation));
        Assert.Equal("23505", duplicate.SqlState);
        Assert.Equal("ux_drivers_reservation_id", duplicate.ConstraintName);
        RecordControl("CTRL-01A", "AUD-01",
            "Direct SQL: the same reservationId current on two Drivers.",
            "PostgreSQL rejects the duplicate reservation identity.",
            $"ENFORCED_BY_CONSTRAINT ({duplicate.SqlState} {duplicate.ConstraintName})",
            "isolated Driver database (control_constraints)",
            "Cross-driver reservation uniqueness is storage-enforced.");
    }

    [Fact]
    public async Task Control_MatchingTerminalIdentity_IsStorageEnforced()
    {
        var db = await _fixture.CreateDatabasesAsync("control_matching_constraints");
        var sharedResult = Guid.NewGuid();
        var duplicate = await CapturePostgresErrorAsync(db.MatchingConnectionString, """
            INSERT INTO matching_rounds
                (trip_id, matching_round, input_message_id, status, result_message_id)
            VALUES (@t1, 1, 'ctrl-a', 'NO_DRIVER', @result),
                   (@t2, 1, 'ctrl-b', 'NO_DRIVER', @result);
            """, ("t1", Guid.NewGuid()), ("t2", Guid.NewGuid()), ("result", sharedResult));
        Assert.Equal("23505", duplicate.SqlState);
        Assert.Equal("ux_matching_rounds_result_message_id", duplicate.ConstraintName);
        RecordControl("CTRL-06A", "AUD-06",
            "Direct SQL: one result identity attached to two NO_DRIVER rounds.",
            "PostgreSQL rejects the duplicate terminal identity.",
            $"ENFORCED_BY_CONSTRAINT ({duplicate.SqlState} {duplicate.ConstraintName})",
            "isolated Matching database (control_matching_constraints)",
            "One logical result identity is storage-enforced.");

        var missingIdentity = await CapturePostgresErrorAsync(db.MatchingConnectionString, """
            INSERT INTO matching_rounds (trip_id, matching_round, input_message_id, status)
            VALUES (@trip, 1, 'ctrl-c', 'NO_DRIVER');
            """, ("trip", Guid.NewGuid()));
        Assert.Equal("23514", missingIdentity.SqlState);
        Assert.Equal("chk_matching_rounds_no_driver_result", missingIdentity.ConstraintName);
        RecordControl("CTRL-06B", "AUD-06",
            "Direct SQL: NO_DRIVER without a persisted result identity.",
            "PostgreSQL rejects the identity-less terminal.",
            $"ENFORCED_BY_CONSTRAINT ({missingIdentity.SqlState} {missingIdentity.ConstraintName})",
            "isolated Matching database (control_matching_constraints)",
            "New NO_DRIVER terminals cannot omit their durable result identity.");
    }

    [Fact]
    public async Task Control_EmptyDataset_IsNotRunNotPass()
    {
        var db = await _fixture.CreateDatabasesAsync("control_empty");
        var run = await RunAuditAsync(db, "control-empty-dataset", "EMPTY_DATASET_CONTROL");
        var statuses = string.Join(", ", run.Checks.Select(c => $"{c.CheckId}={c.Status}"));
        Assert.DoesNotContain(run.Checks, c => c.Status == AuditStatus.PASS);
        Assert.All(run.Checks, c => Assert.True(
            c.Status is AuditStatus.NOT_RUN or AuditStatus.BLOCKED, statuses));
        RecordControl("CTRL-EMPTY", "AUD-01..08",
            "Fresh schemas with zero rows in every authority table.",
            "No PASS from an empty dataset.",
            statuses,
            "isolated empty databases (control_empty)",
            "Demonstrates non-vacuity: an empty dataset yields NOT RUN, never a green result.");
    }

    [Fact]
    public async Task Control_UnreachableTripAuthority_IsBlockedNotPass()
    {
        var db = await _fixture.CreateDriverOnlyAsync("control_unreachable");
        var unreachable = "Host=127.0.0.1;Port=1;Database=missing;Username=none;Password=none;Timeout=2";
        var auditor = new InvariantAuditor(db.DriverConnectionString, unreachable, unreachable);
        var run = await auditor.RunAsync("control-unreachable-trip", "NEGATIVE_CONTROL_SEEDED");
        _fixture.Report.AddRun(run);

        var aud04 = Check(run, "AUD-04");
        var aud07 = Check(run, "AUD-07");
        Assert.Equal(AuditStatus.BLOCKED, aud04.Status);
        Assert.Equal(AuditStatus.BLOCKED, aud07.Status);
        RecordControl("CTRL-UNREACHABLE", "AUD-04/AUD-07",
            "Trip/Matching connection strings point to an unreachable server.",
            "BLOCKED, never PASS.",
            $"AUD-04={aud04.Status}, AUD-07={aud07.Status}",
            "driver-only database + unreachable authorities",
            "An unreachable authority cannot produce a business verdict.");
    }

    // ------------------------------------------------------------- helpers
    private async Task<AuditRun> RunAuditAsync(AuditDatabaseSet db, string scenarioId, string mode = "POST_SCENARIO_CLEANUP_QUIESCED_NO_RELAY")
    {
        var auditor = new InvariantAuditor(
            db.DriverConnectionString, db.MatchingConnectionString, db.TripConnectionString);
        var run = await auditor.RunAsync(scenarioId, mode);
        _fixture.Report.AddRun(run);
        return run;
    }

    private static AuditCheckResult Check(AuditRun run, string checkId) =>
        run.Checks.Single(c => c.CheckId == checkId);

    private void RecordControl(
        string id, string target, string seeded, string expected, string observed,
        string isolation, string notes) =>
        _fixture.Report.AddNegativeControl(new NegativeControlRecord(
            id, target, seeded, expected, observed, isolation, notes));

    private static async Task ExecuteAsync(
        string connectionString, string sql, params (string Name, object? Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        await command.ExecuteNonQueryAsync();
    }

    private static async Task<PostgresException> CapturePostgresErrorAsync(
        string connectionString, string sql, params (string Name, object? Value)[] parameters)
    {
        try
        {
            await ExecuteAsync(connectionString, sql, parameters);
        }
        catch (PostgresException ex)
        {
            return ex;
        }

        throw new Xunit.Sdk.XunitException("Expected PostgreSQL to reject the seeded row, but it committed.");
    }
}
