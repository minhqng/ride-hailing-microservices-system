using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using RabbitMQ.Client;
using RideHailing.DriverService.Application.Messaging;
using RideHailing.DriverService.Domain.Drivers;
using RideHailing.DriverService.Infrastructure.Persistence;
using RideHailing.Messaging.Contracts;
using RideHailing.Messaging.Outbox;
using RideHailing.Messaging.Reliability;
using RideHailing.Messaging.Topology;

namespace RideHailing.DriverService.IntegrationTests;

[Collection(DriverBrokerCollection.Name)]
public sealed class DriverLifecycleBrokerTests(DriverPostgresDatabase db, RabbitMqDatabase rabbit)
{
    private ConnectionFactory Factory => new() { Uri = new(rabbit.ConnectionString), AutomaticRecoveryEnabled = false };

    private async Task<(Guid Driver, Guid Trip, Guid Reservation)> ReservedAsync()
    {
        var driver = Driver.Create(Guid.NewGuid(), Guid.NewGuid());
        driver.GoOnline(driver.Version);
        var repo = db.CreateRepository();
        await repo.TryInsertAsync(driver);
        var trip = Guid.NewGuid();
        var reservation = Guid.NewGuid();
        Assert.Equal(ReserveOutcome.Won,
            (await repo.TryReserveAsync(driver.DriverId, trip, reservation, driver.Version, matchingRound: 1)).Outcome);
        return (driver.DriverId, trip, reservation);
    }

    private async Task PrepareAsync()
    {
        await using var connection = await Factory.CreateConnectionAsync();
        await RideTopology.DeclareAsync(connection);
        await using var channel = await connection.CreateChannelAsync();
        await channel.QueuePurgeAsync(DriverCommand.Queue);
        await channel.QueuePurgeAsync(DriverCommand.Queue + ".dead");
    }

