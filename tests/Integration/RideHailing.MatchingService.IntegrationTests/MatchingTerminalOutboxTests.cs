// NO_DRIVER terminal + durable MatchingFailedV1 Outbox proof
// on REAL PostgreSQL. Every terminal write is one transaction: the round row
// and its publication intent commit together or not at all. The guards below
// are the durable oracle — UNKNOWN/IN_FLIGHT/READY/RESERVED, cancellation,
// incomplete snapshot coverage, a failing Outbox insert and conflicting
// intent content must never leave a terminal without its intent.
//
// Concurrency is scheduled with PostgreSQL row locks (never sleeps): the
// same round-row lock discipline used by attempt creation and dispatch
// authorization is exercised from real blocking transactions, observed via
// pg_locks, so the interleaving is deterministic.
using System.Text.Json;
using Npgsql;
using RideHailing.MatchingService.Application.Messaging;
using RideHailing.MatchingService.Application.Reserve;
using RideHailing.MatchingService.Infrastructure.DriverClient;
using RideHailing.MatchingService.Infrastructure.Persistence;
using RideHailing.Messaging.Contracts;
using RideHailing.Messaging.Outbox;

namespace RideHailing.MatchingService.IntegrationTests;

[Collection(MatchingPostgresCollection.Name)]
public sealed class MatchingTerminalOutboxTests
{
    private readonly MatchingPostgresDatabase _matching;

    public MatchingTerminalOutboxTests(MatchingPostgresDatabase matching)
    {
        _matching = matching;
    }

    private MatchingAttemptRepository Attempts() => new(_matching.ConnectionString);

    private MatchingTerminalizer Terminalizer() =>
        new(_matching.DataSource, _matching.OutboxStore);

    private static Guid NewTrip() => Guid.NewGuid();

    private sealed class RejectingDriverClient : IDriverClient
    {
        public int VersionCalls;
        public int ReserveCalls;

        public Task<DriverVersionRead?> TryGetDriverVersionAsync(
            Guid driverId, CancellationToken ct = default)
        {
            Interlocked.Increment(ref VersionCalls);
            return Task.FromResult<DriverVersionRead?>(
                new DriverVersionRead(3, "AVAILABLE"));
        }

        public Task<DriverReserveResult> ReserveAsync(
            DriverReserveRequest request, CancellationToken ct = default)
        {
            Interlocked.Increment(ref ReserveCalls);
            return Task.FromResult(new DriverReserveResult(
                DriverReserveOutcomeKind.Rejected, "DRIVER_NOT_AVAILABLE", null));
        }

        public Task<ReservationReconcileResult> GetReservationAsync(
            Guid driverId, Guid reservationId, CancellationToken ct = default) =>
            throw new InvalidOperationException("Reconcile is not part of this proof.");
    }

    private sealed class SilentDriverClient : IDriverClient
    {
        public Task<DriverVersionRead?> TryGetDriverVersionAsync(
            Guid driverId, CancellationToken ct = default) =>
            throw new InvalidOperationException("terminal replay must not call Driver");

        public Task<DriverReserveResult> ReserveAsync(
            DriverReserveRequest request, CancellationToken ct = default) =>
            throw new InvalidOperationException("terminal replay must not call Driver");

        public Task<ReservationReconcileResult> GetReservationAsync(
            Guid driverId, Guid reservationId, CancellationToken ct = default) =>
            throw new InvalidOperationException("terminal replay must not call Driver");
    }

    private MatchingReserveOrchestrator Orchestrator(IDriverClient driver) =>
        new(Attempts(), driver, Terminalizer());

    private async Task<string> SeedRoundAsync(Guid trip, int round = 1)
    {
        var inputMessageId = Guid.NewGuid().ToString("D");
        await Attempts().EnsureRoundAsync(trip, round, inputMessageId);
        return inputMessageId;
    }

    private async Task<MatchingAttemptRow> SeedAttemptAsync(
        Guid trip, int round, int ordinal, string finalState, Guid? driverId = null)
    {
        var repo = Attempts();
        var ensured = await repo.EnsureInflightAttemptAsync(
            trip, round, ordinal, driverId ?? Guid.NewGuid(), Guid.NewGuid());
        Assert.NotNull(ensured.Row);
        if (finalState != MatchingAttemptStates.InFlight)
        {
            var error = finalState == MatchingAttemptStates.Rejected ? "TEST_REJECTED" : null;
            Assert.True(await repo.TryTransitionAsync(
                trip, round, ordinal,
                [MatchingAttemptStates.InFlight], finalState, error));
        }

        return (await repo.LoadAttemptAsync(trip, round, ordinal))!;
    }

