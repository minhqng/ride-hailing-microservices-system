// B-REC-01 production-path proofs: a real RideRequestedV1 broker delivery
// must cause durable progress and an authoritative Driver reservation through
// the real Matching host (consumer + hosted worker), not a testing endpoint.
// These tests fail against the pre-repair code because no execution trigger
// existed: the round stayed PROCESSING with zero attempts forever.
using System.Text.Json;
using RideHailing.DriverService.Infrastructure.Faults;
using Xunit.Abstractions;

namespace RideHailing.MatchingService.IntegrationTests;

[Collection(ProductionWorkflowCollection.Name)]
public sealed class ProductionMatchingWorkflowTests : ProductionMatchingTestBase
{
    public ProductionMatchingWorkflowTests(
        MatchingPostgresDatabase pg,
        DriverTestDatabase drivers,
        RedisDatabase redis,
        RabbitMqDatabase rabbit,
        ITestOutputHelper output)
        : base(pg, drivers, redis, rabbit, output)
    {
    }

    [Fact]
    public async Task RideRequested_Through_Real_Broker_And_Worker_Reserves_Driver()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var ct = deadline.Token;
        await PurgeBrokerAsync();
        await AbandonPriorTestWorkAsync();
        var (pickupLat, pickupLon) = NewPickup();
        var d1 = await SeedAvailableDriverAsync(pickupLat + 0.001, pickupLon);
        await using var driverHost = await DriverAuthorityHost.StartAsync(
            Drivers, DriverFaultPoint.Disabled(), ct);
        await using var matching = await StartMatchingAsync(driverHost.BaseUrl, ct: ct);

        var trip = Guid.NewGuid();
        const string messageId = "prod-normal-01K8MESSAGE";
        var body = await PublishRideRequestedAsync(
            trip, 1, messageId, pickupLat, pickupLon);

        await PollUntilAsync(async () =>
        {
            var round = await LoadRoundAsync(trip, 1);
            return round is { Status: "RESERVED" };
        }, "round RESERVED through the production worker", 60);

        var finalRound = (await LoadRoundAsync(trip, 1))!;
        var attempt = Assert.Single(await LoadAttemptsAsync(trip, 1));
        Assert.Equal("RESERVED", attempt.State);
        Assert.Equal("S0", attempt.Stage);
        Assert.Equal(d1.DriverId, attempt.DriverId);
        Assert.Equal(messageId, finalRound.InputMessageId);
        Assert.True(finalRound.AutoExecute);
        Assert.Equal("S0", finalRound.Stage);
        Assert.Equal("S0", finalRound.StageEvaluated);
        Assert.Equal(pickupLat, finalRound.PickupLatitude!.Value, 4);
        Assert.Equal(pickupLon, finalRound.PickupLongitude!.Value, 4);
        Assert.Equal(attempt.ReservationId, finalRound.SelectedReservationId);
        Assert.Equal(d1.DriverId, finalRound.SelectedDriverId);

        var driver = (await LoadDriverAsync(d1.DriverId))!;
        Assert.Equal("RESERVED", driver.Status);
        Assert.Equal(attempt.ReservationId, driver.ReservationId);
        Assert.Equal(trip, driver.ReservedTripId);
        Assert.Equal(1L, await DriverReservedOutboxCountAsync(d1.DriverId));
        Assert.Equal(1L, await DriverReservationIdentityCountAsync(d1.DriverId));
        Assert.Equal(0L, await MatchingFailedCountAsync(trip));

        // Exact redelivery of the SAME envelope cannot mint another round,
        // attempt identity or Driver effect.
        await PublishAsync("ride.events", "trip.ride-requested.v1", body);
        await PollUntilAsync(
            async () => await QueueDepthAsync("matching.ride-requested.q") == 0,
            "redelivery drained after ACK");
        await Task.Delay(500, ct);
        Assert.Single(await LoadAttemptsAsync(trip, 1));
        Assert.Equal(1L, await DriverReservedOutboxCountAsync(d1.DriverId));

