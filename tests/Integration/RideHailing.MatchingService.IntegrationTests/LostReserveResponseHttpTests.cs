// The lost Reserve response is exercised across a REAL
// Kestrel/HTTP boundary, not a transport stub. The Driver endpoints, repository
// and Outbox commit are the production implementations; Matching itself runs
// its production orchestrator and typed HTTP client.
//
// Controlled sequence:
//   write-ahead IN_FLIGHT -> real POST reserve -> Driver commit + Outbox
//   -> Matching post-commit barrier -> Matching timeout -> UNKNOWN (no advance)
//   -> release -> reconcile the SAME reservationId -> RESERVED.
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using RideHailing.DriverService.Api;
using RideHailing.DriverService.Application.Location;
using RideHailing.DriverService.Application.Messaging;
using RideHailing.DriverService.Domain.Drivers;
using RideHailing.DriverService.Infrastructure.Faults;
using RideHailing.DriverService.Infrastructure.Persistence;
using RideHailing.MatchingService.Application.Messaging;
using RideHailing.MatchingService.Application.Reserve;
using RideHailing.MatchingService.Infrastructure.DriverClient;
using RideHailing.MatchingService.Infrastructure.Persistence;
using RideHailing.Messaging.Outbox;
using Xunit.Abstractions;

namespace RideHailing.MatchingService.IntegrationTests;

[Collection(ReserveWorkflowCollection.Name)]
public sealed class LostReserveResponseHttpTests
{
    private const string FaultName = DriverFaultPointNames.AfterReserveCommitBeforeResponse;

    private readonly MatchingPostgresDatabase _matching;
    private readonly DriverTestDatabase _drivers;
    private readonly ITestOutputHelper _output;

    public LostReserveResponseHttpTests(
        MatchingPostgresDatabase matching,
        DriverTestDatabase drivers,
        ITestOutputHelper output)
    {
        _matching = matching;
        _drivers = drivers;
        _output = output;
    }

    [Fact]
    public async Task LostReserveResponse_KeepsUnknownThenReconcilesSameReservationOverRealHttp()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = deadline.Token;
        var repository = _drivers.CreateRepository();
        var first = await SeedAvailableDriverAsync(repository);
        var second = await SeedAvailableDriverAsync(repository);
        var faults = DriverFaultPoint.ForTesting(
            [FaultName], TimeSpan.FromSeconds(30));

        await using var driverHost = BuildDriverHost(faults);
        await driverHost.StartAsync(ct);
        var address = driverHost.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        _output.WriteLine($"Driver HTTP authority: {address}");

        // Separate budgets: the reserve call must outlive a slow DB/HTTP start
        // yet time out well before the 30s fault hold; reconciliation uses a
        // wider budget and is never expected to be held.
        using var reserveHttp = new HttpClient
        {
            BaseAddress = new Uri(address + "/"),
            Timeout = TimeSpan.FromSeconds(5),
        };
        using var reconcileHttp = new HttpClient
        {
            BaseAddress = new Uri(address + "/"),
            Timeout = TimeSpan.FromSeconds(15),
        };
        var attempts = new MatchingAttemptRepository(_matching.ConnectionString);
        var terminalizer = new MatchingTerminalizer(
            _matching.DataSource, _matching.OutboxStore);
        var orchestrator = new MatchingReserveOrchestrator(
            attempts, new HttpDriverClient(reserveHttp), terminalizer);
        var reconciler = new MatchingReserveOrchestrator(
            attempts, new HttpDriverClient(reconcileHttp), terminalizer);
        var tripId = Guid.NewGuid();

        var roundTask = orchestrator.ExecuteRoundAsync(
            tripId, 1, [first.DriverId, second.DriverId],
            $"rel01-http:{tripId:N}", ct);

        // Barrier: the Driver has committed and is holding the response.
        await faults.WaitForReachedAsync(TimeSpan.FromSeconds(10), ct);
        var committed = (await repository.LoadAsync(first.DriverId, ct))!;
        Assert.Equal(DriverStatus.Reserved, committed.Status);
        Assert.Equal(tripId, committed.ReservedTripId);
        Assert.NotNull(committed.ReservationId);
        Assert.Equal(1, committed.ReservationRound);
        var reservationId = committed.ReservationId!.Value;