    private static async Task UntilAsync(Func<Task<bool>> predicate)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!await predicate()) await Task.Delay(25, deadline.Token);
    }

    [Fact]
    public async Task DefaultConfiguration_StartsProductionHostAndConsumesCommand()
    {
        await PrepareAsync();
        var (driver, trip, reservation) = await ReservedAsync();
        var factory = Factory;
        using var host = Host.CreateDefaultBuilder().ConfigureServices(services =>
        {
            services.AddSingleton(db.DataSource);
            services.AddSingleton(db.OutboxStore);
            services.AddSingleton<DriverRepository>();
            services.AddSingleton<DriverCommandHandler>();
            services.AddSingleton(factory);
            services.AddSingleton<ConfirmedPublisher>();
            if (new DriverBrokerOptions().CommandsEnabled)
                services.AddHostedService<DriverCommandConsumerHost>();
        }).Build();
        await host.StartAsync();
        try
        {
            await using var connection = await factory.CreateConnectionAsync();
            await using var channel = await connection.CreateChannelAsync();
            await UntilAsync(async () => await channel.ConsumerCountAsync(DriverCommand.Queue) == 1);
            await host.Services.GetRequiredService<ConfirmedPublisher>().PublishAsync(
                RideTopology.CommandsExchange, "driver.mark-trip-started.v1",
                DriverLifecycleTests.Command(DriverCommand.Start, driver, trip, reservation));
            await UntilAsync(async () => (await db.CreateRepository().LoadAsync(driver))!.Status == DriverStatus.OnTrip);
            Assert.Equal(4, (await db.CreateRepository().LoadAsync(driver))!.Version);

            // Keep ordinary reads legal but block ALTER TABLE. Cancelling the
            // disposable queue forces a real consumer reconnect; it must not
            // need to replay DDL before consuming the next command.
            await using var readerConnection = await db.DataSource.OpenConnectionAsync();
            await using var readTransaction = await readerConnection.BeginTransactionAsync();
            await using (var readLock = new NpgsqlCommand("LOCK TABLE drivers IN ACCESS SHARE MODE", readerConnection, readTransaction))
                await readLock.ExecuteNonQueryAsync();
            await channel.QueueDeleteAsync(DriverCommand.Queue, ifUnused: false, ifEmpty: false);
            await RideTopology.DeclareAsync(connection);
            await UntilAsync(async () => await channel.ConsumerCountAsync(DriverCommand.Queue) == 1);
            await host.Services.GetRequiredService<ConfirmedPublisher>().PublishAsync(
                RideTopology.CommandsExchange, "driver.mark-trip-completed.v1",
                DriverLifecycleTests.Command(DriverCommand.Complete, driver, trip, reservation));
            await UntilAsync(async () => (await db.CreateRepository().LoadAsync(driver))!.Status == DriverStatus.Available);
            await readTransaction.RollbackAsync();
        }
        finally { await host.StopAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommitBeforeAck_RealBrokerRedeliveryConverges(bool completed)
    {
        await PrepareAsync();
        var (driver, trip, reservation) = await ReservedAsync();
        if (completed) await db.CreateRepository().TryMarkTripStartedAsync(driver, trip, reservation);
        var command = DriverLifecycleTests.Command(completed ? DriverCommand.Complete : DriverCommand.Start, driver, trip, reservation);
        await using var publisher = new ConfirmedPublisher(Factory);
        var committed = new TaskCompletionSource<DriverLifecycleResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var first = await ManualAckConsumer.StartAsync(Factory, publisher, DriverCommand.Queue,
            DriverCommand.SupportedTypes, async (message, ct) =>
            {
                committed.TrySetResult(await db.CreateRepository().HandleCommandAsync(message, ct));
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }, prefetch: 1);
        await publisher.PublishAsync(RideTopology.CommandsExchange,
            completed ? "driver.mark-trip-completed.v1" : "driver.mark-trip-started.v1", command);
        var original = await committed.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal("Applied", original.Disposition);
        Assert.Equal(0, first.AcknowledgedCount);
        await first.DisposeAsync();
        await using var second = await ManualAckConsumer.StartAsync(Factory, publisher, DriverCommand.Queue,
            DriverCommand.SupportedTypes, new DriverCommandHandler(db.CreateRepository()).HandleAsync, prefetch: 1);
        await UntilAsync(() => Task.FromResult(second.AcknowledgedCount == 1));
        Assert.Equal(original, await db.CreateRepository().HandleCommandAsync(command));
        Assert.Equal(completed ? 5 : 4, (await db.CreateRepository().LoadAsync(driver))!.Version);
        Assert.NotNull(await db.OutboxStore.LoadAsync(original.EventMessageId!));
    }

    [Fact]
    public async Task SharedQueue_HandlesReleaseAndLifecycle_AndQuarantinesPoison()
    {
        await PrepareAsync();
        var (driver, trip, reservation) = await ReservedAsync();
        var (releasedDriver, releasedTrip, releasedReservation) = await ReservedAsync();
        await using var publisher = new ConfirmedPublisher(Factory);
        await using var consumer = await ManualAckConsumer.StartAsync(Factory, publisher, DriverCommand.Queue,
            DriverCommand.SupportedTypes, new DriverCommandHandler(db.CreateRepository()).HandleAsync, prefetch: 1);
        var start = DriverLifecycleTests.Command(DriverCommand.Start, driver, trip, reservation);
        await publisher.PublishAsync(RideTopology.CommandsExchange, "driver.mark-trip-started.v1", start);
        await publisher.PublishAsync(RideTopology.CommandsExchange, "driver.mark-trip-completed.v1",
            DriverLifecycleTests.Command(DriverCommand.Complete, driver, trip, reservation));
        await publisher.PublishAsync(RideTopology.CommandsExchange, "driver.release-reservation.v1",
            DriverLifecycleTests.Command(DriverCommand.Release, releasedDriver, releasedTrip, releasedReservation));
        await publisher.PublishAsync(RideTopology.CommandsExchange, "driver.mark-trip-started.v1", start with
            { Payload = JsonSerializer.SerializeToElement(new { driverId = driver }) });
        await UntilAsync(() => Task.FromResult(consumer.AcknowledgedCount == 4));
        Assert.Equal(DriverStatus.Available, (await db.CreateRepository().LoadAsync(driver))!.Status);
        Assert.Equal(DriverStatus.Available, (await db.CreateRepository().LoadAsync(releasedDriver))!.Status);
        await using var connection = await Factory.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        var poison = await channel.BasicGetAsync(DriverCommand.Queue + ".dead", true);
        Assert.NotNull(poison);
        Assert.Equal("DeliveryErrorV1", poison!.BasicProperties.Type);
        Assert.Contains("INVALID_DRIVER_COMMAND", System.Text.Encoding.UTF8.GetString(poison.Body.Span));
    }

    [Fact]
    public async Task LifecycleRelay_MarkLossRepublishesIdenticalEnvelope()
    {
        await PrepareAsync();
        await using (var conn = await db.DataSource.OpenConnectionAsync())
        {
            await using var clear = new NpgsqlCommand("TRUNCATE outbox_messages", conn);
            await clear.ExecuteNonQueryAsync();
        }
        var (driver, trip, reservation) = await ReservedAsync();
        var start = await db.CreateRepository().TryMarkTripStartedAsync(driver, trip, reservation);
        var complete = await db.CreateRepository().TryMarkTripCompletedAsync(driver, trip, reservation);
        await using var connection = await Factory.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        var queue = (await channel.QueueDeclareAsync()).QueueName;
        await channel.QueueBindAsync(queue, RideTopology.EventsExchange, "driver.trip-started.v1");
        await channel.QueueBindAsync(queue, RideTopology.EventsExchange, "driver.trip-completed.v1");
        await using var publisher = new ConfirmedPublisher(Factory);
        var loseMark = true;
        var relay = new OutboxRelay(db.OutboxStore, new RabbitOutboxPublisher(publisher), new OutboxRelayOptions
        {
            RelayId = "lifecycle-mark-loss", RetryDelay = TimeSpan.Zero,
            AfterPublishBeforeMark = (message, _) =>
            {
                if (loseMark && message.MessageId == start.EventMessageId) throw new IOException("lost mark");
                return Task.CompletedTask;
            }
        });
        await Assert.ThrowsAsync<IOException>(() => relay.RunOnceAsync());
        loseMark = false;
        await new OutboxRelay(db.OutboxStore, new RabbitOutboxPublisher(publisher),
            new OutboxRelayOptions { RelayId = "lifecycle-restart", RetryDelay = TimeSpan.Zero,
                ClaimLease = TimeSpan.Zero }).RunOnceAsync();
        var messages = new List<BasicGetResult>();
        await UntilAsync(async () =>
        {
            var message = await channel.BasicGetAsync(queue, true);
            if (message is not null) messages.Add(message);
            return messages.Count == 3;
        });
        var starts = messages.Where(m => m.BasicProperties.MessageId == start.EventMessageId).ToArray();
        Assert.Equal(2, starts.Length);
        Assert.Equal(starts[0].Body.ToArray(), starts[1].Body.ToArray());
        Assert.Single(messages, m => m.BasicProperties.MessageId == complete.EventMessageId);
        Assert.Equal(OutboxStatus.Published, (await db.OutboxStore.LoadAsync(start.EventMessageId!))!.Status);
    }
}