        Output.WriteLine(
            $"B-REC-01 normal: trip={trip} driver={d1.DriverId} " +
            $"reservation={attempt.ReservationId} inputMessageId={messageId} status=RESERVED");
    }

    [Fact]
    public async Task Response_Loss_Recovers_Same_Reservation_And_Never_Tries_Next_Driver()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        var ct = deadline.Token;
        await PurgeBrokerAsync();
        await AbandonPriorTestWorkAsync();
        var (pickupLat, pickupLon) = NewPickup();
        var d1 = await SeedAvailableDriverAsync(pickupLat + 0.001, pickupLon);
        var d2 = await SeedAvailableDriverAsync(pickupLat + 0.002, pickupLon);
        var faults = DriverFaultPoint.ForTesting([FaultAfterReserveCommit], TimeSpan.FromSeconds(120));
        await using var driverHost = await DriverAuthorityHost.StartAsync(Drivers, faults, ct);
        await using var matching = await StartMatchingAsync(
            driverHost.BaseUrl,
            driverTimeoutSeconds: 15,
            reconcileBackoff: TimeSpan.FromSeconds(5),
            ct: ct);

        var trip = Guid.NewGuid();
        await PublishRideRequestedAsync(
            trip, 1, "prod-response-loss", pickupLat, pickupLon);
        await faults.WaitForReachedAsync(TimeSpan.FromSeconds(40), ct);

        var committed = (await LoadDriverAsync(d1.DriverId))!;
        Assert.Equal("RESERVED", committed.Status);
        var reservationId = committed.ReservationId!.Value;

        await PollUntilAsync(async () =>
        {
            var attempts = await LoadAttemptsAsync(trip, 1);
            return attempts.Any(a => a.DriverId == d1.DriverId && a.State == "IN_FLIGHT");
        }, "write-ahead IN_FLIGHT while the response is held");

        var heldAttempts = await LoadAttemptsAsync(trip, 1);
        var held = Assert.Single(heldAttempts, a => a.DriverId == d1.DriverId);
        Assert.Equal(reservationId, held.ReservationId);
        var notDispatched = Assert.Single(heldAttempts, a => a.DriverId == d2.DriverId);
        Assert.Equal("READY", notDispatched.State);
        var untouched = (await LoadDriverAsync(d2.DriverId))!;
        Assert.Equal("AVAILABLE", untouched.Status);

        // The real HTTP call times out: UNKNOWN is durable and no second
        // candidate is attempted while the effect is unresolved.
        await PollUntilAsync(async () =>
        {
            var attempts = await LoadAttemptsAsync(trip, 1);
            return attempts.Any(a => a.DriverId == d1.DriverId && a.State == "UNKNOWN");
        }, "UNKNOWN after the real reserve timeout", 40);

        var unknownAttempts = await LoadAttemptsAsync(trip, 1);
        Assert.Equal("UNKNOWN", Assert.Single(unknownAttempts, a => a.DriverId == d1.DriverId).State);
        Assert.Equal("READY", Assert.Single(unknownAttempts, a => a.DriverId == d2.DriverId).State);
        Assert.Equal("PROCESSING", (await LoadRoundAsync(trip, 1))!.Status);
        Assert.Equal(0L, await MatchingFailedCountAsync(trip));
        Assert.Equal("AVAILABLE", (await LoadDriverAsync(d2.DriverId))!.Status);

        Assert.True(faults.Release(FaultAfterReserveCommit));

        await PollUntilAsync(async () =>
            (await LoadRoundAsync(trip, 1))?.Status == "RESERVED",
            "automatic same-reservation recovery", 30);

        var finalRound = (await LoadRoundAsync(trip, 1))!;
        var finalAttempts = await LoadAttemptsAsync(trip, 1);
        var finalAttempt = Assert.Single(finalAttempts, a => a.DriverId == d1.DriverId);
        Assert.Equal("RESERVED", finalAttempt.State);
        Assert.Equal(reservationId, finalAttempt.ReservationId);
        Assert.Equal("READY", Assert.Single(finalAttempts, a => a.DriverId == d2.DriverId).State);
        Assert.Equal(reservationId, finalRound.SelectedReservationId);
        Assert.Equal(d1.DriverId, finalRound.SelectedDriverId);
        Assert.Equal("AVAILABLE", (await LoadDriverAsync(d2.DriverId))!.Status);
        Assert.Equal(1L, await DriverReservedOutboxCountAsync(d1.DriverId));
        Assert.Equal(0L, await MatchingFailedCountAsync(trip));

        Output.WriteLine(
            $"B-REC-01 response-loss: trip={trip} R={reservationId} " +
            "D2 untouched, same-R automatic reconcile");
    }

    [Fact]
    public async Task Restart_Recovers_Acked_Work_Without_Any_Manual_Trigger()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(150));
        var ct = deadline.Token;
        await PurgeBrokerAsync();
        await AbandonPriorTestWorkAsync();
        var (pickupLat, pickupLon) = NewPickup();
        var d1 = await SeedAvailableDriverAsync(pickupLat + 0.001, pickupLon);
        var d2 = await SeedAvailableDriverAsync(pickupLat + 0.002, pickupLon);
        var faults = DriverFaultPoint.ForTesting([FaultAfterReserveCommit], TimeSpan.FromSeconds(120));

        await using var driverHost = await DriverAuthorityHost.StartAsync(Drivers, faults, ct);
        var first = await StartMatchingAsync(
            driverHost.BaseUrl, driverTimeoutSeconds: 60, ct: ct);
        var trip = Guid.NewGuid();
        try
        {
            await PublishRideRequestedAsync(
                trip, 1, "prod-restart-recovery", pickupLat, pickupLon);
            await faults.WaitForReachedAsync(TimeSpan.FromSeconds(40), ct);
            await PollUntilAsync(async () =>
            {
                var attempts = await LoadAttemptsAsync(trip, 1);
                return attempts.Any(a => a.DriverId == d1.DriverId && a.State == "IN_FLIGHT");
            }, "durable IN_FLIGHT before the process kill");

            // ACK has already happened: the broker queue is empty and the
            // only remaining work is the durable attempt.
            await PollUntilAsync(
                async () => await QueueDepthAsync("matching.ride-requested.q") == 0,
                "RideRequested ACKed before the crash");
            var before = Assert.Single(
                await LoadAttemptsAsync(trip, 1), a => a.DriverId == d1.DriverId);
            Assert.Equal("READY", Assert.Single(
                await LoadAttemptsAsync(trip, 1), a => a.DriverId == d2.DriverId).State);
            var committed = (await LoadDriverAsync(d1.DriverId))!;
            Assert.Equal("RESERVED", committed.Status);
            Assert.Equal(before.ReservationId, committed.ReservationId);

            await first.KillAsync();
            Assert.True(first.HasExited);

            Assert.Equal("IN_FLIGHT", Assert.Single(
                await LoadAttemptsAsync(trip, 1), a => a.DriverId == d1.DriverId).State);
            Assert.Equal("PROCESSING", (await LoadRoundAsync(trip, 1))!.Status);
            Assert.Equal("AVAILABLE", (await LoadDriverAsync(d2.DriverId))!.Status);

            Assert.True(faults.Release(FaultAfterReserveCommit));

            await using var restarted = await StartMatchingAsync(
                driverHost.BaseUrl, driverTimeoutSeconds: 5, ct: ct);

            await PollUntilAsync(async () =>
                (await LoadRoundAsync(trip, 1))?.Status == "RESERVED",
                "restarted worker recovers the persisted round", 60);

            var finalRound = (await LoadRoundAsync(trip, 1))!;
            var finalAttempts = await LoadAttemptsAsync(trip, 1);
            var finalAttempt = Assert.Single(finalAttempts, a => a.DriverId == d1.DriverId);
            Assert.Equal("READY", Assert.Single(finalAttempts, a => a.DriverId == d2.DriverId).State);
            Assert.Equal(before.ReservationId, finalAttempt.ReservationId);
            Assert.Equal(before.ReservationId, finalRound.SelectedReservationId);
            Assert.Equal(d1.DriverId, finalRound.SelectedDriverId);
            Assert.Equal("AVAILABLE", (await LoadDriverAsync(d2.DriverId))!.Status);
            Assert.Equal(1L, await DriverReservedOutboxCountAsync(d1.DriverId));
            Assert.Equal(0L, await MatchingFailedCountAsync(trip));

            Output.WriteLine(
                $"B-REC-01 restart: trip={trip} R={before.ReservationId} " +
                "killed after ACK, fresh process recovered same identity");
        }
        finally
        {
            await first.DisposeAsync();
        }
    }

    [Fact]
    public async Task Two_Matching_Processes_Converge_On_One_Reservation()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        var ct = deadline.Token;
        await PurgeBrokerAsync();
        await AbandonPriorTestWorkAsync();
        var (pickupLat, pickupLon) = NewPickup();
        var d1 = await SeedAvailableDriverAsync(pickupLat + 0.001, pickupLon);
        await using var driverHost = await DriverAuthorityHost.StartAsync(
            Drivers, DriverFaultPoint.Disabled(), ct);
        await using var first = await StartMatchingAsync(driverHost.BaseUrl, ct: ct);
        await using var second = await StartMatchingAsync(driverHost.BaseUrl, ct: ct);

        var trip = Guid.NewGuid();
        await PublishRideRequestedAsync(
            trip, 1, "prod-concurrent-workers", pickupLat, pickupLon);
        await PollUntilAsync(async () =>
            (await LoadRoundAsync(trip, 1))?.Status == "RESERVED",
            "one winner under two Matching processes", 60);

        var round = (await LoadRoundAsync(trip, 1))!;
        var attempt = Assert.Single(await LoadAttemptsAsync(trip, 1));
        Assert.Equal("RESERVED", attempt.State);
        Assert.Equal(attempt.ReservationId, round.SelectedReservationId);
        Assert.Equal(d1.DriverId, round.SelectedDriverId);
        Assert.Equal(1L, await DriverReservedOutboxCountAsync(d1.DriverId));
        Assert.Equal(1L, await DriverReservationIdentityCountAsync(d1.DriverId));
        Assert.Equal(0L, await MatchingFailedCountAsync(trip));

        Output.WriteLine(
            $"B-REC-01 concurrent: trip={trip} R={attempt.ReservationId} one durable winner");
    }

    [Fact]
    public async Task Redis_Outage_Does_Not_Terminalize_And_Recovers_When_Dependency_Returns()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        var ct = deadline.Token;
        await PurgeBrokerAsync();
        await AbandonPriorTestWorkAsync();
        var (pickupLat, pickupLon) = NewPickup();
        var d1 = await SeedAvailableDriverAsync(pickupLat + 0.001, pickupLon);
        await using var driverHost = await DriverAuthorityHost.StartAsync(
            Drivers, DriverFaultPoint.Disabled(), ct);

        var degraded = await StartMatchingAsync(
            driverHost.BaseUrl,
            redisConnection: "127.0.0.1:1",
            reconcileBackoff: TimeSpan.FromMilliseconds(500),
            ct: ct);
        var trip = Guid.NewGuid();
        try
        {
            await PublishRideRequestedAsync(
                trip, 1, "prod-redis-outage", pickupLat, pickupLon);
            await PollUntilAsync(async () =>
            {
                var round = await LoadRoundAsync(trip, 1);
                return round is { WorkErrorCode: "REDIS_UNAVAILABLE" };
            }, "durable degraded worker state", 45);

            var round = (await LoadRoundAsync(trip, 1))!;
            Assert.Equal("PROCESSING", round.Status);
            Assert.Empty(await LoadAttemptsAsync(trip, 1));
            Assert.Equal(0L, await MatchingFailedCountAsync(trip));
        }
        finally
        {
            await degraded.DisposeAsync();
        }

        await using var recovered = await StartMatchingAsync(driverHost.BaseUrl, ct: ct);
        await PollUntilAsync(async () =>
            (await LoadRoundAsync(trip, 1))?.Status == "RESERVED",
            "recovery after Redis returns", 60);

        var attempt = Assert.Single(await LoadAttemptsAsync(trip, 1));
        Assert.Equal("RESERVED", attempt.State);
        Assert.Equal(d1.DriverId, attempt.DriverId);
        Assert.Equal(0L, await MatchingFailedCountAsync(trip));

        Output.WriteLine(
            $"B-REC-01 outage: trip={trip} stayed PROCESSING while Redis was down, " +
            $"then reserved R={attempt.ReservationId}");
    }

    [Fact]
    public async Task Cancellation_While_Unknown_Does_Not_Advance_And_Authoritative_Reservation_Wins()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        var ct = deadline.Token;
        await PurgeBrokerAsync();
        await AbandonPriorTestWorkAsync();
        var (pickupLat, pickupLon) = NewPickup();
        var d1 = await SeedAvailableDriverAsync(pickupLat + 0.001, pickupLon);
        var d2 = await SeedAvailableDriverAsync(pickupLat + 0.002, pickupLon);
        var faults = DriverFaultPoint.ForTesting([FaultAfterReserveCommit], TimeSpan.FromSeconds(120));
        await using var driverHost = await DriverAuthorityHost.StartAsync(Drivers, faults, ct);
        await using var matching = await StartMatchingAsync(
            driverHost.BaseUrl,
            driverTimeoutSeconds: 3,
            reconcileBackoff: TimeSpan.FromSeconds(5),
            ct: ct);

        var trip = Guid.NewGuid();
        try
        {
            await PublishRideRequestedAsync(
                trip, 1, "prod-cancel-unknown", pickupLat, pickupLon);
            await faults.WaitForReachedAsync(TimeSpan.FromSeconds(40), ct);
            await PollUntilAsync(async () =>
            {
                var attempts = await LoadAttemptsAsync(trip, 1);
                return attempts.Any(a => a.DriverId == d1.DriverId && a.State == "UNKNOWN");
            }, "UNKNOWN before cancellation", 30);

            await PublishCancelAsync(trip, 1);
            await PollUntilAsync(async () =>
                (await LoadRoundAsync(trip, 1))?.CancelRequested == true,
                "durable cancel flag", 30);

            // Cancellation itself starts no new candidate while the effect
            // is unresolved; D2's durable row stays READY, never dispatched.
            var cancelAttempts = await LoadAttemptsAsync(trip, 1);
            Assert.Equal("UNKNOWN", Assert.Single(cancelAttempts, a => a.DriverId == d1.DriverId).State);
            Assert.Equal("READY", Assert.Single(cancelAttempts, a => a.DriverId == d2.DriverId).State);
            Assert.Equal("AVAILABLE", (await LoadDriverAsync(d2.DriverId))!.Status);

            await PollUntilAsync(async () =>
                (await LoadRoundAsync(trip, 1))?.Status == "RESERVED",
                "same-R reconciliation despite cancellation", 30);

            var round = (await LoadRoundAsync(trip, 1))!;
            var finalAttempts = await LoadAttemptsAsync(trip, 1);
            var attempt = Assert.Single(finalAttempts, a => a.DriverId == d1.DriverId);
            Assert.Equal("READY", Assert.Single(finalAttempts, a => a.DriverId == d2.DriverId).State);
            Assert.True(round.CancelRequested);
            Assert.Equal("RESERVED", attempt.State);
            Assert.Equal(d1.DriverId, round.SelectedDriverId);
            Assert.Equal(attempt.ReservationId, round.SelectedReservationId);
            Assert.Equal("AVAILABLE", (await LoadDriverAsync(d2.DriverId))!.Status);
            Assert.Equal(0L, await MatchingFailedCountAsync(trip));

            Output.WriteLine(
                $"B-REC-01 cancel+unknown: trip={trip} R={attempt.ReservationId} " +
                "no D2, reservation remains authoritative");
        }
        finally
        {
            faults.Release(FaultAfterReserveCommit);
        }
    }

    [Fact]
    public async Task Cancel_Before_RideRequested_Is_Durable_And_Starts_No_Work()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var ct = deadline.Token;
        await PurgeBrokerAsync();
        await AbandonPriorTestWorkAsync();
        var (pickupLat, pickupLon) = NewPickup();
        var d1 = await SeedAvailableDriverAsync(pickupLat + 0.001, pickupLon);
        await using var driverHost = await DriverAuthorityHost.StartAsync(
            Drivers, DriverFaultPoint.Disabled(), ct);
        await using var matching = await StartMatchingAsync(driverHost.BaseUrl, ct: ct);

        var trip = Guid.NewGuid();
        await PublishCancelAsync(trip, 1);
        await PollUntilAsync(
            async () => await CancelIntentExistsAsync(trip, 1),
            "cancel tombstone before the round exists", 30);

        await PublishRideRequestedAsync(
            trip, 1, "prod-cancel-first", pickupLat, pickupLon);
        await PollUntilAsync(async () =>
        {
            var round = await LoadRoundAsync(trip, 1);
            return round is { Status: "CANCELLED", CancelRequested: true };
        }, "round born cancelled from the durable intent", 45);

        Assert.Empty(await LoadAttemptsAsync(trip, 1));
        Assert.Equal("AVAILABLE", (await LoadDriverAsync(d1.DriverId))!.Status);
        Assert.Equal(0L, await DriverReservedOutboxCountAsync(d1.DriverId));
        Assert.Equal(0L, await MatchingFailedCountAsync(trip));

        Output.WriteLine(
            $"B-REC-01 cancel-first: trip={trip} tombstone applied atomically at intake");
    }
}
