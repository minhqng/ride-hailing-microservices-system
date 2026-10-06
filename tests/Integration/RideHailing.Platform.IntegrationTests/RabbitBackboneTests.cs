using System.Diagnostics;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Npgsql;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;
using RideHailing.Messaging.Contracts;
using RideHailing.Messaging.Reliability;
using RideHailing.Messaging.Topology;
using Testcontainers.PostgreSql;

namespace RideHailing.Platform.IntegrationTests;

public sealed class RabbitBackboneFixture : IAsyncLifetime
{
    public IContainer Broker { get; } = new ContainerBuilder("rabbitmq:4.3.6-management")
        .WithEnvironment("RABBITMQ_DEFAULT_USER", "proof")
        .WithEnvironment("RABBITMQ_DEFAULT_PASS", "proof-local-only")
        .WithPortBinding(5672, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Server startup complete"))
        .Build();
    private readonly PostgreSqlContainer database = new PostgreSqlBuilder("postgres:18.6").Build();
    public NpgsqlDataSource Source { get; private set; } = null!;
    public ConnectionFactory Factory => new()
    {
        HostName = Broker.Hostname, Port = Broker.GetMappedPublicPort(5672),
        UserName = "proof", Password = "proof-local-only", AutomaticRecoveryEnabled = false,
        RequestedConnectionTimeout = TimeSpan.FromSeconds(3), ClientProvidedName = "W8-proof"
    };
    public async Task InitializeAsync()
    {
        await Broker.StartAsync();
        await database.StartAsync();
        Source = NpgsqlDataSource.Create(database.GetConnectionString());
        await using var command = Source.CreateCommand("CREATE TABLE committed_deliveries(message_id text PRIMARY KEY)");
        await command.ExecuteNonQueryAsync();
        await using var connection = await Factory.CreateConnectionAsync();
        await RideTopology.DeclareAsync(connection);
    }
    public async Task DisposeAsync()
    {
        if (Source is not null) await Source.DisposeAsync();
        await database.DisposeAsync();
        await Broker.DisposeAsync();
    }
    public async Task<string> QueueAsync()
    {
        var queue = "proof." + Guid.NewGuid().ToString("N");
        await using var connection = await Factory.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        foreach (var name in new[] { queue, queue + ".dead" })
            await channel.QueueDeclareAsync(name, true, false, false, new Dictionary<string, object?> { ["x-queue-type"] = "quorum" });
        await channel.QueueBindAsync(queue, "ride.commands", queue);
        await channel.QueueBindAsync(queue + ".dead", "ride.deadletter", queue);
        return queue;
    }
}

public class RabbitBackboneTests(RabbitBackboneFixture fixture) : IClassFixture<RabbitBackboneFixture>
{
    private static MessageEnvelope<JsonElement> Message() => MessageEnvelope<JsonElement>.Create(
        "ProofV1", Guid.NewGuid().ToString(), JsonSerializer.SerializeToElement(new { value = 1 }));
    private static async Task Eventually(Func<Task<bool>> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!await predicate()) await Task.Delay(100, timeout.Token);
    }