        var intermediateAttempt = Assert.Single(
            await attempts.LoadAttemptsAsync(tripId, 1, ct));
        Assert.Equal(first.DriverId, intermediateAttempt.DriverId);
        Assert.Equal(reservationId, intermediateAttempt.ReservationId);
        Assert.True(
            intermediateAttempt.State is MatchingAttemptStates.InFlight or MatchingAttemptStates.Unknown,
            $"attempt state was {intermediateAttempt.State}");
        Assert.NotNull(intermediateAttempt.MaxDispatchedVersion);
        var intermediateRound = (await attempts.LoadRoundAsync(tripId, 1, ct))!;
        Assert.Equal(MatchingRoundStatuses.Processing, intermediateRound.Status);
        Assert.Null(intermediateRound.ResultMessageId);

        var outboxAfterCommit = await ReadReservedOutboxAsync(first.DriverId, ct);
        Assert.Equal(1, outboxAfterCommit.Count);

        // The real HTTP call times out while the response is held.
        var roundResult = await roundTask.WaitAsync(TimeSpan.FromSeconds(10), ct);
        Assert.Equal(RoundOutcome.BlockedOnUnknown, roundResult.Outcome);

        var unknown = Assert.Single(await attempts.LoadAttemptsAsync(tripId, 1, ct));
        Assert.Equal(MatchingAttemptStates.Unknown, unknown.State);
        Assert.Equal("RESERVE_TRANSPORT", unknown.LastErrorCode);
        Assert.Equal(reservationId, unknown.ReservationId);
        Assert.Equal(intermediateAttempt.Ordinal, unknown.Ordinal);
        Assert.Equal(intermediateAttempt.MaxDispatchedVersion, unknown.MaxDispatchedVersion);
        Assert.Equal(MatchingRoundStatuses.Processing,
            (await attempts.LoadRoundAsync(tripId, 1, ct))!.Status);

        // No advance, no false terminal, no duplicate Driver effect.
        var untouchedSecond = (await repository.LoadAsync(second.DriverId, ct))!;
        Assert.Equal(DriverStatus.Available, untouchedSecond.Status);
        Assert.Equal(second.Version, untouchedSecond.Version);
        Assert.Null(await _matching.OutboxStore.LoadAsync(
            MatchingResultMessageIds.ForNoDriver(tripId, 1), ct));
        var outboxWhileUnknown = await ReadReservedOutboxAsync(first.DriverId, ct);
        Assert.Equal(outboxAfterCommit, outboxWhileUnknown);

        Assert.True(faults.Release(FaultName));

        var reconciled = await reconciler.ReconcileAsync(
            tripId, 1, unknown.Ordinal, ct);
        Assert.Equal(AttemptOutcome.Reserved, reconciled.Outcome);
        Assert.Equal(reservationId, reconciled.ReservationId);

        // Reconcile replay is idempotent: no new row, version or Outbox effect.
        var replay = await reconciler.ReconcileAsync(
            tripId, 1, unknown.Ordinal, ct);
        Assert.Equal(AttemptOutcome.Reserved, replay.Outcome);
        Assert.Equal(reservationId, replay.ReservationId);

        var finalAttempts = await attempts.LoadAttemptsAsync(tripId, 1, ct);
        var finalAttempt = Assert.Single(finalAttempts);
        Assert.Equal(unknown.Ordinal, finalAttempt.Ordinal);
        Assert.Equal(MatchingAttemptStates.Reserved, finalAttempt.State);
        Assert.Equal(reservationId, finalAttempt.ReservationId);
        Assert.Equal(first.DriverId, finalAttempt.DriverId);

        var finalRound = (await attempts.LoadRoundAsync(tripId, 1, ct))!;
        Assert.Equal(MatchingRoundStatuses.Reserved, finalRound.Status);
        Assert.Equal(first.DriverId, finalRound.SelectedDriverId);
        Assert.Equal(reservationId, finalRound.SelectedReservationId);
        Assert.Null(finalRound.ResultMessageId);

        var finalFirst = (await repository.LoadAsync(first.DriverId, ct))!;
        Assert.Equal(DriverStatus.Reserved, finalFirst.Status);
        Assert.Equal(reservationId, finalFirst.ReservationId);
        Assert.Equal(tripId, finalFirst.ReservedTripId);
        Assert.Equal(committed.Version, finalFirst.Version);
        Assert.Equal(finalFirst.UpdatedAt, committed.UpdatedAt);

