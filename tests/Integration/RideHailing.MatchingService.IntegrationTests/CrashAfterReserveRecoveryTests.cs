using Npgsql;
using RideHailing.DriverService.Domain.Drivers;
using RideHailing.DriverService.Infrastructure.Persistence;
using RideHailing.MatchingService.Application.Messaging;
using RideHailing.MatchingService.Application.Reserve;
using RideHailing.MatchingService.Infrastructure.DriverClient;
using RideHailing.MatchingService.Infrastructure.Persistence;

namespace RideHailing.MatchingService.IntegrationTests;

[Collection(ReserveWorkflowCollection.Name)]
public sealed class CrashAfterReserveRecoveryTests(MatchingPostgresDatabase matching, DriverTestDatabase drivers)
{
    // Controlled exception at the remote-commit/local-persist boundary. This
    // is a real-DB recovery base, NOT a process-kill or HTTP transport proof.
    private sealed class InterruptedAfterCommit : Exception;

    private sealed class AuthorityClient(DriverRepository repository, bool interrupt = false) : IDriverClient
    {
        public readonly List<DriverReserveRequest> Reservations = [];
        public readonly List<Guid> Reads = [];
        public int Queries;
        public async Task<DriverVersionRead?> TryGetDriverVersionAsync(Guid id, CancellationToken ct = default)
        {
            Reads.Add(id);
            var driver = await repository.LoadAsync(id, ct);
            return driver is null ? null : new(driver.Version, DriverRepository.ToDb(driver.Status));
        }
        public async Task<DriverReserveResult> ReserveAsync(DriverReserveRequest request, CancellationToken ct = default)
        {
            Reservations.Add(request);
            var result = await repository.TryReserveAsync(request.DriverId, request.TripId,
                request.ReservationId, request.ExpectedVersion, request.MatchingRound, ct);
            Assert.True(result.Outcome is ReserveOutcome.Won or ReserveOutcome.Replayed);
            if (interrupt) throw new InterruptedAfterCommit();
            return new(DriverReserveOutcomeKind.Reserved, null, result.Driver!.Version);
        }
        public async Task<ReservationReconcileResult> GetReservationAsync(Guid driverId, Guid reservationId, CancellationToken ct = default)
        {
            Queries++;
            var truth = await repository.QueryReservationAsync(driverId, reservationId, ct);
            return new(Enum.Parse<ReservationReconcileOutcome>(truth.Outcome.ToString()), truth.Version);
        }
    }

    [Fact]
    public async Task PersistedInflightAttempt_RecoversSameReservationAfterRemoteCommit_WithoutFalseNoDriver()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = deadline.Token;
        var trip = Guid.NewGuid();
        var driver = Driver.Create(Guid.NewGuid(), Guid.NewGuid());
        driver.GoOnline(driver.Version);
        var next = Driver.Create(Guid.NewGuid(), Guid.NewGuid());
        next.GoOnline(next.Version);
        var nextDriver = next.DriverId;
        Assert.True(await drivers.CreateRepository().TryInsertAsync(next, ct));
        Assert.True(await drivers.CreateRepository().TryInsertAsync(driver, ct));
        var firstClient = new AuthorityClient(drivers.CreateRepository(), interrupt: true);
        var firstAttempts = new MatchingAttemptRepository(matching.ConnectionString);
        var terminalizer = new MatchingTerminalizer(matching.DataSource, matching.OutboxStore);
        var first = new MatchingReserveOrchestrator(firstAttempts, firstClient, terminalizer);
        await Assert.ThrowsAsync<InterruptedAfterCommit>(() => first.ExecuteRoundAsync(
            trip, 1, [driver.DriverId, nextDriver], "rel02:" + trip, ct));