    private async Task<long> OutboxCountAsync(string messageId)
    {
        await using var conn = new NpgsqlConnection(_matching.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM outbox_messages WHERE message_id = @id", conn);
        cmd.Parameters.AddWithValue("id", messageId);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection, string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var cmd = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Waits until at least <paramref name="expected"/> backends of this test
    /// are blocked on a lock while running a statement that mentions
    /// <paramref name="queryFragment"/>.
    ///
    /// A row-lock waiter does NOT surface as a granted=false tuple lock: while
    /// the holder is in progress PostgreSQL queues the waiter on the holder's
    /// transaction id, so pg_locks has locktype='transactionid' and a NULL
    /// relation. The blocking statement (visible in pg_stat_activity.query) is
    /// still the one that named the relation. Application Name is unique per
    /// test, so stale waiters from another test can never satisfy the count.
    /// This makes the race schedule observable without time-based sleeps.
    /// </summary>
    private async Task WaitForLockWaitersAsync(
        string applicationName, string queryFragment, int expected)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            while (true)
            {
                await using var conn = new NpgsqlConnection(_matching.ConnectionString);
                await conn.OpenAsync(timeout.Token);
                await using var cmd = new NpgsqlCommand("""
                    SELECT count(*) FROM pg_stat_activity
                    WHERE application_name = @application
                      AND wait_event_type = 'Lock'
                      AND strpos(query, @fragment) > 0;
                    """, conn);
                cmd.Parameters.AddWithValue("application", applicationName);
                cmd.Parameters.AddWithValue("fragment", queryFragment);
                var waiting = (long)(await cmd.ExecuteScalarAsync(timeout.Token))!;
                if (waiting >= expected)
                {
                    return;
                }

                await Task.Delay(20, timeout.Token);
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Timed out waiting for {expected} '{applicationName}' backend(s) " +
                $"blocked while running a statement containing '{queryFragment}'.");
        }
    }

    /// <summary>
    /// A per-test Application Name lets the lock-waiter probe count only this
    /// test's backends, even if a previously failed test left a waiter behind.
    /// </summary>
    private static string WithApplicationName(string connectionString, string applicationName) =>
        new NpgsqlConnectionStringBuilder(connectionString)
        {
            ApplicationName = applicationName,
        }.ConnectionString;

    [Fact]
    public async Task UnknownAttempt_BlocksTerminalization_WithoutIntent()
    {
        var trip = NewTrip();
        await SeedRoundAsync(trip);
        await SeedAttemptAsync(trip, 1, 0, MatchingAttemptStates.Unknown);

        var result = await Terminalizer().TryTerminalizeNoDriverAsync(trip, 1);

        Assert.Equal(MatchingTerminalOutcome.Refused, result.Outcome);
        Assert.Equal(MatchingTerminalReasons.UnresolvedAttempt, result.Reason);
        var round = await Attempts().LoadRoundAsync(trip, 1);
        Assert.Equal(MatchingRoundStatuses.Processing, round!.Status);
        Assert.Null(round.ResultMessageId);
        Assert.Null(await _matching.OutboxStore.LoadAsync(
            MatchingResultMessageIds.ForNoDriver(trip, 1)));
    }

    [Fact]
    public async Task InFlightAttempt_BlocksTerminalization_WithoutIntent()
    {
        var trip = NewTrip();
        await SeedRoundAsync(trip);
        await SeedAttemptAsync(trip, 1, 0, MatchingAttemptStates.InFlight);

        var result = await Terminalizer().TryTerminalizeNoDriverAsync(trip, 1);

        Assert.Equal(MatchingTerminalOutcome.Refused, result.Outcome);
        Assert.Equal(MatchingTerminalReasons.UnresolvedAttempt, result.Reason);
        Assert.Equal(
            MatchingRoundStatuses.Processing,
            (await Attempts().LoadRoundAsync(trip, 1))!.Status);
        Assert.Null(await _matching.OutboxStore.LoadAsync(
            MatchingResultMessageIds.ForNoDriver(trip, 1)));
    }

