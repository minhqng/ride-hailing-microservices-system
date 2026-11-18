// Saga broker delivery proof.
//
// ORACLE: the Trip outbox relay delivers the saga's AuthorizePayment intent
// through the REAL declared topology onto payment.commands.q with the frozen
// operationKey/sagaId lineage intact on the wire. Payment-side consumption
//  is cross-owner pending and is NOT faked here.

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using RabbitMQ.Client;
using RideHailing.Messaging.Contracts;
using RideHailing.Messaging.Outbox;
using RideHailing.Messaging.Reliability;
using RideHailing.TripService.Application.Trips.Lifecycle;
using RideHailing.TripService.Domain.Sagas;
using RideHailing.TripService.Domain.Trips;
using RideHailing.TripService.Infrastructure.Persistence;
using RideHailing.TripService.Integration.Messaging;

namespace RideHailing.TripService.IntegrationTests;

[Collection(TripPostgresCollection.Name)]
public sealed class TripSagaRelayTests(TripPostgresFixture fixture, TripRabbitFixture rabbit)
    : IClassFixture<TripRabbitFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 4, 0, 0, TimeSpan.Zero);

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static TripDbContext Context(string connectionString) =>
        new(new DbContextOptionsBuilder<TripDbContext>().UseNpgsql(connectionString).Options);

    [Fact]
    public async Task AuthorizeIntent_RelaysToRealBroker_PaymentCommandQueueWithLineage()
    {
        var name = $"trip_w10_relay_{Guid.NewGuid():N}";
        var maintenance = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        {
            Database = "postgres",
        }.ToString();
        await using (var maintenanceDb = Context(maintenance))
        {
            var admin = maintenanceDb.Database.GetDbConnection();
            await admin.OpenAsync();
            await using var command = admin.CreateCommand();
            command.CommandText = $"CREATE DATABASE \"{name}\"";
            await command.ExecuteNonQueryAsync();
        }

        var cs = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = name }.ToString();

        // Seed through the real intake so the saga + intent are the production
        // artifacts, committed by the real handlers.
        var tripId = Guid.NewGuid();
        var riderId = Guid.NewGuid();
        var driverId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        await using (var db = Context(cs))
        {
            await db.Database.MigrateAsync();
            db.Trips.Add(Trip.Create(tripId, riderId, new(10.7, 106.6), new(10.8, 106.7),
                new("VND", 50000m, "v1", Now), Now));
            await db.SaveChangesAsync();
            await new StartMatchingHandler(db, new Clock(), NullLogger<StartMatchingHandler>.Instance)
                .HandleAsync(tripId, riderId);
        }

        await using (var db = Context(cs))
        {
            await new RecordDriverReservationHandler(db, new Clock(), NullLogger<RecordDriverReservationHandler>.Instance)
                .HandleAsync(tripId, driverId, reservationId, 1);
        }

        await using (var db = Context(cs))
        {
            var accepted = await new RecordDriverAcceptedHandler(db, new Clock(),
                NullLogger<RecordDriverAcceptedHandler>.Instance).HandleAsync(tripId, driverId, reservationId);
            Assert.True(accepted.IsSuccess);
        }

        // Shared relay (C-owned mechanics) over the real broker: purge the
        // command queue first so only this proof's delivery is observed.
        var dataSource = NpgsqlDataSource.Create(cs);
        var services = new ServiceCollection()
            .AddDbContext<TripDbContext>(options => options.UseNpgsql(dataSource))
            .AddSingleton(dataSource)
            .AddSingleton<IOutboxStore>(sp => new NpgsqlOutboxStore(sp.GetRequiredService<NpgsqlDataSource>()))
            .AddSingleton(new ConfirmedPublisher(rabbit.Factory))
            .AddSingleton<IOutboxPublisher, RabbitOutboxPublisher>()
            .AddSingleton(new OutboxRelayOptions
            {
                RelayId = $"relay-proof:{Guid.NewGuid():N}",
                BatchSize = 10,
                ClaimLease = TimeSpan.Zero,
                PollInterval = TimeSpan.FromMilliseconds(100),
            })
            .AddSingleton<OutboxRelay>()
            .BuildServiceProvider();
        var relay = services.GetRequiredService<OutboxRelay>();

        await using (var preConnection = await rabbit.Factory.CreateConnectionAsync())
        await using (var preChannel = await preConnection.CreateChannelAsync())
        {
            await preChannel.QueuePurgeAsync("payment.commands.q");
        }

        var published = await relay.RunOnceAsync(CancellationToken.None);
        Assert.True(published >= 1, "The relay must publish at least the authorize intent.");

        // The intent is durably marked published, never silently lost.
        await using (var db = Context(cs))
        {
            var row = await db.OutboxMessages.AsNoTracking()
                .SingleAsync(m => m.MessageId == TripSagaIntents.AuthorizePaymentMessageId(tripId));
            Assert.Equal("PUBLISHED", row.Status);
            Assert.NotNull(row.PublishedAt);
        }

        string sagaId;
        await using (var db = Context(cs))
        {
            sagaId = (await db.RideSagas.AsNoTracking().SingleAsync(s => s.TripId == tripId)).SagaId.ToString("D");
        }

        // The message physically sits on payment.commands.q, lineage intact.
        await using (var connection = await rabbit.Factory.CreateConnectionAsync())
        await using (var channel = await connection.CreateChannelAsync())
        {
            var sawAuthorize = false;
            while (await channel.BasicGetAsync("payment.commands.q", autoAck: true) is { } delivery)
            {
                var envelope = JsonSerializer.Deserialize<MessageEnvelope<JsonElement>>(delivery.Body.ToArray());
                Assert.NotNull(envelope);
                if (envelope.MessageId != TripSagaIntents.AuthorizePaymentMessageId(tripId))
                {
                    continue;
                }

                sawAuthorize = true;
                Assert.Equal("AuthorizePaymentV1", envelope.MessageType);
                Assert.Equal(RideSagaOperationKeys.Authorize(tripId), envelope.OperationKey);
                Assert.Equal(sagaId, envelope.SagaId);
                Assert.Equal(tripId, envelope.Payload.GetProperty("tripId").GetGuid());
                Assert.Equal(50000L, envelope.Payload.GetProperty("amountMinor").GetInt64());
                Assert.Equal("VND", envelope.Payload.GetProperty("currency").GetString());
            }

            Assert.True(sawAuthorize, "The authorize command must be delivered to payment.commands.q.");
        }

        await services.DisposeAsync();
    }
}
