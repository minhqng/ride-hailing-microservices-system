// L1 real production-process proof: the Matching worker crashes after the
// Driver committed the reservation, the ride then starts (or completes), and
// a fresh Matching process must recover the ORIGINAL reservation instead of
// rejecting D1 and reserving D2. The lifecycle commands run through the real
// Driver command handler (the same path the Trip saga consumer uses), and the
// Matching process is a real OS process kill/restart.
using System.Text.Json;
using RideHailing.DriverService.Application.Messaging;
using RideHailing.DriverService.Infrastructure.Faults;
using RideHailing.Messaging.Contracts;
using Xunit.Abstractions;

namespace RideHailing.MatchingService.IntegrationTests;

[Collection(ProductionWorkflowCollection.Name)]
public sealed class ProductionMatchingLifecycleRecoveryTests : ProductionMatchingTestBase
{
    public ProductionMatchingLifecycleRecoveryTests(
        MatchingPostgresDatabase pg,
        DriverTestDatabase drivers,
        RedisDatabase redis,
        RabbitMqDatabase rabbit,
        ITestOutputHelper output)
        : base(pg, drivers, redis, rabbit, output)
    {
    }

    private static MessageEnvelope<JsonElement> DriverCommandEnvelope(
        string type, Guid driverId, Guid tripId, Guid reservationId, string messageId) =>
        MessageEnvelope<JsonElement>.Create(
            type,
            tripId.ToString("N"),
            JsonSerializer.SerializeToElement(new
            {
                driverId,
                tripId,
                reservationId,
                matchingRound = 1,
            }),
            messageId: messageId);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Restart_AfterLifecycleAdvance_RecoversOriginalReservation_WithoutD2(
        bool completed)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(240));
        var ct = deadline.Token;
        await PurgeBrokerAsync();
        await AbandonPriorTestWorkAsync();
        var (pickupLat, pickupLon) = NewPickup();
        var d1 = await SeedAvailableDriverAsync(pickupLat + 0.001, pickupLon);
        var d2 = await SeedAvailableDriverAsync(pickupLat + 0.002, pickupLon);
        var faults = DriverFaultPoint.ForTesting(
            [FaultAfterReserveCommit], TimeSpan.FromSeconds(180));
        await using var driverHost = await DriverAuthorityHost.StartAsync(Drivers, faults, ct);
        var first = await StartMatchingAsync(driverHost.BaseUrl, driverTimeoutSeconds: 90, ct: ct);
        var trip = Guid.NewGuid();
        try
        {
            await PublishRideRequestedAsync(
                trip, 1, $"prod-lifecycle-{(completed ? "completed" : "started")}",
                pickupLat, pickupLon);

            // The Driver committed R and is holding the response.
            await faults.WaitForReachedAsync(TimeSpan.FromSeconds(60), ct);
            var committed = (await LoadDriverAsync(d1.DriverId))!;
            Assert.Equal("RESERVED", committed.Status);
            var reservationId = committed.ReservationId!.Value;

            // Durable write-ahead attempt survives the kill; the response was
            // never observed by Matching.
            AttemptState? before = null;
            await PollUntilAsync(async () =>
            {
                before = (await LoadAttemptsAsync(trip, 1))
                    .SingleOrDefault(a => a.DriverId == d1.DriverId);
                return before is { State: "IN_FLIGHT" or "UNKNOWN" };
            }, "durable unresolved attempt before the process kill", 45);
            Assert.Equal(reservationId, before!.ReservationId);
            Assert.Equal("READY",
                Assert.Single(await LoadAttemptsAsync(trip, 1), a => a.DriverId == d2.DriverId).State);
            await PollUntilAsync(
                async () => await QueueDepthAsync("matching.ride-requested.q") == 0,
                "RideRequested ACKed before the crash");

            await first.KillAsync();
            Assert.True(first.HasExited);

            // The ride progresses before Matching recovers (real Driver
            // command handler, same as the Trip saga consumer).
            var repo = Drivers.CreateRepository();
            var started = await repo.HandleCommandAsync(DriverCommandEnvelope(
                DriverCommand.Start, d1.DriverId, trip, reservationId, $"start:{trip:N}"), ct);
            Assert.Equal("Applied", started.Disposition);
            if (completed)
            {
                var finished = await repo.HandleCommandAsync(DriverCommandEnvelope(
                    DriverCommand.Complete, d1.DriverId, trip, reservationId,
                    $"complete:{trip:N}"), ct);
                Assert.Equal("Applied", finished.Disposition);
                Assert.Equal("AVAILABLE", (await LoadDriverAsync(d1.DriverId))!.Status);
            }
            else
            {
                Assert.Equal("ON_TRIP", (await LoadDriverAsync(d1.DriverId))!.Status);
            }

            Assert.True(faults.Release(FaultAfterReserveCommit));

            await using var restarted = await StartMatchingAsync(
                driverHost.BaseUrl,
                driverTimeoutSeconds: 5,
                reconcileBackoff: TimeSpan.FromMilliseconds(500),
                ct: ct);

            await PollUntilAsync(async () =>
                (await LoadRoundAsync(trip, 1))?.Status == "RESERVED",
                "restarted worker recovers the committed reservation", 60);

            var round = (await LoadRoundAsync(trip, 1))!;
            var attempts = await LoadAttemptsAsync(trip, 1);
            var winner = Assert.Single(attempts, a => a.DriverId == d1.DriverId);
            Assert.Equal("RESERVED", winner.State);
            Assert.Equal(reservationId, winner.ReservationId);
            Assert.Equal("CANCELLED", Assert.Single(attempts, a => a.DriverId == d2.DriverId).State);
            Assert.Equal(d1.DriverId, round.SelectedDriverId);
            Assert.Equal(reservationId, round.SelectedReservationId);
            Assert.Equal(
                completed ? "AVAILABLE" : "ON_TRIP",
                (await LoadDriverAsync(d1.DriverId))!.Status);
            Assert.Equal("AVAILABLE", (await LoadDriverAsync(d2.DriverId))!.Status);
            Assert.Equal(1L, await DriverReservedOutboxCountAsync(d1.DriverId));
            Assert.Equal(0L, await MatchingFailedCountAsync(trip));

            Output.WriteLine(
                $"L1 production restart ({(completed ? "COMPLETED" : "STARTED")}): " +
                $"trip={trip} R={reservationId} recovered without D2");
        }
        finally
        {
            faults.Release(FaultAfterReserveCommit);
            await first.DisposeAsync();
        }
    }
}
