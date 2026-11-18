// Durable-intent publication proof on REAL PostgreSQL + REAL RabbitMQ. The
// shared OutboxRelay is used unchanged. The terminal row commits before any
// broker activity; a crash after commit or after the publisher confirm must
// replay the SAME message identity, never mint a new logical result or a
// second intent.
using System.Text.Json;
using Npgsql;
using RabbitMQ.Client;
using RideHailing.MatchingService.Application.Messaging;
using RideHailing.MatchingService.Application.Reserve;
using RideHailing.MatchingService.Infrastructure.Persistence;
using RideHailing.Messaging.Contracts;
using RideHailing.Messaging.Outbox;
using RideHailing.Messaging.Reliability;
using RideHailing.Messaging.Topology;

namespace RideHailing.MatchingService.IntegrationTests;

[Collection(BrokerWorkflowCollection.Name)]
public sealed class MatchingOutboxRelayTests
{
    private const string TripOutcomeQueue = "trip.matching-outcome.q";

    private readonly MatchingPostgresDatabase _pg;
    private readonly RabbitMqDatabase _rabbit;

    public MatchingOutboxRelayTests(MatchingPostgresDatabase pg, RabbitMqDatabase rabbit)
    {
        _pg = pg;
        _rabbit = rabbit;
    }

    private MatchingAttemptRepository Attempts() => new(_pg.ConnectionString);

    private MatchingTerminalizer Terminalizer() =>
        new(_pg.DataSource, _pg.OutboxStore);

    private ConnectionFactory Factory() => new()
    {
        Uri = new Uri(_rabbit.ConnectionString),
        ClientProvidedName = "matching-w9-outbox-proof",
        AutomaticRecoveryEnabled = false,
    };