    [Fact]
    public async Task ReservedAttempt_BlocksNoDriver_AndReportsDurableWinner()
    {
        var trip = NewTrip();
        await SeedRoundAsync(trip);
        var reserved = await SeedAttemptAsync(trip, 1, 0, MatchingAttemptStates.Reserved);
        await SeedAttemptAsync(trip, 1, 1, MatchingAttemptStates.Rejected);

        var result = await Terminalizer().TryTerminalizeNoDriverAsync(trip, 1);

        Assert.Equal(MatchingTerminalOutcome.Reserved, result.Outcome);
        Assert.Equal(reserved.DriverId, result.DriverId);
        Assert.Equal(reserved.ReservationId, result.ReservationId);
        var round = await Attempts().LoadRoundAsync(trip, 1);
        Assert.Equal(MatchingRoundStatuses.Processing, round!.Status);
        Assert.Null(round.ResultMessageId);
        Assert.Null(await _matching.OutboxStore.LoadAsync(
            MatchingResultMessageIds.ForNoDriver(trip, 1)));
    }

    [Fact]
    public async Task CancellationRequested_BlocksTerminalization_WithoutIntent()
    {
        var trip = NewTrip();
        await SeedRoundAsync(trip);
        await SeedAttemptAsync(trip, 1, 0, MatchingAttemptStates.Rejected);

        await using (var conn = new NpgsqlConnection(_matching.ConnectionString))
        {
            await conn.OpenAsync();
            await ExecuteAsync(conn,
                "UPDATE matching_rounds SET cancel_requested = TRUE " +
                "WHERE trip_id = @trip AND matching_round = 1",
                ("trip", trip));
        }

        var result = await Terminalizer().TryTerminalizeNoDriverAsync(trip, 1);

        Assert.Equal(MatchingTerminalOutcome.Refused, result.Outcome);
        Assert.Equal(MatchingTerminalReasons.CancelRequested, result.Reason);
        Assert.Equal(
            MatchingRoundStatuses.Processing,
            (await Attempts().LoadRoundAsync(trip, 1))!.Status);
        Assert.Null(await _matching.OutboxStore.LoadAsync(
            MatchingResultMessageIds.ForNoDriver(trip, 1)));
    }

    [Fact]
    public async Task IncompleteSnapshotCoverage_RefusesNoDriver()
    {
        var trip = NewTrip();
        await SeedRoundAsync(trip);
        var first = await SeedAttemptAsync(trip, 1, 0, MatchingAttemptStates.Rejected);
        var missingDriver = Guid.NewGuid();
        var snapshot = JsonSerializer.Serialize(new object[]
        {
            new { ordinal = 0, driverId = first.DriverId },
            new { ordinal = 1, driverId = missingDriver },
        });

        await using (var conn = new NpgsqlConnection(_matching.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "UPDATE matching_rounds SET candidate_snapshot_json = CAST(@json AS jsonb) " +
                "WHERE trip_id = @trip AND matching_round = 1", conn);
            cmd.Parameters.AddWithValue("json", snapshot);
            cmd.Parameters.AddWithValue("trip", trip);
            await cmd.ExecuteNonQueryAsync();
        }

        var result = await Terminalizer().TryTerminalizeNoDriverAsync(trip, 1);

        Assert.Equal(MatchingTerminalOutcome.Refused, result.Outcome);
        Assert.Equal(MatchingTerminalReasons.SnapshotIncomplete, result.Reason);
        Assert.Equal(
            MatchingRoundStatuses.Processing,
            (await Attempts().LoadRoundAsync(trip, 1))!.Status);
        Assert.Null(await _matching.OutboxStore.LoadAsync(
            MatchingResultMessageIds.ForNoDriver(trip, 1)));
    }

