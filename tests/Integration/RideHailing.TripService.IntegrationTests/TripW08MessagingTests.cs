using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using RabbitMQ.Client;
using RideHailing.Messaging.Contracts;
using RideHailing.Messaging.Reliability;
using RideHailing.Messaging.Topology;
using RideHailing.TripService.Domain.Trips;
using RideHailing.TripService.Infrastructure.Persistence;
using RideHailing.TripService.Integration.Messaging;

namespace RideHailing.TripService.IntegrationTests;

public sealed class TripRabbitFixture : IAsyncLifetime
{
    public IContainer Broker { get; } = new ContainerBuilder("rabbitmq:4.3.6-management")
        .WithEnvironment("RABBITMQ_DEFAULT_USER", "trip-proof")
        .WithEnvironment("RABBITMQ_DEFAULT_PASS", "disposable-test-only")
        .WithPortBinding(5672, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Server startup complete"))
        .Build();
    public ConnectionFactory Factory => new()
    {
        HostName = Broker.Hostname, Port = Broker.GetMappedPublicPort(5672),
        UserName = "trip-proof", Password = "disposable-test-only", AutomaticRecoveryEnabled = false
    };
    public async Task InitializeAsync()
    {
        await Broker.StartAsync();
        await using var connection = await Factory.CreateConnectionAsync();
        await RideTopology.DeclareAsync(connection);
    }
    public async Task DisposeAsync() => await Broker.DisposeAsync();
}

[Collection(TripPostgresCollection.Name)]
public sealed class TripW08MessagingTests(TripPostgresFixture postgres, TripRabbitFixture rabbit)
    : IClassFixture<TripRabbitFixture>
{
    private const string DriverQueue = "trip.driver-state.q";
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 1, 0, 0, TimeSpan.Zero);
    private TripDbContext Db(params IInterceptor[] interceptors) => new(
        new DbContextOptionsBuilder<TripDbContext>().UseNpgsql(postgres.ConnectionString)
            .AddInterceptors(interceptors).Options);
    private async Task<Trip> Seed(bool cancel = false)
    {
        await using var db = Db();
        await db.Database.MigrateAsync();
        var trip = Trip.Create(Guid.NewGuid(), Guid.NewGuid(), new(10.7, 106.6), new(10.8, 106.7),
            new("VND", 50000m, "w8", Now), Now);
        trip.StartMatching(Now);
        if (cancel) trip.Cancel(Now);
        db.Trips.Add(trip);
        await db.SaveChangesAsync();
        return trip;
    }
    private async Task Deliver(string queue, MessageEnvelope<JsonElement> e, CancellationToken token = default)
    {
        await using var db = Db();
        await new TripMessageHandler(db, TimeProvider.System, NullLogger<TripMessageHandler>.Instance)
            .HandleAsync(queue, e, token);
    }
    private static MessageEnvelope<JsonElement> Reserved(Trip trip, Guid? driver = null, Guid? reservation = null, int round = 1) =>
        MessageEnvelope<JsonElement>.Create("DriverReservedV1", trip.TripId.ToString("D"),
            JsonSerializer.SerializeToElement(new
            { tripId = trip.TripId, driverId = driver ?? Guid.NewGuid(), reservationId = reservation ?? Guid.NewGuid(), matchingRound = round }));
    private static async Task Eventually(Func<Task<bool>> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!await predicate()) await Task.Delay(50, timeout.Token);
    }
    private Task<ManualAckConsumer> Consumer(ConfirmedPublisher publisher, string queue = DriverQueue) =>
        ManualAckConsumer.StartAsync(rabbit.Factory, publisher, queue, TripMessageHandler.Subscriptions[queue],
            (e, token) => Deliver(queue, e, token), prefetch: 1);

    [Fact]
    public async Task Worker_DoesNotConsumeUntilReceiptSchemaIsReady()
    {
        var name = "w8_unmigrated_" + Guid.NewGuid().ToString("N");
        await using var admin = new NpgsqlConnection(postgres.ConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin))
            await create.ExecuteNonQueryAsync();
        var cs = new NpgsqlConnectionStringBuilder(postgres.ConnectionString) { Database = name, Pooling = false }.ToString();
        try
        {
            using var app = new TripApplicationFactory(cs).WithWebHostBuilder(builder =>
            {
                builder.UseSetting("TripMessaging:Enabled", "true");
                builder.UseSetting("RabbitMq:Host", rabbit.Factory.HostName);
                builder.UseSetting("RabbitMq:Port", rabbit.Factory.Port.ToString());
                builder.UseSetting("RabbitMq:Username", rabbit.Factory.UserName);
                builder.UseSetting("RabbitMq:Password", rabbit.Factory.Password);
            });
            using var client = app.CreateClient();
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready")).StatusCode);
            await using var connection = await rabbit.Factory.CreateConnectionAsync();
            await using var channel = await connection.CreateChannelAsync();
            Assert.Equal(0u, await channel.ConsumerCountAsync(DriverQueue));
            var e = MessageEnvelope<JsonElement>.Create("DriverReservedV1", Guid.NewGuid().ToString(),
                JsonSerializer.SerializeToElement(new { tripId = Guid.NewGuid(), driverId = Guid.NewGuid(),
                    reservationId = Guid.NewGuid(), matchingRound = 1 }));
            await using var publisher = new ConfirmedPublisher(rabbit.Factory);
            await publisher.PublishAsync(RideTopology.EventsExchange, "driver.reserved.v1", e);
            Assert.Equal(1u, await channel.MessageCountAsync(DriverQueue));
            await using var db = new TripDbContext(new DbContextOptionsBuilder<TripDbContext>().UseNpgsql(cs).Options);
            await db.Database.MigrateAsync();
            await Eventually(async () => (await client.GetAsync("/health/ready")).IsSuccessStatusCode);
            await Eventually(() => db.MessageReceipts.AnyAsync(r => r.MessageId == e.MessageId));
            await Eventually(async () => await channel.MessageCountAsync(DriverQueue) == 0);
            Assert.Equal("TRIP_NOT_FOUND", (await db.MessageReceipts.SingleAsync(r => r.MessageId == e.MessageId)).Reason);
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE \"{name}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task DriverRepublish_WithNewDiagnosticMetadata_ReplaysLegacyReceipt()
    {
        var trip = await Seed();
        var driver = Guid.NewGuid();
        var e = MessageEnvelope<JsonElement>.Create("DriverReservedV1", trip.TripId.ToString(),
            JsonSerializer.SerializeToElement(new { tripId = trip.TripId, driverId = driver,
                reservationId = Guid.NewGuid(), matchingRound = 1, status = "RESERVED", version = 2 }),
            aggregateType: "driver", aggregateId: driver.ToString("N"), aggregateVersion: 2);
        await Deliver(DriverQueue, e);
        // Old deployed rows used a hash of the full envelope. Evidence remains
        // authoritative across an upgrade without rewriting/deleting receipts.
        await using (var legacy = Db())
        {
            var receipt = await legacy.MessageReceipts.SingleAsync(r => r.MessageId == e.MessageId);
            receipt.Fingerprint = new string('0', 64);
            await legacy.SaveChangesAsync();
        }
        await Deliver(DriverQueue, e with { CorrelationId = Guid.NewGuid().ToString(),
            CausationId = "republished", OccurredAt = e.OccurredAt.AddMinutes(1) });
        await using var db = Db();
        Assert.Equal(trip.Version + 1, (await db.Trips.SingleAsync(t => t.TripId == trip.TripId)).Version);
        Assert.Equal(1, await db.MessageReceipts.CountAsync(r => r.MessageId == e.MessageId));
    }

    [Fact]
    public async Task TerminalReplay_DoesNotRequireBroker_AndStillChecksOwnership()
    {
        var trip = await Seed(cancel: true);
        await using var db = Db();
        await using var services = new ServiceCollection()
            .AddSingleton(new TripMessagingReadiness()).BuildServiceProvider();
        var publisher = new TripRideRequestedPublisher(db, new TripMessagingSettings(true), services,
            TimeProvider.System, NullLogger<TripRideRequestedPublisher>.Instance);
        Assert.True(await publisher.PublishForOwnedRideAsync(trip.TripId, trip.RiderId, default));
        Assert.False(await publisher.PublishForOwnedRideAsync(trip.TripId, Guid.NewGuid(), default));
        var current = await db.Trips.SingleAsync(t => t.TripId == trip.TripId);
        Assert.Equal(trip.Version, current.Version);
        Assert.Equal(TripState.Cancelled, current.State);
    }

    [Fact]
    public async Task UnknownTrip_BrokerDeliveryRetainsOriginalPayloadBeforeAck()
    {
        await using (var setup = Db()) await setup.Database.MigrateAsync();
        var trip = Trip.Create(Guid.NewGuid(), Guid.NewGuid(), new(10.7, 106.6), new(10.8, 106.7),
            new("VND", 50000m, "w8-review", Now), Now);
        var e = Reserved(trip);
        await using var publisher = new ConfirmedPublisher(rabbit.Factory);
        await using var consumer = await Consumer(publisher);
        await publisher.PublishAsync(RideTopology.EventsExchange, "driver.reserved.v1", e);
        await publisher.PublishAsync(RideTopology.EventsExchange, "driver.reserved.v1", e);
        await Eventually(() => Task.FromResult(consumer.AcknowledgedCount == 2));
        await using var db = Db();
        Assert.False(await db.Trips.AnyAsync(t => t.TripId == trip.TripId));
        var receipt = Assert.Single(await db.MessageReceipts.Where(r => r.TripId == trip.TripId).ToListAsync());
        Assert.Equal("PendingWorkflow", receipt.Disposition);
        Assert.Equal("TRIP_NOT_FOUND", receipt.Reason);
        using var stored = JsonDocument.Parse(receipt.EnvelopeJson);
        Assert.Equal(e.Payload.GetProperty("reservationId").GetGuid(),
            stored.RootElement.GetProperty("payload").GetProperty("reservationId").GetGuid());
    }

    [Theory]
    [InlineData("status")]
    [InlineData("aggregate")]
    [InlineData("version")]
    public async Task ContradictoryDriverMetadata_IsRejectedBeforeReceiptOrEffect(string conflict)
    {
        var trip = await Seed();
        var driver = Guid.NewGuid();
        var e = MessageEnvelope<JsonElement>.Create("DriverReservedV1", trip.TripId.ToString(),
            JsonSerializer.SerializeToElement(new
            {
                tripId = trip.TripId, driverId = driver, reservationId = Guid.NewGuid(), matchingRound = 1,
                status = conflict == "status" ? "AVAILABLE" : "RESERVED", version = 5
            }), aggregateType: "Driver", aggregateId: (conflict == "aggregate" ? Guid.NewGuid() : driver).ToString(),
            aggregateVersion: conflict == "version" ? 6 : 5);
        var error = await Assert.ThrowsAsync<DeliveryFailureException>(() => Deliver(DriverQueue, e));
        Assert.Equal(FailureKind.Poison, error.Kind);
        await using var db = Db();
        Assert.False(await db.MessageReceipts.AnyAsync(r => r.MessageId == e.MessageId));
        Assert.Equal(trip.Version, (await db.Trips.SingleAsync(t => t.TripId == trip.TripId)).Version);
    }

    [Fact]
    public async Task MatchFailed_LateReservationThenExactRelease_ConvergesThroughBroker()
    {
        var trip = await Seed();
        await using (var db = Db())
        {
            var saved = await db.Trips.SingleAsync(t => t.TripId == trip.TripId);
            Assert.True(saved.MarkMatchingFailed(1, Now).IsSuccess);
            await db.SaveChangesAsync();
        }
        var reserved = Reserved(trip);
        await using var publisher = new ConfirmedPublisher(rabbit.Factory);
        await using var consumer = await Consumer(publisher);
        await publisher.PublishAsync(RideTopology.EventsExchange, "driver.reserved.v1", reserved);
        await Eventually(() => Task.FromResult(consumer.AcknowledgedCount == 1));
        var released = reserved with { MessageId = Guid.NewGuid().ToString(), MessageType = "DriverReservationReleasedV1" };
        await publisher.PublishAsync(RideTopology.EventsExchange, "driver.reservation-released.v1", released);
        await Eventually(() => Task.FromResult(consumer.AcknowledgedCount == 2));
        await using var final = Db();
        var tripAfter = await final.Trips.SingleAsync(t => t.TripId == trip.TripId);
        Assert.Equal(TripState.MatchFailed, tripAfter.State);
        Assert.Null(tripAfter.Assignment);
        Assert.NotNull(Assert.Single(tripAfter.CleanupIntents).CompletedAt);
    }

    [Fact]
    public async Task HttpCreate_PublishesStableRideRequested_AndMatchingStyleCommitPrecedesAck()
    {
        await using (var db = Db())
        {
            await db.Database.MigrateAsync();
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS w8_matching_style_receipts
                (message_id text PRIMARY KEY, trip_id uuid NOT NULL, round integer NOT NULL, body jsonb NOT NULL)
                """);
        }
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "RideHailing.Messaging",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(listener);
        using var root = new Activity("trip-http-w8").SetIdFormat(ActivityIdFormat.W3C).Start();
        using var app = new TripApplicationFactory(postgres.ConnectionString).WithWebHostBuilder(builder =>
        {
            builder.UseSetting("TripMessaging:Enabled", "true");
            builder.UseSetting("RabbitMq:Host", rabbit.Factory.HostName);
            builder.UseSetting("RabbitMq:Port", rabbit.Factory.Port.ToString());
            builder.UseSetting("RabbitMq:Username", rabbit.Factory.UserName);
            builder.UseSetting("RabbitMq:Password", rabbit.Factory.Password);
            builder.ConfigureTestServices(s => s.AddTransient<IStartupFilter, TestPrincipalStartupFilter>());
        });
        using var client = app.CreateClient();
        await Eventually(async () => (await client.GetAsync("/health/ready")).IsSuccessStatusCode);
        await using var publisher = new ConfirmedPublisher(rabbit.Factory);
        var observed = new List<(string Id, string Json, string Correlation, ActivityTraceId Trace)>();
        await using var matching = await ManualAckConsumer.StartAsync(rabbit.Factory, publisher,
            "matching.ride-requested.q", new HashSet<string> { "RideRequestedV1" }, async (e, token) =>
            {
                await using var connection = new NpgsqlConnection(postgres.ConnectionString);
                await connection.OpenAsync(token);
                await using var command = new NpgsqlCommand("""
                    INSERT INTO w8_matching_style_receipts VALUES (@id, @trip, @round, CAST(@body AS jsonb))
                    ON CONFLICT (message_id) DO NOTHING
                    """, connection);
                command.Parameters.AddWithValue("id", e.MessageId);
                command.Parameters.AddWithValue("trip", e.Payload.GetProperty("tripId").GetGuid());
                command.Parameters.AddWithValue("round", e.Payload.GetProperty("matchingRound").GetInt32());
                command.Parameters.AddWithValue("body", JsonSerializer.Serialize(e));
                await command.ExecuteNonQueryAsync(token);
                lock (observed) observed.Add((e.MessageId, JsonSerializer.Serialize(e), e.CorrelationId, Activity.Current!.TraceId));
            });
        var rider = Guid.NewGuid();
        var key = Guid.NewGuid().ToString();
        HttpRequestMessage Request()
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/trips")
            {
                Content = JsonContent.Create(new { pickup = new { latitude = 10.7, longitude = 106.6 },
                    dropoff = new { latitude = 10.8, longitude = 106.7 } })
            };
            request.Headers.Add(TestPrincipalStartupFilter.NameIdentifierHeader, rider.ToString());
            request.Headers.Add(TestPrincipalStartupFilter.RoleHeader, "Rider");
            request.Headers.Add("Idempotency-Key", key);
            // TestServer's in-memory HttpClient does not inject distributed
            // tracing headers as a network HttpClient handler would.
            request.Headers.TryAddWithoutValidation("traceparent", root.Id);
            return request;
        }
        using var firstRequest = Request();
        using var first = await client.SendAsync(firstRequest);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var tripId = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("tripId").GetGuid();
        using var secondRequest = Request();
        using var second = await client.SendAsync(secondRequest);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        await Eventually(() => Task.FromResult(matching.AcknowledgedCount == 2));
        lock (observed)
        {
            Assert.Equal(2, observed.Count);
            Assert.Equal(observed[0].Id, observed[1].Id);
            Assert.Equal(observed[0].Json, observed[1].Json);
            Assert.Equal(tripId.ToString("D"), observed[0].Correlation);
            Assert.Equal(root.TraceId, observed[0].Trace);
        }
        await using var verify = Db();
        Assert.Equal(TripState.Matching, (await verify.Trips.SingleAsync(t => t.TripId == tripId)).State);
        await using var countConnection = new NpgsqlConnection(postgres.ConnectionString);
        await countConnection.OpenAsync();
        await using var count = new NpgsqlCommand("SELECT count(*) FROM w8_matching_style_receipts WHERE trip_id=@trip", countConnection);
        count.Parameters.AddWithValue("trip", tripId);
        Assert.Equal(1L, await count.ExecuteScalarAsync());
    }

    [Fact]
    public async Task BrokerReconnect_ReadinessGatesPublication_AndSameHttpKeyRecoversSavedRide()
    {
        await using (var db = Db()) await db.Database.MigrateAsync();
        using var app = new TripApplicationFactory(postgres.ConnectionString).WithWebHostBuilder(builder =>
        {
            builder.UseSetting("TripMessaging:Enabled", "true");
            builder.UseSetting("RabbitMq:Host", rabbit.Factory.HostName);
            builder.UseSetting("RabbitMq:Port", rabbit.Factory.Port.ToString());
            builder.UseSetting("RabbitMq:Username", rabbit.Factory.UserName);
            builder.UseSetting("RabbitMq:Password", rabbit.Factory.Password);
            builder.ConfigureTestServices(s => s.AddTransient<IStartupFilter, TestPrincipalStartupFilter>());
        });
        using var client = app.CreateClient();
        await Eventually(async () => (await client.GetAsync("/health/ready")).IsSuccessStatusCode);
        var rider = Guid.NewGuid();
        var key = Guid.NewGuid().ToString();
        HttpRequestMessage Request()
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/trips")
            {
                Content = JsonContent.Create(new { pickup = new { latitude = 10.7, longitude = 106.6 },
                    dropoff = new { latitude = 10.8, longitude = 106.7 } })
            };
            request.Headers.Add(TestPrincipalStartupFilter.NameIdentifierHeader, rider.ToString());
            request.Headers.Add(TestPrincipalStartupFilter.RoleHeader, "Rider");
            request.Headers.Add("Idempotency-Key", key);
            return request;
        }
        Guid tripId;
        try
        {
            Assert.Equal(0, (await rabbit.Broker.ExecAsync(["rabbitmqctl", "stop_app"])).ExitCode);
            await Eventually(async () => (await client.GetAsync("/health/ready")).StatusCode == HttpStatusCode.ServiceUnavailable);
            using var request = Request();
            using var saved = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, saved.StatusCode);
            var problem = await saved.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("MATCHING_PUBLICATION_PENDING", problem.GetProperty("code").GetString());
            tripId = problem.GetProperty("tripId").GetGuid();
            await using var verify = Db();
            Assert.Equal(TripState.Created, (await verify.Trips.SingleAsync(t => t.TripId == tripId)).State);
        }
        finally { Assert.Equal(0, (await rabbit.Broker.ExecAsync(["rabbitmqctl", "start_app"])).ExitCode); }
        await Eventually(async () => (await client.GetAsync("/health/ready")).IsSuccessStatusCode);
        using var retryRequest = Request();
        using var retry = await client.SendAsync(retryRequest);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(tripId, (await retry.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("tripId").GetGuid());
        await using var final = Db();
        Assert.Equal(1, await final.Trips.CountAsync(t => t.RiderId == rider));
        Assert.Equal(TripState.Matching, (await final.Trips.SingleAsync(t => t.TripId == tripId)).State);
        // Consume this test's publication so the shared broker has no leftover
        // RideRequested delivery for the independent HTTP trace test.
        await using var connection = await rabbit.Factory.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        var delivery = await channel.BasicGetAsync("matching.ride-requested.q", autoAck: false);
        Assert.NotNull(delivery);
        Assert.Equal(TripRideRequestedPublisher.CreateEnvelope(await final.Trips.SingleAsync(t => t.TripId == tripId)).MessageId,
            delivery.BasicProperties.MessageId);
        await channel.BasicAckAsync(delivery.DeliveryTag, false);
    }

    [Fact]
    public async Task BrokerDuplicates_AndNewMessageIdReplay_ApplyOnlyOnce_ThenRejectStaleRound()
    {
        var trip = await Seed();
        var e = Reserved(trip);
        await using var publisher = new ConfirmedPublisher(rabbit.Factory);
        await using var consumer = await Consumer(publisher);
        await publisher.PublishAsync(RideTopology.EventsExchange, "driver.reserved.v1", e);
        await publisher.PublishAsync(RideTopology.EventsExchange, "driver.reserved.v1", e);
        await publisher.PublishAsync(RideTopology.EventsExchange, "driver.reserved.v1", e with { MessageId = Guid.NewGuid().ToString() });
        var stale = Reserved(trip, round: 2);
        await publisher.PublishAsync(RideTopology.EventsExchange, "driver.reserved.v1", stale);
        await Eventually(() => Task.FromResult(consumer.AcknowledgedCount == 4));
        await using var db = Db();
        var saved = await db.Trips.SingleAsync(t => t.TripId == trip.TripId);
        Assert.Equal(trip.Version + 1, saved.Version);
        Assert.Equal(e.Payload.GetProperty("reservationId").GetGuid(), saved.Assignment!.ReservationId);
        var receipts = await db.MessageReceipts.Where(r => r.TripId == trip.TripId).ToListAsync();
        Assert.Equal(3, receipts.Count);
        Assert.Contains(receipts, r => r.Disposition == "BusinessReplay");
        Assert.Contains(receipts, r => r.MessageId == stale.MessageId && r.Reason == "StaleIdentity");
    }

    [Fact]
    public async Task CommitBeforeAck_ConnectionClose_RedeliveryDoesNotReapply()
    {
        var trip = await Seed();
        var e = Reserved(trip);
        await using var publisher = new ConfirmedPublisher(rabbit.Factory);
        var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var first = await ManualAckConsumer.StartAsync(rabbit.Factory, publisher, DriverQueue,
            TripMessageHandler.Subscriptions[DriverQueue], async (message, token) =>
            {
                await Deliver(DriverQueue, message, token);
                committed.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }, prefetch: 1);
        await publisher.PublishAsync(RideTopology.EventsExchange, "driver.reserved.v1", e);
        await committed.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(0, first.AcknowledgedCount);
        await first.DisposeAsync();
        await using var second = await Consumer(publisher);
        await Eventually(() => Task.FromResult(second.AcknowledgedCount == 1));
        await using var db = Db();
        Assert.Equal(trip.Version + 1, (await db.Trips.SingleAsync(t => t.TripId == trip.TripId)).Version);
        Assert.Equal(1, await db.MessageReceipts.CountAsync(r => r.TripId == trip.TripId));
    }

    private sealed class FailSave : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data,
            InterceptionResult<int> result, CancellationToken token = default) => throw new IOException("Injected before commit");
    }

    private sealed class TwoWriterBarrier : SaveChangesInterceptor
    {
        private int arrived;
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data,
            InterceptionResult<int> result, CancellationToken token = default)
        {
            if (Interlocked.Increment(ref arrived) == 2) release.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(15), token);
            return result;
        }
    }

    [Fact]
    public async Task ConcurrentLateReservationMessages_RetryLosingTransaction_WithOneCleanup()
    {
        var trip = await Seed(cancel: true);
        var first = Reserved(trip);
        var second = first with { MessageId = Guid.NewGuid().ToString() };
        var barrier = new TwoWriterBarrier();
        async Task<Exception?> Attempt(MessageEnvelope<JsonElement> e)
        {
            await using var db = Db(barrier);
            return await Record.ExceptionAsync(() =>
                new TripMessageHandler(db, TimeProvider.System, NullLogger<TripMessageHandler>.Instance)
                    .HandleAsync(DriverQueue, e, default));
        }
        var errors = await Task.WhenAll(Attempt(first), Attempt(second));
        Assert.Single(errors, e => e is null);
        var conflict = Assert.IsType<DeliveryFailureException>(Assert.Single(errors, e => e is not null));
        Assert.Equal(FailureKind.Transient, conflict.Kind);
        await Deliver(DriverQueue, first);
        await Deliver(DriverQueue, second);
        await using var verify = Db();
        var saved = await verify.Trips.SingleAsync(t => t.TripId == trip.TripId);
        Assert.Equal(TripState.Cancelled, saved.State);
        Assert.Single(saved.CleanupIntents, i => i.Kind == TripCleanupKind.ReleaseReservation);
        Assert.Equal(trip.Version + 1, saved.Version);
        Assert.Equal(2, await verify.MessageReceipts.CountAsync(r => r.TripId == trip.TripId));
    }

    [Fact]
    public async Task FailureBeforeCommit_RollsBackReceiptAndEffect_ThenReplayApplies()
    {
        var trip = await Seed();
        var e = Reserved(trip);
        await using (var broken = Db(new FailSave()))
            await Assert.ThrowsAsync<IOException>(() =>
                new TripMessageHandler(broken, TimeProvider.System, NullLogger<TripMessageHandler>.Instance)
                    .HandleAsync(DriverQueue, e, default));
        await using (var verify = Db())
        {
            Assert.False(await verify.MessageReceipts.AnyAsync(r => r.MessageId == e.MessageId));
            Assert.Equal(TripState.Matching, (await verify.Trips.SingleAsync(t => t.TripId == trip.TripId)).State);
        }
        await Deliver(DriverQueue, e);
        await using var final = Db();
        Assert.Equal(TripState.DriverReserved, (await final.Trips.SingleAsync(t => t.TripId == trip.TripId)).State);
    }

    [Fact]
    public async Task ConcurrentSameMessage_OneDurableEffect_ChangedContentCannotReuseIdentity()
    {
        var trip = await Seed();
        var e = Reserved(trip);
        await Task.WhenAll(Deliver(DriverQueue, e), Deliver(DriverQueue, e));
        var conflict = Reserved(trip) with { MessageId = e.MessageId };
        var failure = await Assert.ThrowsAsync<DeliveryFailureException>(() => Deliver(DriverQueue, conflict));
        Assert.Equal("MESSAGE_ID_CONTENT_CONFLICT", failure.Code);
        await using var db = Db();
        Assert.Equal(trip.Version + 1, (await db.Trips.SingleAsync(t => t.TripId == trip.TripId)).Version);
        Assert.Equal(1, await db.MessageReceipts.CountAsync(r => r.TripId == trip.TripId));
    }

    [Fact]
    public async Task MissingRound_IsDurableReview_AndPaymentSkeletonNeverAuthorizesFromTripIdAlone()
    {
        var trip = await Seed();
        var missing = MessageEnvelope<JsonElement>.Create("DriverReservedV1", trip.TripId.ToString(),
            JsonSerializer.SerializeToElement(new { tripId = trip.TripId, driverId = Guid.NewGuid(), reservationId = Guid.NewGuid() }));
        await Deliver(DriverQueue, missing);
        var payment = MessageEnvelope<JsonElement>.Create("PaymentAuthorizedV1", trip.TripId.ToString(),
            JsonSerializer.SerializeToElement(new { tripId = trip.TripId }), operationKey: "unproven-operation");
        await Deliver("trip.payment-outcome.q", payment);
        await using var db = Db();
        Assert.Equal(trip.Version, (await db.Trips.SingleAsync(t => t.TripId == trip.TripId)).Version);
        Assert.Equal("RESERVATION_ROUND_REQUIRED", (await db.MessageReceipts.SingleAsync(r => r.MessageId == missing.MessageId)).Reason);
        var stored = await db.MessageReceipts.SingleAsync(r => r.MessageId == payment.MessageId);
        Assert.Equal("PendingWorkflow", stored.Disposition);
        Assert.Contains("unproven-operation", stored.EnvelopeJson);
    }

    [Fact]
    public async Task CancelledTrip_LateBrokerReservationAndPayment_PersistCleanupWithoutReopening()
    {
        var trip = await Seed(cancel: true);
        var reservation = Reserved(trip);
        var payment = MessageEnvelope<JsonElement>.Create("PaymentAuthorizedV1", trip.TripId.ToString(),
            JsonSerializer.SerializeToElement(new { tripId = trip.TripId }), operationKey: "late-operation");
        await using var publisher = new ConfirmedPublisher(rabbit.Factory);
        await using var driverConsumer = await Consumer(publisher);
        await using var paymentConsumer = await Consumer(publisher, "trip.payment-outcome.q");
        await publisher.PublishAsync(RideTopology.EventsExchange, "driver.reserved.v1", reservation);
        await Eventually(() => Task.FromResult(driverConsumer.AcknowledgedCount == 1));
        await publisher.PublishAsync(RideTopology.EventsExchange, "payment.authorized.v1", payment);
        await Eventually(() => Task.FromResult(paymentConsumer.AcknowledgedCount == 1));
        await using var db = Db();
        var saved = await db.Trips.SingleAsync(t => t.TripId == trip.TripId);
        Assert.Equal(TripState.Cancelled, saved.State);
        Assert.Null(saved.Assignment);
        Assert.Equal(3, saved.CleanupIntents.Count);
        Assert.Contains(saved.CleanupIntents, i => i.ReservationId == reservation.Payload.GetProperty("reservationId").GetGuid());
    }

    [Fact]
    public async Task MatchingOutcome_StaleGenerationDoesNotFailCurrentRide()
    {
        var trip = await Seed();
        await using var publisher = new ConfirmedPublisher(rabbit.Factory);
        await using var consumer = await Consumer(publisher, "trip.matching-outcome.q");
        MessageEnvelope<JsonElement> Failed(int round) => MessageEnvelope<JsonElement>.Create(
            "MatchingFailedV1", trip.TripId.ToString(), JsonSerializer.SerializeToElement(new { tripId = trip.TripId, matchingRound = round }));
        await publisher.PublishAsync(RideTopology.EventsExchange, "matching.failed.v1", Failed(2));
        await Eventually(() => Task.FromResult(consumer.AcknowledgedCount == 1));
        await using (var verify = Db())
            Assert.Equal(TripState.Matching, (await verify.Trips.SingleAsync(t => t.TripId == trip.TripId)).State);
        await publisher.PublishAsync(RideTopology.EventsExchange, "matching.failed.v1", Failed(1));
        await Eventually(() => Task.FromResult(consumer.AcknowledgedCount == 2));
        await using var final = Db();
        Assert.Equal(TripState.MatchFailed, (await final.Trips.SingleAsync(t => t.TripId == trip.TripId)).State);
        Assert.False(final.Database.HasPendingModelChanges());
    }
}