        // Both sides are re-read from PostgreSQL; no fixture invents IN_FLIGHT.
        var persisted = Assert.Single(await firstAttempts.LoadAttemptsAsync(trip, 1, ct));
        Assert.Equal(MatchingAttemptStates.InFlight, persisted.State);
        Assert.Equal(Assert.Single(firstClient.Reservations).ReservationId, persisted.ReservationId);
        var committed = (await drivers.CreateRepository().LoadAsync(driver.DriverId, ct))!;
        Assert.Equal(DriverStatus.Reserved, committed.Status);
        Assert.Equal(persisted.ReservationId, committed.ReservationId);
        Assert.Equal(trip, committed.ReservedTripId);
        Assert.DoesNotContain(nextDriver, firstClient.Reads);
        var refused = await terminalizer.TryTerminalizeNoDriverAsync(trip, 1, ct);
        Assert.Equal(MatchingTerminalOutcome.Refused, refused.Outcome);
        Assert.Equal(MatchingTerminalReasons.UnresolvedAttempt, refused.Reason);
        Assert.Null(await matching.OutboxStore.LoadAsync(MatchingResultMessageIds.ForNoDriver(trip, 1), ct));

        // Recreate repositories, pools and orchestrator, then recover ONLY by
        // persisted identity. A possibly-dispatched IN_FLIGHT attempt is
        // reconciled with the authoritative same-R query BEFORE any replay;
        // only a proven non-commit would then advance (never a new attempt).
        await using var restartedPool = NpgsqlDataSource.Create(drivers.ConnectionString);
        var restartedDriver = new DriverRepository(restartedPool,
            new RideHailing.Messaging.Outbox.NpgsqlOutboxStore(restartedPool));
        var recoveredClient = new AuthorityClient(restartedDriver);
        var restartedAttempts = new MatchingAttemptRepository(matching.ConnectionString);
        var restarted = new MatchingReserveOrchestrator(restartedAttempts, recoveredClient,
            new MatchingTerminalizer(matching.DataSource, matching.OutboxStore));
        var result = await restarted.ReconcileAsync(trip, 1, persisted.Ordinal, ct);
        Assert.Equal(AttemptOutcome.Reserved, result.Outcome);
        Assert.Equal(persisted.ReservationId, result.ReservationId);
        Assert.Empty(recoveredClient.Reservations);
        Assert.True(recoveredClient.Queries > 0, "recovery must query the committed identity");
        var finalAttempt = Assert.Single(await restartedAttempts.LoadAttemptsAsync(trip, 1, ct));
        Assert.Equal(persisted.Ordinal, finalAttempt.Ordinal);
        Assert.Equal(MatchingAttemptStates.Reserved, finalAttempt.State);
        var round = (await restartedAttempts.LoadRoundAsync(trip, 1, ct))!;
        Assert.Equal(MatchingRoundStatuses.Reserved, round.Status);
        Assert.Equal(persisted.ReservationId, round.SelectedReservationId);
        Assert.Equal(driver.DriverId, round.SelectedDriverId);
        var finalDriver = (await restartedDriver.LoadAsync(driver.DriverId, ct))!;
        Assert.Equal(committed.Version, finalDriver.Version);
        Assert.Equal(committed.ReservationId, finalDriver.ReservationId);
        var untouched = (await restartedDriver.LoadAsync(nextDriver, ct))!;
        Assert.Equal(DriverStatus.Available, untouched.Status);
        Assert.Equal(next.Version, untouched.Version);
        await using (var connection = await restartedPool.OpenConnectionAsync(ct))
        await using (var count = new NpgsqlCommand(
            "SELECT count(*) FROM outbox_messages WHERE aggregate_id = @driver AND message_type = 'DriverReservedV1'", connection))
        {
            count.Parameters.AddWithValue("driver", driver.DriverId.ToString("N"));
            Assert.Equal(1L, await count.ExecuteScalarAsync(ct));
        }
        Assert.DoesNotContain(nextDriver, recoveredClient.Reads);
        Assert.Equal(MatchingTerminalOutcome.Reserved,
            (await terminalizer.TryTerminalizeNoDriverAsync(trip, 1, ct)).Outcome);
        var replay = await restarted.ExecuteRoundAsync(trip, 1, [driver.DriverId, nextDriver], "rel02:" + trip, ct);
        Assert.Equal(RoundOutcome.Reserved, replay.Outcome);
        Assert.Empty(recoveredClient.Reservations);
        Assert.Null(await matching.OutboxStore.LoadAsync(MatchingResultMessageIds.ForNoDriver(trip, 1), ct));
    }
}