    [Fact]
    public async Task OutboxInsertFailure_RollsBackNoDriverState()
    {
        var trip = NewTrip();
        var inputMessageId = await SeedRoundAsync(trip);
        await SeedAttemptAsync(trip, 1, 0, MatchingAttemptStates.Rejected);

        await using var conn = new NpgsqlConnection(_matching.ConnectionString);
        await conn.OpenAsync();
        // Fault injection: this one intent cannot be inserted. The failure is
        // deliberately raised after the round UPDATE, proving the state change
        // is rolled back with the intent.
        var functionName = "matching_test_fail_outbox_" + trip.ToString("N");
        await ExecuteAsync(conn, $"""
            CREATE OR REPLACE FUNCTION {functionName}() RETURNS trigger
            LANGUAGE plpgsql AS $$
            BEGIN
              IF NEW.correlation_id = '{inputMessageId}' THEN
                RAISE EXCEPTION 'injected outbox insert failure';
              END IF;
              RETURN NEW;
            END;
            $$;
            """);
        await ExecuteAsync(conn, $"""
            CREATE TRIGGER matching_test_fail_outbox
            BEFORE INSERT ON outbox_messages
            FOR EACH ROW EXECUTE FUNCTION {functionName}();
            """);

        try
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(
                () => Terminalizer().TryTerminalizeNoDriverAsync(trip, 1));
            Assert.Equal("P0001", ex.SqlState);
        }
        finally
        {
            await ExecuteAsync(conn, "DROP TRIGGER IF EXISTS matching_test_fail_outbox ON outbox_messages");
            await ExecuteAsync(conn, $"DROP FUNCTION IF EXISTS {functionName}()");
        }

