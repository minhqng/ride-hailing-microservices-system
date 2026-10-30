using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RabbitMQ.Client;
using RideHailing.MatchingService.Application.Messaging;
using RideHailing.MatchingService.Infrastructure.Persistence;
using RideHailing.Messaging.Contracts;
using RideHailing.Messaging.Reliability;
using RideHailing.Messaging.Topology;
using Testcontainers.RabbitMq;
using Xunit.Abstractions;

namespace RideHailing.MatchingService.IntegrationTests;

[Collection(ProductionWorkflowCollection.Name)]
public sealed class BacklogRecoveryTests(
    MatchingPostgresDatabase pg, DriverTestDatabase drivers, RedisDatabase redis,
    RabbitMqDatabase rabbit, ITestOutputHelper output)
    : ProductionMatchingTestBase(pg, drivers, redis, rabbit, output)
{
    [Fact]
    public async Task BrokerStopAndReturn_ReportsUncertainPublish_AndLiveApplicationReconnects()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        var ct = deadline.Token;
        await AbandonPriorTestWorkAsync();
        // This container belongs exclusively to this test. External/shared brokers are never stopped.
        using var portProbe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        portProbe.Start();
        var port = ((System.Net.IPEndPoint)portProbe.LocalEndpoint).Port;
        portProbe.Stop();
        await using var broker = new RabbitMqBuilder("rabbitmq:4.3.6-management")
            .WithPortBinding(port, 5672).Build();
        await broker.StartAsync(ct);
        var uri = new Uri(broker.GetConnectionString());
        var credentials = uri.UserInfo.Split(':', 2);
        var factory = new ConnectionFactory { Uri = uri, AutomaticRecoveryEnabled = true };
        await using (var connection = await factory.CreateConnectionAsync(ct))
            await RideTopology.DeclareAsync(connection, ct);
        var (lat, lon) = NewPickup();
        var firstDriver = await SeedAvailableDriverAsync(lat, lon);
        await using var driverHost = await DriverAuthorityHost.StartAsync(Drivers,
            RideHailing.DriverService.Infrastructure.Faults.DriverFaultPoint.Disabled(), ct);
        var environment = MatchingEnvironment(driverHost.BaseUrl);
        environment["RabbitMq__Host"] = uri.Host;
        environment["RabbitMq__Port"] = uri.Port.ToString();
        environment["RabbitMq__Username"] = Uri.UnescapeDataString(credentials[0]);
        environment["RabbitMq__Password"] = Uri.UnescapeDataString(credentials[1]);
        await using var matching = await MatchingServiceProcess.StartAsync(environment, ct);
        await using var publisher = new ConfirmedPublisher(factory);
        MessageEnvelope<JsonElement> Request(Guid trip) => MessageEnvelope<JsonElement>.Create(
            "RideRequestedV1", trip.ToString(), JsonSerializer.SerializeToElement(new
            {
                tripId = trip, matchingRound = 1, pickup = new { latitude = lat, longitude = lon },
            }));
        var baseline = Request(Guid.NewGuid());
        await publisher.PublishAsync("ride.events", "trip.ride-requested.v1", baseline, ct);
        await PollUntilAsync(async () => (await LoadRoundAsync(baseline.Payload.GetProperty("tripId").GetGuid(), 1))
            ?.Status == "RESERVED", "baseline on dedicated broker", 60);
        var original = (await LoadDriverAsync(firstDriver.DriverId))!;
        await broker.StopAsync(ct);
        var resumed = Request(Guid.NewGuid());
        await Assert.ThrowsAnyAsync<Exception>(() => publisher.PublishAsync(
            "ride.events", "trip.ride-requested.v1", resumed, ct));
        Assert.Null(await LoadRoundAsync(resumed.Payload.GetProperty("tripId").GetGuid(), 1));
        Assert.Equal(original, await LoadDriverAsync(firstDriver.DriverId));
        Assert.Equal(1L, await DriverReservedOutboxCountAsync(firstDriver.DriverId));
        await broker.StartAsync(ct);
        Assert.Equal(port, new Uri(broker.GetConnectionString()).Port);
        var secondDriver = await SeedAvailableDriverAsync(lat + 0.001, lon);
        await publisher.PublishAsync("ride.events", "trip.ride-requested.v1", resumed, ct);
        var resumedTrip = resumed.Payload.GetProperty("tripId").GetGuid();
        await PollUntilAsync(async () => (await LoadRoundAsync(resumedTrip, 1))?.Status == "RESERVED",
            "same live process reconnected after real broker restart", 60);
        Assert.False(matching.HasExited);
        var recoveredAttempts = await LoadAttemptsAsync(resumedTrip, 1);
        var attempt = Assert.Single(recoveredAttempts, item => item.State == "RESERVED");
        Assert.All(recoveredAttempts.Where(item => item.State != "RESERVED"), item =>
        {
            Assert.Equal("REJECTED", item.State);
            Assert.Equal(firstDriver.DriverId, item.DriverId);
        });
        Assert.Equal(secondDriver.DriverId, attempt.DriverId);
        Assert.Equal(resumed.MessageId, (await LoadRoundAsync(resumedTrip, 1))!.InputMessageId);
        Assert.Equal(attempt.ReservationId, (await LoadDriverAsync(secondDriver.DriverId))!.ReservationId);
        Assert.Equal(1L, await DriverReservedOutboxCountAsync(secondDriver.DriverId));
        Assert.Equal(0L, await MatchingFailedCountAsync(resumedTrip));
        Output.WriteLine("brokerStopReturn=true publisherUncertaintyObserved=true liveReconnect=true " +
            "duplicateDriverEffects=0 falseMatchingTerminal=0");
    }

    [Fact]
    public async Task CommittedIntakeBeforeAck_RedeliversAndResumesOneReservationAfterProcessReturn()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(150));
        var ct = deadline.Token;
        await PurgeBrokerAsync();
        await AbandonPriorTestWorkAsync();
        var (lat, lon) = NewPickup();
        var driver = await SeedAvailableDriverAsync(lat, lon);
        var trip = Guid.NewGuid();
        var envelope = MessageEnvelope<JsonElement>.Create("RideRequestedV1", trip.ToString(),
            JsonSerializer.SerializeToElement(new
            {
                tripId = trip, matchingRound = 1,
                pickup = new { latitude = lat, longitude = lon },
            }), messageId: $"recovery-{Guid.NewGuid():N}");
        using var connection = await RabbitFactory().CreateConnectionAsync(ct);
        await RideTopology.DeclareAsync(connection, ct);
        await using var publisher = new ConfirmedPublisher(RabbitFactory());
        await publisher.PublishAsync("ride.events", "trip.ride-requested.v1", envelope, ct);

        var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new RideRequestedHandler(new MatchingAttemptRepository(Pg.ConnectionString),
            NullLogger<RideRequestedHandler>.Instance);
        using var consumerStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var consumer = BrokerConsume.RunAsync(RabbitFactory(), "matching.ride-requested.q", 1, 3,
            async (delivery, token) =>
            {
                await handler.HandleAsync(delivery, token);
                committed.TrySetResult();
                // Deterministic crash window: PostgreSQL commit happened, broker ACK cannot happen.
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return HandlerOutcome.Done;
            }, NullLogger.Instance, consumerStop.Token);
        try
        {
            await committed.Task.WaitAsync(ct);
            var persisted = (await LoadRoundAsync(trip, 1))!;
            Assert.Equal(envelope.MessageId, persisted.InputMessageId);
            Assert.True(persisted.AutoExecute);
            Assert.Equal("PROCESSING", persisted.Status);
            Assert.Empty(await LoadAttemptsAsync(trip, 1));
        }
        finally
        {
            consumerStop.Cancel();
            await consumer.WaitAsync(ct);
        }
        await PollUntilAsync(async () => await QueueDepthAsync("matching.ride-requested.q") == 1,
            "unacked delivery returned to ready after connection closes");

        // Capture real redelivery and its original identity before production resumes.
        await using (var channel = await connection.CreateChannelAsync(cancellationToken: ct))
        {
            var redelivery = await channel.BasicGetAsync("matching.ride-requested.q", false, ct);
            Assert.NotNull(redelivery);
            Assert.True(redelivery.Redelivered);
            Assert.Equal(envelope.MessageId,
                BrokerEnvelope.Parse(redelivery.Body.ToArray()).MessageId);
            await channel.BasicNackAsync(redelivery.DeliveryTag, false, true, ct);
        }
        // Continued publication during the consumer outage, including duplicate work.
        await publisher.PublishAsync("ride.events", "trip.ride-requested.v1", envelope, ct);
        await using var driverHost = await DriverAuthorityHost.StartAsync(Drivers,
            RideHailing.DriverService.Infrastructure.Faults.DriverFaultPoint.Disabled(), ct);
        await using (var matching = await StartMatchingAsync(driverHost.BaseUrl, ct: ct))
        {
            await PollUntilAsync(async () => (await LoadRoundAsync(trip, 1))?.Status == "RESERVED",
                "production worker resumed ACKed and redelivered durable work", 60);
            await PollUntilAsync(async () => await QueueDepthAsync("matching.ride-requested.q") == 0,
                "broker ready backlog drained");
            Assert.Contains(envelope.MessageId, (await LoadRoundAsync(trip, 1))!.InputMessageId);
            await matching.KillAsync();
        }
        var beforeReplay = Assert.Single(await LoadAttemptsAsync(trip, 1));
        await publisher.PublishAsync("ride.events", "trip.ride-requested.v1", envelope, ct);
        await using (var restarted = await StartMatchingAsync(driverHost.BaseUrl, ct: ct))
        {
            await PollUntilAsync(async () => await QueueDepthAsync("matching.ride-requested.q") == 0,
                "duplicate delivery drained after real process restart");
            var afterReplay = Assert.Single(await LoadAttemptsAsync(trip, 1));
            Assert.Equal(beforeReplay.ReservationId, afterReplay.ReservationId);
            Assert.Equal("RESERVED", afterReplay.State);
        }
        var authority = (await LoadDriverAsync(driver.DriverId))!;
        Assert.Equal("RESERVED", authority.Status);
        Assert.Equal(trip, authority.ReservedTripId);
        Assert.Equal(beforeReplay.ReservationId, authority.ReservationId);
        Assert.Equal(1L, await DriverReservedOutboxCountAsync(driver.DriverId));
        Assert.Equal(0L, await MatchingFailedCountAsync(trip));
        Output.WriteLine($"confirmed=true redelivered=true trip={trip} message={envelope.MessageId} " +
            $"reservation={authority.ReservationId} driverEffects=1 matchingFalseTerminal=0");
    }
}
