// Asynchronous trace-propagation proof on the real broker.
//
// CHAIN UNDER TEST (one trace across two process roles):
//
//   HTTP POST /trips
//     -> RideHailing.Trip/CreateRide span (domain decision)
//     -> durable Outbox row whose headers_json carries that span's W3C
//        traceparent (EF persistence boundary)
//     -> relay "outbox.publish" producer span, re-parented from the DURABLE
//        headers, not from the relay's own execution context
//     -> RabbitMQ message headers (wire)
//     -> shared "consume" consumer span, re-parented from the DELIVERY
//        headers inside a real ManualAckConsumer
//
// The oracle is the emitted activities plus the durable row and the wire
// headers — a DTO-level traceparent string check is explicitly NOT the proof.
// Creation context vs relay execution context stay distinguished: the wire
// traceparent is the CREATION context; outbox.publish and consume are both
// children of it, siblings of each other.

using System.Net;
using System.Net.Http.Json;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using RabbitMQ.Client;
using RideHailing.Messaging.Contracts;
using RideHailing.Messaging.Reliability;
using RideHailing.Messaging.Topology;
using RideHailing.TripService.Api;
using RideHailing.TripService.Domain.Trips;
using RideHailing.TripService.Infrastructure.Persistence;
using RideHailing.TripService.Integration.Messaging;

namespace RideHailing.TripService.IntegrationTests;

