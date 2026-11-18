// Production proofs: a real broker delivery must traverse S0 -> S1 -> S2 with
// a durable, ordered attempt ledger, and terminalize only after the final stage
// is conclusively exhausted.
using System.Text.Json;
using RideHailing.DriverService.Infrastructure.Faults;
using RideHailing.MatchingService.Application.Matching;
using RideHailing.MatchingService.Domain.Matching;
using RideHailing.MatchingService.Infrastructure.Persistence;
using Xunit.Abstractions;

namespace RideHailing.MatchingService.IntegrationTests;

[Collection(ProductionWorkflowCollection.Name)]
public sealed class ProductionMatchingStageTests : ProductionMatchingTestBase
{
    public ProductionMatchingStageTests(
        MatchingPostgresDatabase pg,
        DriverTestDatabase drivers,
        RedisDatabase redis,
        RabbitMqDatabase rabbit,
        ITestOutputHelper output)
        : base(pg, drivers, redis, rabbit, output)
    {
    }

    [Fact]
    public async Task Real_Round_Progresses_S0_S1_S2_With_Stable_Ordered_Identities()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var ct = deadline.Token;
        await PurgeBrokerAsync();
        await AbandonPriorTestWorkAsync();
        var (pickupLat, pickupLon) = NewPickup();

        // Only D3 exists in Driver authority; D1/D2 are projection-only and
        // become conclusive DRIVER_NOT_FOUND rejections in their stages.
        var d1 = Guid.NewGuid();
        var d2 = Guid.NewGuid();
        await SeedProjectionOnlyAsync(d1, pickupLat + 0.003, pickupLon); // ~0.33 km, S0
        await SeedProjectionOnlyAsync(d2, pickupLat + 0.018, pickupLon); // ~2.0 km, S1
        var d3 = await SeedAvailableDriverAsync(pickupLat + 0.036, pickupLon); // ~4.0 km, S2

        await using var driverHost = await DriverAuthorityHost.StartAsync(
            Drivers, DriverFaultPoint.Disabled(), ct);
        await using var matching = await StartMatchingAsync(
            driverHost.BaseUrl,
            radii: new StageRadii(1.0, 3.0, 5.0),
            ct: ct);

        var trip = Guid.NewGuid();
        await PublishRideRequestedAsync(
            trip, 1, "prod-stage-progression", pickupLat, pickupLon);
        await PollUntilAsync(async () =>
            (await LoadRoundAsync(trip, 1))?.Status == "RESERVED",
            "S0->S1->S2 reservation", 90);

        var round = (await LoadRoundAsync(trip, 1))!;
        var attempts = await LoadAttemptsAsync(trip, 1);
        Assert.Equal(3, attempts.Count);
        Assert.Equal([0, 1, 2], attempts.Select(a => a.Ordinal));
        Assert.Equal(["S0", "S1", "S2"], attempts.Select(a => a.Stage));
        Assert.Equal([d1, d2, d3.DriverId], attempts.Select(a => a.DriverId));
        Assert.Equal(
            ["REJECTED", "REJECTED", "RESERVED"],
            attempts.Select(a => a.State));
        Assert.Equal("S2", round.Stage);
        Assert.Equal("S2", round.StageEvaluated);
        Assert.Equal(d3.DriverId, round.SelectedDriverId);
        Assert.Equal(attempts[2].ReservationId, round.SelectedReservationId);
        Assert.Null(await LoadDriverAsync(d1));
        Assert.Null(await LoadDriverAsync(d2));
        Assert.Equal("RESERVED", (await LoadDriverAsync(d3.DriverId))!.Status);

        using var snapshot = JsonDocument.Parse(round.SnapshotJson!);
        var entries = snapshot.RootElement.EnumerateArray().ToList();
        Assert.Equal(3, entries.Count);
        Assert.Equal(
            ["S0", "S1", "S2"],
            entries.Select(e => e.GetProperty("stage").GetString()));
        Assert.Equal(
            attempts.Select(a => a.ReservationId.ToString()),
            entries.Select(e => e.GetProperty("reservationId").GetString()));
        Assert.Equal(
            attempts.Select(a => a.Ordinal),
            entries.Select(e => e.GetProperty("ordinal").GetInt32()));

        Assert.Equal(1L, await DriverReservedOutboxCountAsync(d3.DriverId));
        Assert.Equal(0L, await MatchingFailedCountAsync(trip));