    [Fact]
    public async Task ConfirmDoesNotMeanConsumerAckAndCommitBeforeAckRedelivers()
    {
        var queue = await fixture.QueueAsync();
        var message = Message();
        await using var publisher = new ConfirmedPublisher(fixture.Factory);
        // Confirm succeeds even with no consumer attached.
        await publisher.PublishAsync("ride.commands", queue, message);
        var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var consumer = await ManualAckConsumer.StartAsync(fixture.Factory, publisher, queue,
            new HashSet<string> { "ProofV1" }, async (envelope, token) =>
            {
                await using var command = fixture.Source.CreateCommand("INSERT INTO committed_deliveries VALUES(@id)");
                command.Parameters.AddWithValue("id", envelope.MessageId);
                await command.ExecuteNonQueryAsync(token); // Autocommit completes before handler returns.
                committed.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token); // Hold the commit-before-ACK window open.
            }, prefetch: 1);
        await committed.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(0, consumer.AcknowledgedCount);
        await consumer.DisposeAsync(); // Connection close, no ACK: broker must redeliver.
        await using var connection = await fixture.Factory.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        BasicGetResult? redelivery = null;
        await Eventually(async () => (redelivery = await channel.BasicGetAsync(queue, false)) is not null);
        Assert.True(redelivery!.Redelivered);
        Assert.Equal(message.MessageId, redelivery.BasicProperties.MessageId);
        await using var count = fixture.Source.CreateCommand("SELECT count(*) FROM committed_deliveries WHERE message_id=@id");
        count.Parameters.AddWithValue("id", message.MessageId);
        Assert.Equal(1L, (long)(await count.ExecuteScalarAsync())!);
        await channel.BasicAckAsync(redelivery.DeliveryTag, false);
        // W9 must make a real business consumer's redelivery harmless; this test intentionally does not reapply it.
    }

    [Fact]
    public async Task MandatoryUnroutablePublishFailsVisibly()
    {
        await using var publisher = new ConfirmedPublisher(fixture.Factory);
        var error = await Assert.ThrowsAsync<PublishReturnException>(() => publisher.PublishAsync("ride.commands", "missing." + Guid.NewGuid(), Message()));
        Assert.True(error.IsReturn);
    }

    [Fact]
    public async Task DurableHandlerReceivesTraceAndAckOccursAfterReturn()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "RideHailing.Messaging",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(listener);
        using var root = new Activity("proof-root").SetIdFormat(ActivityIdFormat.W3C).Start();
        var message = Message();
        var queue = await fixture.QueueAsync();
        await using var publisher = new ConfirmedPublisher(fixture.Factory);
        var observed = new TaskCompletionSource<ActivityContext>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var consumer = await ManualAckConsumer.StartAsync(fixture.Factory, publisher, queue, new HashSet<string> { "ProofV1" },
            async (envelope, token) =>
            {
                Assert.Equal(message.CorrelationId, envelope.CorrelationId);
                await using var command = fixture.Source.CreateCommand("INSERT INTO committed_deliveries VALUES(@id)");
                command.Parameters.AddWithValue("id", envelope.MessageId);
                await command.ExecuteNonQueryAsync(token);
                observed.SetResult(Activity.Current!.Context);
            });
        await publisher.PublishAsync("ride.commands", queue, message);
        Assert.Equal(root.TraceId, (await observed.Task.WaitAsync(TimeSpan.FromSeconds(20))).TraceId);
        await Eventually(() => Task.FromResult(consumer.AcknowledgedCount == 1));
        await consumer.DisposeAsync();
        await using var connection = await fixture.Factory.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        Assert.Null(await channel.BasicGetAsync(queue, false));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PoisonOrExhaustedTransientMovesToConfirmedErrorQueue(bool poison)
    {
        var queue = await fixture.QueueAsync();
        var message = Message();
        await using var publisher = new ConfirmedPublisher(fixture.Factory);
        var calls = 0;
        await using var consumer = await ManualAckConsumer.StartAsync(fixture.Factory, publisher, queue,
            new HashSet<string> { "ProofV1" }, (_, _) =>
            {
                Interlocked.Increment(ref calls);
                throw new DeliveryFailureException(FailureKind.Transient, "DEPENDENCY_DOWN");
            });
        await publisher.PublishRawAsync("ride.commands", queue, MessageProperties.Create(message),
            JsonSerializer.SerializeToUtf8Bytes(poison ? message with { SchemaVersion = 99 } : message));
        await using var connection = await fixture.Factory.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        BasicGetResult? dead = null;
        await Eventually(async () => (dead = await channel.BasicGetAsync(queue + ".dead", false)) is not null);
        var error = JsonSerializer.Deserialize<MessageEnvelope<DeliveryError>>(dead!.Body.Span)!;
        Assert.Equal(message.MessageId, error.Payload.OriginalMessageId);
        Assert.Equal(poison ? FailureKind.Poison : FailureKind.Transient, error.Payload.Kind);
        Assert.Equal(poison ? 0 : 4, calls);
        await Eventually(() => Task.FromResult(consumer.AcknowledgedCount == (poison ? 1 : 4)));
        await channel.BasicAckAsync(dead.DeliveryTag, false);
    }

    [Fact]
    public async Task ConnectionLossFailsPublicationAndNextExplicitCallReconnects()
    {
        var queue = await fixture.QueueAsync();
        await using var publisher = new ConfirmedPublisher(fixture.Factory);
        await publisher.PublishAsync("ride.commands", queue, Message());
        // Restart the broker application while retaining the same container endpoint.
        // Docker can assign a different host port after a container stop/start.
        var stopped = await fixture.Broker.ExecAsync(new[] { "rabbitmqctl", "stop_app" });
        Assert.Equal(0, stopped.ExitCode);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAnyAsync<Exception>(() => publisher.PublishAsync("ride.commands", queue, Message(), timeout.Token));
        }
        finally
        {
            var started = await fixture.Broker.ExecAsync(new[] { "rabbitmqctl", "start_app" });
            Assert.Equal(0, started.ExitCode);
        }
        await Eventually(async () =>
        {
            try { await using var probe = await fixture.Factory.CreateConnectionAsync(); return probe.IsOpen; }
            catch (BrokerUnreachableException) { return false; }
        });
        // New explicit call, not an automatic replay of the uncertain publication.
        await publisher.PublishAsync("ride.commands", queue, Message());
    }

    [Fact]
    public async Task BrokerCancellationFaultsCompletionForHostSupervision()
    {
        var queue = await fixture.QueueAsync();
        await using var publisher = new ConfirmedPublisher(fixture.Factory);
        await using var consumer = await ManualAckConsumer.StartAsync(fixture.Factory, publisher, queue,
            new HashSet<string> { "ProofV1" }, (_, _) => Task.CompletedTask);
        await using var connection = await fixture.Factory.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        await channel.QueueDeleteAsync(queue, false, false);
        await Assert.ThrowsAsync<IOException>(() => consumer.Completion.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.Equal(0, consumer.AcknowledgedCount);
    }

    [Fact]
    public async Task FailedErrorTransferLeavesOriginalUnacknowledgedForRedelivery()
    {
        var queue = await fixture.QueueAsync();
        var message = Message();
        await using var connection = await fixture.Factory.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        await channel.QueueDeleteAsync(queue + ".dead", false, false);
        await using var publisher = new ConfirmedPublisher(fixture.Factory);
        await using var consumer = await ManualAckConsumer.StartAsync(fixture.Factory, publisher, queue,
            new HashSet<string> { "ProofV1" }, (_, _) => throw new DeliveryFailureException(FailureKind.Poison, "POISON"));
        await publisher.PublishAsync("ride.commands", queue, message);
        await Assert.ThrowsAsync<PublishReturnException>(() => consumer.Completion.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.Equal(0, consumer.AcknowledgedCount);
        await consumer.DisposeAsync();
        BasicGetResult? redelivery = null;
        await Eventually(async () => (redelivery = await channel.BasicGetAsync(queue, false)) is not null);
        Assert.True(redelivery!.Redelivered);
        Assert.Equal(message.MessageId, redelivery.BasicProperties.MessageId);
        await channel.BasicAckAsync(redelivery.DeliveryTag, false);
    }

}