[Collection(TripPostgresCollection.Name)]
public sealed class TripTracePropagationTests(TripPostgresFixture postgres, TripRabbitFixture rabbit)
    : IClassFixture<TripRabbitFixture>
{
    [Fact]
    public async Task CreateRideTrace_SurvivesOutboxPersistenceAndBrokerIntoConsumer()
    {
        var database = $"trip_w12_trace_{Guid.NewGuid():N}";
        var maintenance = new NpgsqlConnectionStringBuilder(postgres.ConnectionString)
            { Database = "postgres" }.ToString();
        await using (var admin = new NpgsqlConnection(maintenance))
        {
            await admin.OpenAsync();
            await using var create = admin.CreateCommand();
            create.CommandText = $"CREATE DATABASE \"{database}\"";
            await create.ExecuteNonQueryAsync();
        }

        var connectionString = new NpgsqlConnectionStringBuilder(postgres.ConnectionString)
            { Database = database }.ToString();
        // The HTTP create path needs the schema; the factory host does not
        // auto-migrate (production contract).
        await using (var setup = new TripDbContext(new DbContextOptionsBuilder<TripDbContext>()
                         .UseNpgsql(connectionString).Options))
        {
            await setup.Database.MigrateAsync();
        }

        var probeQueue = $"trace.probe.{Guid.NewGuid():N}";
        try
        {
            using var app = new TripApplicationFactory(connectionString).WithWebHostBuilder(builder =>
            {
                builder.ConfigureTestServices(services =>
                    services.AddTransient<IStartupFilter, TestPrincipalStartupFilter>());
                builder.UseSetting("TripMessaging:Enabled", "true");
                builder.UseSetting("RabbitMq:Host", rabbit.Factory.HostName);
                builder.UseSetting("RabbitMq:Port", rabbit.Factory.Port.ToString());
                builder.UseSetting("RabbitMq:Username", rabbit.Factory.UserName);
                builder.UseSetting("RabbitMq:Password", rabbit.Factory.Password);
                builder.UseSetting("Outbox:PollIntervalMs", "100");
            });
            using var client = app.CreateClient();

            using var activities = new ActivityRecorder();
            using var meter = new MeterRecorder();

            // Durable probe queue bound to the real topology (RabbitMQ 4.x
            // rejects transient non-exclusive queues); it is deleted in the
            // finally block. The production consumers do not subscribe to
            // the matching request, so this queue observes exactly the
            // relayed publication.
            await using var probeConnection = await rabbit.Factory.CreateConnectionAsync();
            await using var probeChannel = await probeConnection.CreateChannelAsync();
            await probeChannel.QueueDeclareAsync(probeQueue, durable: true, exclusive: false,
                autoDelete: false);
            await probeChannel.QueueBindAsync(probeQueue, RideTopology.EventsExchange, "trip.ride-requested.v1");

            var rider = Guid.NewGuid();
            var request = new HttpRequestMessage(HttpMethod.Post, TripRoutes.CreateRide)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    pickup = new { latitude = 10.762622, longitude = 106.660172 },
                    dropoff = new { latitude = 10.776889, longitude = 106.700806 },
                }), Encoding.UTF8, "application/json"),
            };
            request.Headers.Add(TripIdentityHeaders.IdempotencyKey, $"trace-{Guid.NewGuid():N}");
            request.Headers.Add(TestPrincipalStartupFilter.NameIdentifierHeader, rider.ToString("D"));
            request.Headers.Add(TestPrincipalStartupFilter.RoleHeader, "Rider");

            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var tripId = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("tripId").GetGuid();
            var messageId = TripOutboxIntents.RideRequestedMessageId(
                await LoadTripAsync(connectionString, tripId));

            // ORACLE 1 — durable row: the outbox header carries the CREATION
            // context, which is the CreateRide decision span.
            var createSpan = WaitFor(activities, a =>
                a.OperationName == "RideHailing.Trip/CreateRide" &&
                a.GetTagItem("trip.id")?.ToString() == tripId.ToString());
            Assert.NotNull(createSpan);
            string durableTraceParent;
            await using (var db = new TripDbContext(new DbContextOptionsBuilder<TripDbContext>()
                             .UseNpgsql(connectionString).Options))
            {
                var row = await db.OutboxMessages.AsNoTracking().SingleAsync(m => m.MessageId == messageId);
                durableTraceParent = ExtractHeader(row.HeadersJson, "traceparent");
            }

            Assert.StartsWith($"00-{createSpan.TraceId}-", durableTraceParent);
            Assert.Contains($"-{createSpan.SpanId}", durableTraceParent);

            // The relayed delivery arrives on the probe queue with the same
            // creation context on the wire.
            ulong probeTag = 0;
            IReadOnlyBasicProperties? properties = null;
            byte[]? body = null;
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
            {
                while (!timeout.IsCancellationRequested)
                {
                    var delivery = await probeChannel.BasicGetAsync(probeQueue, autoAck: false);
                    if (delivery is null)
                    {
                        await Task.Delay(50, timeout.Token);
                        continue;
                    }

                    probeTag = delivery.DeliveryTag;
                    properties = delivery.BasicProperties;
                    body = delivery.Body.ToArray();
                    break;
                }
            }

            Assert.NotNull(properties);

            // ORACLE 2 — wire headers equal the durable creation context.
            var wireTraceParent = HeaderValue(properties.Headers, "traceparent");
            Assert.Equal(durableTraceParent, wireTraceParent);

            // ORACLE 3 — relay publish span re-parented from durable headers.
            var publishSpan = WaitFor(activities, a =>
                a.OperationName == "outbox.publish" &&
                a.GetTagItem("messaging.message.id")?.ToString() == messageId);
            Assert.NotNull(publishSpan);
            Assert.Equal(createSpan.TraceId, publishSpan.TraceId);
            Assert.Equal(createSpan.SpanId, publishSpan.ParentSpanId);

            // Return the delivery to the queue so the REAL shared consumer
            // receives it as a broker redelivery — proving the same context
            // survives a second asynchronous handoff.
            await probeChannel.BasicNackAsync(probeTag, multiple: false, requeue: true);

            // ORACLE 4 — a real shared consumer re-parents its consume span
            // from the delivery headers, inside the actual ManualAckConsumer.
            var handlerContext = new TaskCompletionSource<Activity?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            await using var publisher = new ConfirmedPublisher(rabbit.Factory);
            await using (var consumer = await ManualAckConsumer.StartAsync(
                rabbit.Factory, publisher, probeQueue,
                new HashSet<string>(StringComparer.Ordinal) { "RideRequestedV1" },
                (_, _) =>
                {
                    handlerContext.TrySetResult(Activity.Current);
                    return Task.CompletedTask;
                }, prefetch: 1))
            {
                var handlerActivity = await handlerContext.Task.WaitAsync(TimeSpan.FromSeconds(30));
                Assert.NotNull(handlerActivity);
                Assert.Equal("consume", handlerActivity.OperationName);
                Assert.Equal(createSpan.TraceId, handlerActivity.TraceId);
                Assert.Equal(createSpan.SpanId, handlerActivity.ParentSpanId);

                // The shared gauges refreshed by the relay's durable-row
                // observation: the Trip store has no pending rows left.
                var pending = meter.Flush().Where(e => e.Instrument == "outbox.pending").ToList();
                Assert.Equal(0, (long)pending.Sum(e => e.Value));

                // The handled envelope is exactly the durable intent.
                var envelope = JsonSerializer.Deserialize<MessageEnvelope<JsonElement>>(body!);
                Assert.NotNull(envelope);
                Assert.Equal(messageId, envelope.MessageId);
            }

            // No double instrumentation: exactly one publish span per relayed
            // messageId (relay execution context, creation context linkage),
            // and the Trip wrapper is a domain span, not a second producer.
            Assert.Single(activities.All("outbox.publish"),
                a => a.GetTagItem("messaging.message.id")?.ToString() == messageId);
        }
        finally
        {
            try
            {
                await using var cleanup = await rabbit.Factory.CreateConnectionAsync();
                await using var channel = await cleanup.CreateChannelAsync();
                await channel.QueueDeleteAsync(probeQueue, ifUnused: false, ifEmpty: false);
                // The durable topology queue also received a copy of the
                // relayed request; leave the shared broker clean for the
                // other broker-backed suites.
                await channel.QueuePurgeAsync("matching.ride-requested.q");
            }
            catch (Exception)
            {
                // Probe cleanup is best-effort; the broker fixture is disposed.
            }

            await using var drop = new NpgsqlConnection(maintenance);
            await drop.OpenAsync();
            await using var command = drop.CreateCommand();
            command.CommandText = $"DROP DATABASE \"{database}\" WITH (FORCE)";
            await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task<Trip> LoadTripAsync(string connectionString, Guid tripId)
    {
        await using var db = new TripDbContext(new DbContextOptionsBuilder<TripDbContext>()
            .UseNpgsql(connectionString).Options);
        return await db.Trips.AsNoTracking().SingleAsync(t => t.TripId == tripId);
    }

    private static Activity? WaitFor(ActivityRecorder recorder, Func<Activity, bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!timeout.IsCancellationRequested)
        {
            var match = recorder.Activities.FirstOrDefault(predicate);
            if (match is not null) return match;
            Task.Delay(50, timeout.Token).Wait(timeout.Token);
        }

        return null;
    }

    private static string ExtractHeader(string headersJson, string header)
    {
        using var doc = JsonDocument.Parse(headersJson);
        return doc.RootElement.GetProperty(header).GetString() ?? "";
    }

    private static string? HeaderValue(IDictionary<string, object?>? headers, string header)
    {
        if (headers is null || !headers.TryGetValue(header, out var value)) return null;
        return value switch
        {
            byte[] bytes => Encoding.UTF8.GetString(bytes),
            string text => text,
            _ => null,
        };
    }
}