    private static async Task EventuallyAsync(Func<Task<bool>> condition, string what)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            while (!await condition())
            {
                await Task.Delay(50, timeout.Token);
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            Assert.Fail($"Timed out waiting for: {what}");
        }
    }

    private async Task ResetAsync()
    {
        await using (var conn = new NpgsqlConnection(_pg.ConnectionString))
        {
            await conn.OpenAsync();
            await using var delete = new NpgsqlCommand("DELETE FROM outbox_messages", conn);
            await delete.ExecuteNonQueryAsync();
        }

        await using var connection = await Factory().CreateConnectionAsync();
        await RideTopology.DeclareAsync(connection);
        await using var channel = await connection.CreateChannelAsync();
        await channel.QueuePurgeAsync(TripOutcomeQueue);
    }

    private async Task<(Guid Trip, string InputMessageId, string Identity)> TerminalizeAsync()
    {
        var trip = Guid.NewGuid();
        var inputMessageId = Guid.NewGuid().ToString("D");
        var repo = Attempts();
        await repo.EnsureRoundAsync(trip, 1, inputMessageId);
        var ensured = await repo.EnsureInflightAttemptAsync(
            trip, 1, 0, Guid.NewGuid(), Guid.NewGuid());
        Assert.NotNull(ensured.Row);
        Assert.True(await repo.TryTransitionAsync(
            trip, 1, 0, [MatchingAttemptStates.InFlight],
            MatchingAttemptStates.Rejected, "TEST_REJECTED"));

        var terminal = await Terminalizer().TryTerminalizeNoDriverAsync(trip, 1);
        Assert.Equal(MatchingTerminalOutcome.NoDriver, terminal.Outcome);
        return (trip, inputMessageId, terminal.ResultMessageId!);
    }

    private OutboxRelay Relay(
        ConfirmedPublisher publisher,
        string relayId,
        Func<OutboxMessage, CancellationToken, Task>? beforePublish = null,
        Func<OutboxMessage, CancellationToken, Task>? afterPublishBeforeMark = null) =>
        new(_pg.OutboxStore, new RabbitOutboxPublisher(publisher),
            new OutboxRelayOptions
            {
                RelayId = relayId,
                BatchSize = 8,
                ClaimLease = TimeSpan.Zero,
                RetryDelay = TimeSpan.Zero,
                BeforePublish = beforePublish,
                AfterPublishBeforeMark = afterPublishBeforeMark,
            });

    private async Task<ManualAckConsumer> StartConsumerAsync(
        ConfirmedPublisher publisher, List<MessageEnvelope<JsonElement>> captured)
    {
        return await ManualAckConsumer.StartAsync(
            Factory(), publisher, TripOutcomeQueue,
            new HashSet<string>(StringComparer.Ordinal) { MatchingEventTypes.MatchingFailed },
            (delivered, _) =>
            {
                lock (captured)
                {
                    captured.Add(delivered);
                }

                return Task.CompletedTask;
            },
            prefetch: 8);
    }

    [Fact]
    public async Task TerminalCommit_ThenRelayPublishesPersistedIdentity()
    {
        await ResetAsync();
        var (trip, inputMessageId, identity) = await TerminalizeAsync();

        var captured = new List<MessageEnvelope<JsonElement>>();
        await using var publisher = new ConfirmedPublisher(Factory());
        await using var consumer = await StartConsumerAsync(publisher, captured);
        var relay = Relay(publisher, "relay-baseline");

        Assert.Equal(1, await relay.RunOnceAsync());
        await EventuallyAsync(() => Task.FromResult(consumer.AcknowledgedCount == 1),
            "terminal event delivery");

        var delivered = Assert.Single(captured);
        Assert.Equal(identity, delivered.MessageId);
        Assert.Equal(MatchingEventTypes.MatchingFailed, delivered.MessageType);
        Assert.Equal(inputMessageId, delivered.CorrelationId);
        Assert.Equal(trip, delivered.Payload.GetProperty("tripId").GetGuid());
        Assert.Equal(1, delivered.Payload.GetProperty("matchingRound").GetInt32());

        var stored = await _pg.OutboxStore.LoadAsync(identity);
        Assert.NotNull(stored);
        Assert.Equal(OutboxStatus.Published, stored!.Status);
        Assert.NotNull(stored.PublishedAt);
        var round = await Attempts().LoadRoundAsync(trip, 1);
        Assert.Equal(MatchingRoundStatuses.NoDriver, round!.Status);
        Assert.Equal(Guid.Parse(identity), round.ResultMessageId);
    }

    [Fact]
    public async Task CrashAfterCommitBeforePublish_RestartPublishesSameIdentity()
    {
        await ResetAsync();
        var (trip, _, identity) = await TerminalizeAsync();

        await using var publisher = new ConfirmedPublisher(Factory());
        var crashed = Relay(publisher, "relay-crashed",
            beforePublish: (_, _) => Task.FromException(new SimulatedCrashException()));
        await Assert.ThrowsAsync<SimulatedCrashException>(() => crashed.RunOnceAsync());

        var pending = await _pg.OutboxStore.LoadAsync(identity);
        Assert.NotNull(pending);
        Assert.Equal(OutboxStatus.Pending, pending!.Status);
        Assert.Equal("relay-crashed", pending.ClaimedBy);
        Assert.Null(pending.PublishedAt);

        var captured = new List<MessageEnvelope<JsonElement>>();
        await using var consumer = await StartConsumerAsync(publisher, captured);
        var restarted = Relay(publisher, "relay-restarted");
        Assert.Equal(1, await restarted.RunOnceAsync());

        await EventuallyAsync(() => Task.FromResult(consumer.AcknowledgedCount == 1),
            "restarted relay delivery");
        Assert.Equal(identity, Assert.Single(captured).MessageId);

        var final = await _pg.OutboxStore.LoadAsync(identity);
        Assert.Equal(OutboxStatus.Published, final!.Status);
        var round = await Attempts().LoadRoundAsync(trip, 1);
        Assert.Equal(MatchingRoundStatuses.NoDriver, round!.Status);
        Assert.Equal(Guid.Parse(identity), round.ResultMessageId);
        await AssertOutboxRowCountAsync(identity, 1);
    }

    [Fact]
    public async Task CrashAfterPublishBeforeMark_RestartRepublishesSameIdentity()
    {
        await ResetAsync();
        var (_, _, identity) = await TerminalizeAsync();

        var captured = new List<MessageEnvelope<JsonElement>>();
        await using var publisher = new ConfirmedPublisher(Factory());
        await using var consumer = await StartConsumerAsync(publisher, captured);

        var lostMark = Relay(publisher, "relay-lost-mark",
            afterPublishBeforeMark: (_, _) => Task.FromException(new SimulatedCrashException()));
        await Assert.ThrowsAsync<SimulatedCrashException>(() => lostMark.RunOnceAsync());

        await EventuallyAsync(() => Task.FromResult(consumer.AcknowledgedCount == 1),
            "first terminal delivery");
        var stillPending = await _pg.OutboxStore.LoadAsync(identity);
        Assert.NotNull(stillPending);
        Assert.Equal(OutboxStatus.Pending, stillPending!.Status);
        Assert.Null(stillPending.PublishedAt);

        var restarted = Relay(publisher, "relay-after-lost-mark");
        Assert.Equal(1, await restarted.RunOnceAsync());

        await EventuallyAsync(() => Task.FromResult(consumer.AcknowledgedCount == 2),
            "same-identity redelivery");
        List<MessageEnvelope<JsonElement>> deliveries;
        lock (captured)
        {
            deliveries = captured.ToList();
        }

        Assert.Equal(2, deliveries.Count);
        Assert.All(deliveries, d => Assert.Equal(identity, d.MessageId));
        Assert.All(deliveries, d => Assert.Equal(MatchingEventTypes.MatchingFailed, d.MessageType));

        var final = await _pg.OutboxStore.LoadAsync(identity);
        Assert.Equal(OutboxStatus.Published, final!.Status);
        await AssertOutboxRowCountAsync(identity, 1);
    }

    private async Task AssertOutboxRowCountAsync(string identity, long expected)
    {
        await using var conn = new NpgsqlConnection(_pg.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM outbox_messages WHERE message_id = @id", conn);
        cmd.Parameters.AddWithValue("id", identity);
        Assert.Equal(expected, (long)(await cmd.ExecuteScalarAsync())!);
    }

    private sealed class SimulatedCrashException : Exception;
}