        Output.WriteLine(
            $"Stage progression: trip={trip} ordinals=0,1,2 stages=S0,S1,S2 " +
            $"R={round.SelectedReservationId} winner={d3.DriverId}");
    }

    [Fact]
    public async Task All_Stages_Exhausted_Emit_Exactly_One_NoDriver_Terminal()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var ct = deadline.Token;
        await PurgeBrokerAsync();
        await AbandonPriorTestWorkAsync();
        var (pickupLat, pickupLon) = NewPickup();
        var d1 = Guid.NewGuid();
        var d2 = Guid.NewGuid();
        var d3 = Guid.NewGuid();
        await SeedProjectionOnlyAsync(d1, pickupLat + 0.003, pickupLon);
        await SeedProjectionOnlyAsync(d2, pickupLat + 0.018, pickupLon);
        await SeedProjectionOnlyAsync(d3, pickupLat + 0.036, pickupLon);

        await using var driverHost = await DriverAuthorityHost.StartAsync(
            Drivers, DriverFaultPoint.Disabled(), ct);
        await using var matching = await StartMatchingAsync(
            driverHost.BaseUrl,
            radii: new StageRadii(1.0, 3.0, 5.0),
            ct: ct);

        var trip = Guid.NewGuid();
        var body = await PublishRideRequestedAsync(
            trip, 1, "prod-stage-no-driver", pickupLat, pickupLon);
        await PollUntilAsync(async () =>
            (await LoadRoundAsync(trip, 1))?.Status == "NO_DRIVER",
            "legal NO_DRIVER only after S2", 90);

        var round = (await LoadRoundAsync(trip, 1))!;
        var attempts = await LoadAttemptsAsync(trip, 1);
        Assert.Equal("S2", round.Stage);
        Assert.Equal("S2", round.StageEvaluated);
        Assert.Equal(3, attempts.Count);
        Assert.Equal(["S0", "S1", "S2"], attempts.Select(a => a.Stage));
        Assert.All(attempts, a => Assert.Equal("REJECTED", a.State));
        Assert.Null(round.SelectedDriverId);
        Assert.Null(round.SelectedReservationId);
        Assert.Equal(
            Guid.Parse(RideHailing.MatchingService.Application.Messaging.MatchingResultMessageIds.ForNoDriver(trip, 1)),
            round.ResultMessageId);
        Assert.Equal(1L, await MatchingFailedCountAsync(trip));

        // Redelivery cannot create a second terminal intent.
        await PublishAsync("ride.events", "trip.ride-requested.v1", body);
        await PollUntilAsync(
            async () => await QueueDepthAsync("matching.ride-requested.q") == 0,
            "NO_DRIVER redelivery drained");
        await Task.Delay(500, ct);
        Assert.Equal(1L, await MatchingFailedCountAsync(trip));
        Assert.Equal("NO_DRIVER", (await LoadRoundAsync(trip, 1))!.Status);

        Output.WriteLine(
            $"Stage exhaustion: trip={trip} stages=S0,S1,S2 all REJECTED, one terminal intent");
    }

    [Fact]
    public async Task PersistedStageMissingFromConfiguration_IsVisible_AndNeverTerminal()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var ct = deadline.Token;
        await PurgeBrokerAsync();
        await AbandonPriorTestWorkAsync();
        var (pickupLat, pickupLon) = NewPickup();
        var trip = Guid.NewGuid();
        await using (var conn = new Npgsql.NpgsqlConnection(Pg.ConnectionString))
        {
            await conn.OpenAsync(ct);
            await using var seed = new Npgsql.NpgsqlCommand("""
                INSERT INTO matching_rounds
                    (trip_id, matching_round, input_message_id, status,
                     pickup_longitude, pickup_latitude, auto_execute, current_stage)
                VALUES (@trip, 1, @message, 'PROCESSING', @lon, @lat, TRUE, 'S9');
                """, conn);
            seed.Parameters.AddWithValue("trip", trip);
            seed.Parameters.AddWithValue("message", $"l7-stage:{trip:N}");
            seed.Parameters.AddWithValue("lon", pickupLon);
            seed.Parameters.AddWithValue("lat", pickupLat);
            await seed.ExecuteNonQueryAsync(ct);
        }

        await using var driverHost = await DriverAuthorityHost.StartAsync(
            Drivers, DriverFaultPoint.Disabled(), ct);
        await using var matching = await StartMatchingAsync(driverHost.BaseUrl, ct: ct);

        await PollUntilAsync(async () =>
            (await LoadRoundAsync(trip, 1))?.WorkErrorCode == "STAGE_CONFIGURATION_MISMATCH",
            "durable stage/configuration mismatch signal", 45);

        var round = (await LoadRoundAsync(trip, 1))!;
        Assert.Equal("PROCESSING", round.Status);
        Assert.Equal("S9", round.Stage);
        Assert.Null(round.SelectedDriverId);
        Assert.Null(round.SelectedReservationId);
        Assert.Empty(await LoadAttemptsAsync(trip, 1));
        Assert.Equal(0L, await MatchingFailedCountAsync(trip));

        Output.WriteLine(
            $"L7: trip={trip} persisted stage S9 is not configured; work stays PROCESSING with a visible error code.");
    }
}