        var finalSecond = (await repository.LoadAsync(second.DriverId, ct))!;
        Assert.Equal(DriverStatus.Available, finalSecond.Status);
        Assert.Equal(second.Version, finalSecond.Version);

        var finalOutbox = await ReadReservedOutboxAsync(first.DriverId, ct);
        Assert.Equal(1, finalOutbox.Count);
        Assert.Equal(reservationId, Guid.Parse(finalOutbox.ReservationId!));
        Assert.Equal("1", finalOutbox.MatchingRound);
        Assert.Null(await _matching.OutboxStore.LoadAsync(
            MatchingResultMessageIds.ForNoDriver(tripId, 1), ct));

        var allReservations = await ReadReservationCountAsync(first.DriverId, ct);
        Assert.Equal(1L, allReservations);

        _output.WriteLine("AUTHORITY_PROOF " + JsonSerializer.Serialize(new
        {
            scenario = "REL-01",
            tripId,
            driverId = first.DriverId,
            otherDriverId = second.DriverId,
            reservationId,
            postCommitReservationId = committed.ReservationId,
            uncertainState = unknown.State,
            uncertainReservationId = unknown.ReservationId,
            attemptCount = finalAttempts.Count,
            finalAttemptState = finalAttempt.State,
            finalRoundStatus = finalRound.Status,
            finalRoundReservationId = finalRound.SelectedReservationId,
            finalDriverStatus = finalFirst.Status.ToString(),
            finalDriverReservationId = finalFirst.ReservationId,
            otherDriverStatus = finalSecond.Status.ToString(),
            reserveOutboxCount = finalOutbox.Count,
            authoritativeReservationCount = allReservations,
        }));

        await driverHost.StopAsync(ct);
    }

    private async Task<Driver> SeedAvailableDriverAsync(DriverRepository repository)
    {
        var driver = Driver.Create(Guid.NewGuid(), Guid.NewGuid());
        driver.GoOnline(driver.Version);
        Assert.True(await repository.TryInsertAsync(driver));
        return driver;
    }

    private WebApplication BuildDriverHost(DriverFaultPoint faults)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var outbox = new NpgsqlOutboxStore(_drivers.DataSource);
        builder.Services.AddSingleton(_drivers.DataSource);
        builder.Services.AddSingleton(outbox);
        builder.Services.AddSingleton(new DriverRepository(_drivers.DataSource, outbox));
        builder.Services.AddSingleton(faults);

        // Full endpoint map inference requires every mapped dependency to be
        // resolvable. Location is never called here; the publisher opens no
        // connection until used.
        builder.Services.AddOptions<LocationIngressOptions>();
        builder.Services.AddOptions<DriverBrokerOptions>();
        builder.Services.AddSingleton<LocationAcceptor>();
        builder.Services.AddSingleton<DriverEventPublisher>();

        var app = builder.Build();
        DriverEndpoints.Map(app);
        return app;
    }

    private async Task<(long Count, string? ReservationId, string? MatchingRound)>
        ReadReservedOutboxAsync(Guid driverId, CancellationToken ct)
    {
        await using var connection = await _drivers.DataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("""
            SELECT count(*),
                   min(payload_json->>'reservationId'),
                   min(payload_json->>'matchingRound')
            FROM outbox_messages
            WHERE message_type = 'DriverReservedV1' AND aggregate_id = @driver;
            """, connection);
        command.Parameters.AddWithValue("driver", driverId.ToString("N"));
        await using var reader = await command.ExecuteReaderAsync(ct);
        Assert.True(await reader.ReadAsync(ct));
        return (
            reader.GetInt64(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2));
    }

    private async Task<long> ReadReservationCountAsync(Guid driverId, CancellationToken ct)
    {
        await using var connection = await _drivers.DataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(
            "SELECT count(DISTINCT reservation_id) FROM drivers WHERE driver_id = @driver;",
            connection);
        command.Parameters.AddWithValue("driver", driverId);
        return (long)(await command.ExecuteScalarAsync(ct))!;
    }
}