        var round = await Attempts().LoadRoundAsync(trip, 1);
        Assert.Equal(MatchingRoundStatuses.Processing, round!.Status);
        Assert.Null(round.ResultMessageId);
        Assert.Equal(MatchingAttemptStates.Rejected,
            Assert.Single(await Attempts().LoadAttemptsAsync(trip, 1)).State);
        Assert.Null(await _matching.OutboxStore.LoadAsync(
            MatchingResultMessageIds.ForNoDriver(trip, 1)));
    }

    [Fact]
    public async Task ConflictingIntentContent_FailsLoud_AndRollsBackTerminalState()
    {
        var trip = NewTrip();
        await SeedRoundAsync(trip);
        await SeedAttemptAsync(trip, 1, 0, MatchingAttemptStates.Rejected);
        var identity = MatchingResultMessageIds.ForNoDriver(trip, 1);

        var conflictingEnvelope = MessageEnvelope<JsonElement>.Create(
            "SomeOtherEventV1",
            correlationId: Guid.NewGuid().ToString("D"),
            payload: JsonSerializer.SerializeToElement(new { tripId = trip }),
            messageId: identity);
        var conflicting = OutboxMessage.Create(
            conflictingEnvelope, MatchingEventRoutes.EventsExchange, "some.other.route");
        await using (var conn = await _matching.DataSource.OpenConnectionAsync())
        await using (var tx = await conn.BeginTransactionAsync())
        {
            Assert.True(await _matching.OutboxStore.EnqueueAsync(conn, tx, conflicting));
            await tx.CommitAsync();
        }

        try
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => Terminalizer().TryTerminalizeNoDriverAsync(trip, 1));
            Assert.Contains("different content", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            await using var conn = new NpgsqlConnection(_matching.ConnectionString);
            await conn.OpenAsync();
            await ExecuteAsync(conn,
                "DELETE FROM outbox_messages WHERE message_id = @id", ("id", identity));
        }

        var round = await Attempts().LoadRoundAsync(trip, 1);
        Assert.Equal(MatchingRoundStatuses.Processing, round!.Status);
        Assert.Null(round.ResultMessageId);
    }

    [Theory]
    [InlineData("aggregate")]
    [InlineData("version")]
    [InlineData("saga")]
    [InlineData("operation")]
    public async Task MatchingIntent_ConflictingSemanticMetadata_RollsBackTerminal(string field)
    {
        var trip = NewTrip();
        var input = await SeedRoundAsync(trip);
        await SeedAttemptAsync(trip, 1, 0, MatchingAttemptStates.Rejected);
        var identity = MatchingResultMessageIds.ForNoDriver(trip, 1);
        var envelope = MessageEnvelope<MatchingFailedPayload>.Create(
            MatchingEventTypes.MatchingFailed, input, new(trip, 1), messageId: identity,
            aggregateType: field is "aggregate" or "version" ? "trip" : null,
            aggregateId: field is "aggregate" or "version" ? trip.ToString() : null,
            aggregateVersion: field == "version" ? 1 : null,
            sagaId: field == "saga" ? "other-saga" : null,
            operationKey: field == "operation" ? "other-operation" : null);
        await using (var conn = await _matching.DataSource.OpenConnectionAsync())
        await using (var tx = await conn.BeginTransactionAsync())
        {
            Assert.True(await _matching.OutboxStore.EnqueueAsync(conn, tx,
                OutboxMessage.Create(envelope, MatchingEventRoutes.EventsExchange, MatchingEventRoutes.MatchingFailed)));
            await tx.CommitAsync();
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => Terminalizer().TryTerminalizeNoDriverAsync(trip, 1));
        var round = (await Attempts().LoadRoundAsync(trip, 1))!;
        Assert.Equal(MatchingRoundStatuses.Processing, round.Status);
        Assert.Null(round.ResultMessageId);
    }

    [Fact]
    public async Task ConcurrentTerminalizers_ShareOneIdentityAndOneIntent()
    {
        var trip = NewTrip();
        await SeedRoundAsync(trip);
        await SeedAttemptAsync(trip, 1, 0, MatchingAttemptStates.Rejected);
        await SeedAttemptAsync(trip, 1, 1, MatchingAttemptStates.Rejected);

        using var barrier = new Barrier(2);
        var runs = await Task.WhenAll(
            Task.Run(async () =>
            {
                barrier.SignalAndWait(TimeSpan.FromSeconds(60));
                return await Terminalizer().TryTerminalizeNoDriverAsync(trip, 1);
            }),
            Task.Run(async () =>
            {
                barrier.SignalAndWait(TimeSpan.FromSeconds(60));
                return await Terminalizer().TryTerminalizeNoDriverAsync(trip, 1);
            }));

        var expected = MatchingResultMessageIds.ForNoDriver(trip, 1);
        Assert.All(runs, r =>
        {
            Assert.Equal(MatchingTerminalOutcome.NoDriver, r.Outcome);
            Assert.Equal(expected, r.ResultMessageId);
        });
        Assert.Equal(1, await OutboxCountAsync(expected));
        Assert.Equal(2, (await Attempts().LoadAttemptsAsync(trip, 1)).Count);
        var round = await Attempts().LoadRoundAsync(trip, 1);
        Assert.Equal(MatchingRoundStatuses.NoDriver, round!.Status);
        Assert.Equal(Guid.Parse(expected), round.ResultMessageId);
    }

    [Fact]
    public async Task DuplicateRequest_ReusesIdentityAndSingleIntent()
    {
        var trip = NewTrip();
        var driver = new RejectingDriverClient();
        var orchestrator = Orchestrator(driver);

        var first = await orchestrator.ExecuteRoundAsync(
            trip, 1, [Guid.NewGuid(), Guid.NewGuid()], Guid.NewGuid().ToString("D"));
        var second = await orchestrator.ExecuteRoundAsync(
            trip, 1, [Guid.NewGuid(), Guid.NewGuid()], Guid.NewGuid().ToString("D"));

        Assert.Equal(RoundOutcome.NoDriver, first.Outcome);
        Assert.Equal(RoundOutcome.NoDriver, second.Outcome);
        Assert.Equal(first.ResultMessageId, second.ResultMessageId);
        Assert.Equal(MatchingResultMessageIds.ForNoDriver(trip, 1), first.ResultMessageId);
        Assert.Equal(2, driver.ReserveCalls);
        Assert.Equal(1, await OutboxCountAsync(first.ResultMessageId!));
        Assert.Equal(2, (await Attempts().LoadAttemptsAsync(trip, 1)).Count);
    }

    [Fact]
    public async Task Restart_ReplaysTerminalWithoutNewEffects()
    {
        var trip = NewTrip();
        var first = await Orchestrator(new RejectingDriverClient()).ExecuteRoundAsync(
            trip, 1, [Guid.NewGuid()], Guid.NewGuid().ToString("D"));
        Assert.Equal(RoundOutcome.NoDriver, first.Outcome);

        // Fresh repository/terminalizer/orchestrator instances simulate a
        // process restart: durable state is the only input.
        var silent = new SilentDriverClient();
        var restarted = Orchestrator(silent);
        var replay = await restarted.ExecuteRoundAsync(
            trip, 1, [Guid.NewGuid()], Guid.NewGuid().ToString("D"));

        Assert.Equal(RoundOutcome.NoDriver, replay.Outcome);
        Assert.Equal(first.ResultMessageId, replay.ResultMessageId);
        Assert.Equal(1, await OutboxCountAsync(first.ResultMessageId!));
        Assert.Single(await Attempts().LoadAttemptsAsync(trip, 1));
    }

    [Fact]
    public async Task ExactReplay_RepairsMissingIntentUnderPersistedIdentity()
    {
        var trip = NewTrip();
        await SeedRoundAsync(trip);
        await SeedAttemptAsync(trip, 1, 0, MatchingAttemptStates.Rejected);
        var first = await Terminalizer().TryTerminalizeNoDriverAsync(trip, 1);
        Assert.Equal(MatchingTerminalOutcome.NoDriver, first.Outcome);
        var identity = first.ResultMessageId!;

        await using (var conn = new NpgsqlConnection(_matching.ConnectionString))
        {
            await conn.OpenAsync();
            await ExecuteAsync(conn,
                "DELETE FROM outbox_messages WHERE message_id = @id", ("id", identity));
        }

        var replay = await Terminalizer().TryTerminalizeNoDriverAsync(trip, 1);

        Assert.Equal(MatchingTerminalOutcome.NoDriver, replay.Outcome);
        Assert.Equal(identity, replay.ResultMessageId);
        Assert.Single(await Attempts().LoadAttemptsAsync(trip, 1));
        var round = await Attempts().LoadRoundAsync(trip, 1);
        Assert.Equal(MatchingRoundStatuses.NoDriver, round!.Status);
        Assert.Equal(Guid.Parse(identity), round.ResultMessageId);
        var repaired = await _matching.OutboxStore.LoadAsync(identity);
        Assert.NotNull(repaired);
        Assert.Equal(MatchingEventTypes.MatchingFailed, repaired!.MessageType);
        Assert.Equal(MatchingEventRoutes.MatchingFailed, repaired.RoutingKey);
    }

    [Fact]
    public async Task ConcurrentDispatchCommit_MakesTerminalizationRefuse()
    {
        // A dispatch/attempt resume takes FOR SHARE on the round and updates
        // the attempt. Here that transaction is in progress while the
        // terminalizer asks for FOR UPDATE: it must wait, then observe the
        // committed IN_FLIGHT row and refuse. NO_DRIVER can never be based on
        // the pre-dispatch attempt set.
        var trip = NewTrip();
        await SeedRoundAsync(trip);
        var attempt = await SeedAttemptAsync(trip, 1, 0, MatchingAttemptStates.Rejected);

        var applicationName = $"matching-race-{Guid.NewGuid():N}";
        await using var terminalizerSource = NpgsqlDataSource.Create(
            WithApplicationName(_matching.ConnectionString, applicationName));
        var terminalizer = new MatchingTerminalizer(
            terminalizerSource, new NpgsqlOutboxStore(terminalizerSource));

        await using var dispatch = new NpgsqlConnection(_matching.ConnectionString);
        await dispatch.OpenAsync();
        await using (var tx = await dispatch.BeginTransactionAsync())
        {
            await using (var share = new NpgsqlCommand("""
                SELECT 1 FROM matching_rounds
                WHERE trip_id = @trip AND matching_round = 1
                  AND status = 'PROCESSING' AND cancel_requested = FALSE
                FOR SHARE;
                """, dispatch, tx))
            {
                share.Parameters.AddWithValue("trip", trip);
                Assert.NotNull(await share.ExecuteScalarAsync());
            }

            await using (var fence = new NpgsqlCommand("""
                UPDATE matching_attempts
                SET state = 'IN_FLIGHT', max_dispatched_version = 7, updated_at = now()
                WHERE trip_id = @trip AND matching_round = 1 AND ordinal = 0;
                """, dispatch, tx))
            {
                fence.Parameters.AddWithValue("trip", trip);
                Assert.Equal(1, await fence.ExecuteNonQueryAsync());
            }

            var terminalizing = Task.Run(
                () => terminalizer.TryTerminalizeNoDriverAsync(trip, 1));
            await WaitForLockWaitersAsync(applicationName, "matching_rounds", 1);

            // The dispatch commits first. The terminalizer resumes with a
            // fresh statement snapshot and sees the unresolved attempt.
            await tx.CommitAsync();
            var result = await terminalizing;
            Assert.Equal(MatchingTerminalOutcome.Refused, result.Outcome);
            Assert.Equal(MatchingTerminalReasons.UnresolvedAttempt, result.Reason);
        }

        var round = await Attempts().LoadRoundAsync(trip, 1);
        Assert.Equal(MatchingRoundStatuses.Processing, round!.Status);
        Assert.Null(round.ResultMessageId);
        Assert.Equal(attempt.DriverId,
            (await Attempts().LoadAttemptAsync(trip, 1, 0))!.DriverId);
        Assert.Null(await _matching.OutboxStore.LoadAsync(
            MatchingResultMessageIds.ForNoDriver(trip, 1)));
    }

    [Fact]
    public async Task TerminalCommit_DeniesNewAttemptCreationAndDispatch()
    {
        var trip = NewTrip();
        await SeedRoundAsync(trip);
        await SeedAttemptAsync(trip, 1, 0, MatchingAttemptStates.Rejected);
        var terminal = await Terminalizer().TryTerminalizeNoDriverAsync(trip, 1);
        Assert.Equal(MatchingTerminalOutcome.NoDriver, terminal.Outcome);

        var created = await Attempts().EnsureInflightAttemptAsync(
            trip, 1, 1, Guid.NewGuid(), Guid.NewGuid());
        Assert.False(created.Inserted);
        Assert.Null(created.Row);

        Assert.False(await Attempts().TryRecordDispatchAsync(trip, 1, 0, 9));

        var computeRepo = new MatchingRepository(_matching.ConnectionString);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            computeRepo.InsertAttemptAsync(trip, 1, 2, Guid.NewGuid(), Guid.NewGuid()));

        Assert.Single(await Attempts().LoadAttemptsAsync(trip, 1));
        Assert.Equal(1, await OutboxCountAsync(terminal.ResultMessageId!));
    }

    [Fact]
    public async Task TerminalizationQueuedAheadOfNewWork_DeniesAttemptCreation()
    {
        // Deterministic order: the terminalizer takes the round row FOR
        // UPDATE and then blocks on the Outbox table (locked ACCESS EXCLUSIVE
        // by the test). Only after that round lock is provably held does a
        // new attempt creator start, so it queues on the terminalizer's
        // transaction id. Releasing the Outbox block lets the terminalizer
        // commit NO_DRIVER first; the creator then re-evaluates the round
        // predicate and never inserts.
        var trip = NewTrip();
        await SeedRoundAsync(trip);
        await SeedAttemptAsync(trip, 1, 0, MatchingAttemptStates.Rejected);

        var applicationName = $"matching-race-{Guid.NewGuid():N}";
        await using var terminalizerSource = NpgsqlDataSource.Create(
            WithApplicationName(_matching.ConnectionString, applicationName));
        var terminalizer = new MatchingTerminalizer(
            terminalizerSource, new NpgsqlOutboxStore(terminalizerSource));
        var creator = new MatchingAttemptRepository(
            WithApplicationName(_matching.ConnectionString, applicationName));

        await using var holder = new NpgsqlConnection(_matching.ConnectionString);
        await holder.OpenAsync();
        await using var tx = await holder.BeginTransactionAsync();
        await using (var outbox = new NpgsqlCommand(
            "LOCK TABLE outbox_messages IN ACCESS EXCLUSIVE MODE;", holder, tx))
        {
            await outbox.ExecuteNonQueryAsync();
        }

        var terminalizing = Task.Run(
            () => terminalizer.TryTerminalizeNoDriverAsync(trip, 1));
        // The terminalizer can only reach the Outbox after it holds the round
        // row FOR UPDATE: the terminal round update precedes the intent write.
        await WaitForLockWaitersAsync(applicationName, "outbox_messages", 1);

        var creating = Task.Run(() => creator.EnsureInflightAttemptAsync(
            trip, 1, 1, Guid.NewGuid(), Guid.NewGuid()));
        await WaitForLockWaitersAsync(applicationName, "matching_rounds", 1);

        await tx.CommitAsync();

        var terminal = await terminalizing;
        var created = await creating;
        Assert.Equal(MatchingTerminalOutcome.NoDriver, terminal.Outcome);
        Assert.False(created.Inserted);
        Assert.Null(created.Row);
        Assert.Single(await Attempts().LoadAttemptsAsync(trip, 1));
        Assert.Equal(1, await OutboxCountAsync(terminal.ResultMessageId!));
    }
}