// Deterministic stage-gate proofs on REAL PostgreSQL: no stage advance on
// READY/IN_FLIGHT/UNKNOWN/RESERVED, no backward transition, no terminal
// before the final stage is evaluated and covered.
[Collection(MatchingPostgresCollection.Name)]
public sealed class MatchingStageGateTests
{
    private readonly MatchingPostgresDatabase _pg;

    public MatchingStageGateTests(MatchingPostgresDatabase pg)
    {
        _pg = pg;
    }

    private MatchingAttemptRepository Attempts() => new(_pg.ConnectionString);

    private MatchingWorkRepository Work() => new(_pg.ConnectionString);

    private RideHailing.MatchingService.Application.Reserve.MatchingTerminalizer Terminalizer() =>
        new(_pg.DataSource, _pg.OutboxStore);

    private static DateTimeOffset Now => DateTimeOffset.UtcNow;

    private async Task<(Guid Trip, MatchingAttemptRow Attempt)> SeedStageAsync(
        string stage, Guid? driver = null)
    {
        var trip = Guid.NewGuid();
        var d = driver ?? Guid.NewGuid();
        var r = Guid.NewGuid();
        var attempts = Attempts();
        await attempts.EnsureProductionRoundAsync(trip, 1, $"stage-{trip:N}", 106.6, 10.8);
        var ensured = await Work().EnsureStageSnapshotAsync(
            trip, 1, stage,
            [new StageCandidateSeed(d, r, 1.0, 1.0, 1, Now)],
            "v1", Now);
        Assert.Equal(StageEnsureOutcome.Ready, ensured.Outcome);
        var row = Assert.Single(await attempts.LoadAttemptsByStageAsync(trip, 1, stage));
        return (trip, row);
    }

    [Fact]
    public async Task Ready_InFlight_Unknown_And_Reserved_All_Block_Advance()
    {
        var (trip, ready) = await SeedStageAsync("S0");
        var work = Work();
        Assert.Equal(
            StageAdvanceOutcome.StageNotExhausted,
            await work.TryAdvanceStageAsync(trip, 1, "S0", "S1"));

        Assert.True(await Attempts().TryTransitionAsync(
            trip, 1, ready.Ordinal, ["READY"], "IN_FLIGHT", null));
        Assert.Equal(
            StageAdvanceOutcome.BlockedUnresolved,
            await work.TryAdvanceStageAsync(trip, 1, "S0", "S1"));

        Assert.True(await Attempts().TryTransitionAsync(
            trip, 1, ready.Ordinal, ["IN_FLIGHT"], "UNKNOWN", "RESERVE_TRANSPORT"));
        Assert.Equal(
            StageAdvanceOutcome.BlockedUnresolved,
            await work.TryAdvanceStageAsync(trip, 1, "S0", "S1"));

        Assert.True(await Attempts().TryTransitionAsync(
            trip, 1, ready.Ordinal, ["UNKNOWN"], "RESERVED", null));
        Assert.Equal(
            StageAdvanceOutcome.BlockedUnresolved,
            await work.TryAdvanceStageAsync(trip, 1, "S0", "S1"));

        // Only a conclusive REJECTED stage can advance, and it cannot move
        // backward afterward.
        Assert.True(await Attempts().TryTransitionAsync(
            trip, 1, ready.Ordinal, ["RESERVED"], "REJECTED", "RESERVED_BY_OTHER"));
        Assert.Equal(
            StageAdvanceOutcome.Advanced,
            await work.TryAdvanceStageAsync(trip, 1, "S0", "S1"));
        Assert.Equal(
            StageAdvanceOutcome.NotCurrentStage,
            await work.TryAdvanceStageAsync(trip, 1, "S0", "S1"));
        Assert.Equal("S1", (await Attempts().LoadRoundAsync(trip, 1))!.CurrentStage);
    }

    [Fact]
    public async Task Missing_Snapshot_Coverage_Blocks_Advance()
    {
        var (trip, blocking) = await SeedStageAsync("S0");
        await Attempts().TryTransitionAsync(
            trip, 1, blocking.Ordinal, ["READY"], "REJECTED", "DRIVER_NOT_AVAILABLE");

        // Append a second frozen entry without its attempt row: the stage is
        // not exhaustive even though the first attempt is conclusive.
        await using (var conn = new Npgsql.NpgsqlConnection(_pg.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new Npgsql.NpgsqlCommand("""
                UPDATE matching_rounds
                SET candidate_snapshot_json = candidate_snapshot_json::jsonb || @extra::jsonb
                WHERE trip_id = @trip AND matching_round = 1;
                """, conn);
            cmd.Parameters.AddWithValue("trip", trip);
            cmd.Parameters.AddWithValue("extra",
                $"[{{\"ordinal\":99,\"driverId\":\"{Guid.NewGuid():D}\",\"reservationId\":\"{Guid.NewGuid():D}\",\"distanceKm\":1.0,\"score\":1.0,\"version\":1,\"capturedAt\":\"{Now:O}\",\"stage\":\"S0\"}}]");
            await cmd.ExecuteNonQueryAsync();
        }

        var work = Work();
        Assert.Equal(
            StageAdvanceOutcome.StageNotExhausted,
            await work.TryAdvanceStageAsync(trip, 1, "S0", "S1"));
    }

    [Fact]
    public async Task Internal_Compute_Seam_Refuses_Worker_Owned_Round()
    {
        var trip = Guid.NewGuid();
        await Attempts().EnsureProductionRoundAsync(trip, 1, $"seam-{trip:N}", 106.6, 10.8);
        var service = new MatchingRoundService(
            new MatchingRepository(_pg.ConnectionString),
            new CandidateDiscovery(_ => throw new InvalidOperationException("discovery must not run")),
            new MatchingMetrics(),
            TimeProvider.System);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ComputeAsync(
                new ComputeRoundRequest(trip, 1, 106.6, 10.8),
                MatchingPolicy.FrozenW5Baseline()));
        Assert.Contains("worker-owned", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Terminalizer_Requires_Final_Evaluated_Stage_And_Commits_One_Intent()
    {
        var (trip, attempt) = await SeedStageAsync("S0");
        Assert.True(await Attempts().TryTransitionAsync(
            trip, 1, attempt.Ordinal, ["READY"], "REJECTED", "DRIVER_NOT_AVAILABLE"));

        // Not the final configured stage.
        Assert.Equal(
            RideHailing.MatchingService.Application.Reserve.MatchingTerminalOutcome.Refused,
            (await Terminalizer().TryTerminalizeNoDriverAsync(trip, 1, "S1")).Outcome);

        // Final stage name but its discovery has not completed: even though
        // the current attempts are conclusive, no terminal may be invented.
        await using (var conn = new Npgsql.NpgsqlConnection(_pg.ConnectionString))
        {
            await conn.OpenAsync();
            await using var clear = new Npgsql.NpgsqlCommand(
                "UPDATE matching_rounds SET stage_evaluated = NULL WHERE trip_id = @trip AND matching_round = 1", conn);
            clear.Parameters.AddWithValue("trip", trip);
            await clear.ExecuteNonQueryAsync();
        }

        Assert.Equal(
            RideHailing.MatchingService.Application.Reserve.MatchingTerminalReasons.StageNotEvaluated,
            (await Terminalizer().TryTerminalizeNoDriverAsync(trip, 1, "S0")).Reason);

        // Re-evaluate the frozen S0 stage (existing entries are replayed, no
        // new identities), then freeze/evaluate S1 with zero candidates.
        var work = Work();
        Assert.Equal(
            StageEnsureOutcome.Ready,
            (await work.EnsureStageSnapshotAsync(trip, 1, "S0", [], "v1", Now)).Outcome);
        Assert.Equal(
            StageAdvanceOutcome.Advanced,
            await work.TryAdvanceStageAsync(trip, 1, "S0", "S1"));
        var s1 = await work.EnsureStageSnapshotAsync(trip, 1, "S1", [], "v1", Now);
        Assert.Equal(StageEnsureOutcome.Ready, s1.Outcome);
        Assert.Empty(s1.Candidates);

        var terminal = await Terminalizer().TryTerminalizeNoDriverAsync(trip, 1, "S1");
        Assert.Equal(
            RideHailing.MatchingService.Application.Reserve.MatchingTerminalOutcome.NoDriver,
            terminal.Outcome);
        Assert.Equal(
            RideHailing.MatchingService.Application.Messaging.MatchingResultMessageIds.ForNoDriver(trip, 1),
            terminal.ResultMessageId);
        Assert.NotNull(await _pg.OutboxStore.LoadAsync(terminal.ResultMessageId!));

        // Replay is the same logical terminal, not a second result.
        var replay = await Terminalizer().TryTerminalizeNoDriverAsync(trip, 1, "S1");
        Assert.Equal(terminal.ResultMessageId, replay.ResultMessageId);

        var restarted = await Attempts().LoadRoundAsync(trip, 1);
        Assert.Equal("S1", restarted!.CurrentStage);
        Assert.Single(await Attempts().LoadAttemptsByStageAsync(trip, 1, "S0"));
    }

}
