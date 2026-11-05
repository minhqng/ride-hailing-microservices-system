// DS03 notification inbox acceptance over real PostgreSQL and a real host:
// recipient isolation, durable dedup, replay, migration and content safety.
// Critical cases use the production store/host; no in-memory substitute is
// accepted for the guarantees these tests assert.

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using RideHailing.Messaging.Contracts;
using RideHailing.Messaging.Reliability;
using RideHailing.NotificationWorker;

namespace RideHailing.NotificationService.IntegrationTests;

[Collection(NotificationCollection.Name)]
public sealed class NotificationInboxTests(NotificationHostFixture fixture)
{
    private static MessageEnvelope<JsonElement> Envelope(
        Guid recipient, string role, string type, string messageId, Guid tripId,
        string correlationId = "correlation-1", string? reasonCode = null)
    {
        var intent = new NotificationRequestedV1
        {
            RecipientUserId = recipient,
            RecipientRole = role,
            NotificationType = type,
            TripId = tripId,
            ReasonCode = reasonCode,
        };
        return MessageEnvelope<JsonElement>.Create(
            NotificationMessage.MessageType,
            correlationId,
            JsonSerializer.SerializeToElement(intent),
            messageId: messageId,
            aggregateType: "Trip",
            aggregateId: tripId.ToString("D"));
    }

    private Task<bool> ProcessAsync(MessageEnvelope<JsonElement> envelope) =>
        fixture.Processor.ProcessAsync(envelope, default);

    private Task<bool> ProcessAsync(Guid recipient, string role, string type, string messageId,
        Guid? tripId = null, string? reasonCode = null) =>
        ProcessAsync(Envelope(recipient, role, type, messageId, tripId ?? Guid.NewGuid(),
            reasonCode: reasonCode));

    private HttpRequestMessage Request(HttpMethod method, string path, Guid? actor, string role = "Rider")
    {
        var request = new HttpRequestMessage(method, path);
        if (actor is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer", fixture.Factory.CreateToken(actor.Value, role));
        return request;
    }

    private async Task<NotificationInboxItem[]> InboxAsync(Guid actor, string role = "Rider", int? limit = null)
    {
        using var response = await fixture.Client.SendAsync(
            Request(HttpMethod.Get, "/notifications/me" + (limit is null ? "" : $"?limit={limit}"), actor, role));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<NotificationInboxItem[]>())!;
    }

    [Fact]
    public async Task RiderReadsOwnInbox_AndSeesBoundedContent()
    {
        var rider = Guid.NewGuid();
        var trip = Guid.NewGuid();
        await ProcessAsync(rider, NotificationMessage.Roles.Rider,
            NotificationMessage.Types.RiderRideRequested, "n-rider-1-" + Guid.NewGuid(), trip);
        await ProcessAsync(rider, NotificationMessage.Roles.Rider,
            NotificationMessage.Types.RiderDriverAssigned, "n-rider-2-" + Guid.NewGuid(), trip);

        var items = await InboxAsync(rider);

        Assert.Equal(2, items.Length);
        Assert.All(items, item => Assert.Equal(NotificationMessage.Roles.Rider, item.RecipientRole));
        Assert.Contains(items, item => item.NotificationType == NotificationMessage.Types.RiderRideRequested);
        Assert.Contains(items, item => item.NotificationType == NotificationMessage.Types.RiderDriverAssigned);
        Assert.All(items, item => Assert.Equal("INBOX_AVAILABLE", item.Status));
    }

    [Fact]
    public async Task DriverReadsOwnInbox()
    {
        var driver = Guid.NewGuid();
        await ProcessAsync(driver, NotificationMessage.Roles.Driver,
            NotificationMessage.Types.DriverTripAssigned, "n-driver-1-" + Guid.NewGuid());

        var items = await InboxAsync(driver, NotificationMessage.Roles.Driver);

        var item = Assert.Single(items);
        Assert.Equal(NotificationMessage.Roles.Driver, item.RecipientRole);
        Assert.Equal(NotificationMessage.Types.DriverTripAssigned, item.NotificationType);
    }

    [Fact]
    public async Task CrossUserAndCrossRoleInboxAccessIsDenied()
    {
        var riderA = Guid.NewGuid();
        var riderB = Guid.NewGuid();
        var driverB = Guid.NewGuid();
        await ProcessAsync(riderA, NotificationMessage.Roles.Rider,
            NotificationMessage.Types.RiderRideRequested, "n-cross-a-" + Guid.NewGuid());
        await ProcessAsync(driverB, NotificationMessage.Roles.Driver,
            NotificationMessage.Types.DriverTripAssigned, "n-cross-d-" + Guid.NewGuid());

        Assert.Single(await InboxAsync(riderA));
        Assert.Empty(await InboxAsync(riderB));
        Assert.Single(await InboxAsync(driverB, NotificationMessage.Roles.Driver));
        Assert.Empty(await InboxAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task ForeignNotificationIdsAreNotFoundAndCannotBeMarkedRead()
    {
        var owner = Guid.NewGuid();
        var attacker = Guid.NewGuid();
        await ProcessAsync(owner, NotificationMessage.Roles.Rider,
            NotificationMessage.Types.RiderRideRequested, "n-owner-" + Guid.NewGuid());
        var entry = Assert.Single(await fixture.Store.ListForRecipientAsync(owner, 10, default));

        using var foreignGet = await fixture.Client.SendAsync(
            Request(HttpMethod.Get, $"/notifications/{entry.NotificationId:D}", attacker));
        Assert.Equal(HttpStatusCode.NotFound, foreignGet.StatusCode);

        using var foreignRead = await fixture.Client.SendAsync(
            Request(HttpMethod.Post, $"/notifications/{entry.NotificationId:D}/read", attacker));
        Assert.Equal(HttpStatusCode.NotFound, foreignRead.StatusCode);

        using var ownerGet = await fixture.Client.SendAsync(
            Request(HttpMethod.Get, $"/notifications/{entry.NotificationId:D}", owner));
        Assert.Equal(HttpStatusCode.OK, ownerGet.StatusCode);

        using var ownerRead = await fixture.Client.SendAsync(
            Request(HttpMethod.Post, $"/notifications/{entry.NotificationId:D}/read", owner));
        Assert.Equal(HttpStatusCode.OK, ownerRead.StatusCode);
        var read = (await ownerRead.Content.ReadFromJsonAsync<NotificationInboxItem>())!;
        Assert.Equal("READ", read.Status);
        Assert.NotNull(read.ReadAt);

        // Read-marking is idempotent and stays READ.
        using var repeat = await fixture.Client.SendAsync(
            Request(HttpMethod.Post, $"/notifications/{entry.NotificationId:D}/read", owner));
        var repeated = (await repeat.Content.ReadFromJsonAsync<NotificationInboxItem>())!;
        Assert.Equal("READ", repeated.Status);
    }

    [Fact]
    public async Task UnauthenticatedInboxAccessIsRejected()
    {
        foreach (var (method, path) in new[]
                 {
                     (HttpMethod.Get, "/notifications/me"),
                     (HttpMethod.Get, $"/notifications/{Guid.NewGuid():D}"),
                     (HttpMethod.Post, $"/notifications/{Guid.NewGuid():D}/read"),
                 })
        {
            using var response = await fixture.Client.SendAsync(Request(method, path, null));
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Fact]
    public async Task DuplicateEventProducesOneNotification()
    {
        var rider = Guid.NewGuid();
        var envelope = Envelope(rider, NotificationMessage.Roles.Rider,
            NotificationMessage.Types.RiderTripCompleted, "n-duplicate-" + Guid.NewGuid(), Guid.NewGuid());

        Assert.True(await ProcessAsync(envelope));
        Assert.False(await ProcessAsync(envelope));

        Assert.Single(await InboxAsync(rider));
    }

    [Fact]
    public async Task TwoRecipientsForOneSourceEvent_AreSeparateInboxRows()
    {
        var rider = Guid.NewGuid();
        var driver = Guid.NewGuid();
        var trip = Guid.NewGuid();
        // One source business event yields two recipient-addressed intents,
        // each with its own deterministic message identity.
        Assert.True(await ProcessAsync(Envelope(rider, NotificationMessage.Roles.Rider,
            NotificationMessage.Types.RiderDriverAssigned, "n-shared-rider-" + Guid.NewGuid(), trip)));
        Assert.True(await ProcessAsync(Envelope(driver, NotificationMessage.Roles.Driver,
            NotificationMessage.Types.DriverTripAssigned, "n-shared-driver-" + Guid.NewGuid(), trip)));

        Assert.Single(await InboxAsync(rider));
        Assert.Single(await InboxAsync(driver, NotificationMessage.Roles.Driver));
    }

    [Fact]
    public async Task SameMessageIdWithChangedRecipientIsPoisonAndCannotInjectAnotherInbox()
    {
        var rider = Guid.NewGuid();
        var attacker = Guid.NewGuid();
        var trip = Guid.NewGuid();
        var messageId = "n-recipient-conflict-" + Guid.NewGuid();
        Assert.True(await ProcessAsync(Envelope(rider, NotificationMessage.Roles.Rider,
            NotificationMessage.Types.RiderRideRequested, messageId, trip)));

        var error = await Assert.ThrowsAsync<DeliveryFailureException>(() =>
            ProcessAsync(Envelope(attacker, NotificationMessage.Roles.Rider,
                NotificationMessage.Types.RiderRideRequested, messageId, trip)));

        Assert.Equal(FailureKind.Poison, error.Kind);
        Assert.Equal("NOTIFICATION_CONTENT_CONFLICT", error.Code);
        Assert.Single(await InboxAsync(rider));
        Assert.Empty(await InboxAsync(attacker));
    }

    [Fact]
    public async Task SameMessageIdWithConflictingPayloadIsPoison()
    {
        var rider = Guid.NewGuid();
        var trip = Guid.NewGuid();
        var messageId = "n-conflict-" + Guid.NewGuid();
        Assert.True(await ProcessAsync(Envelope(rider, NotificationMessage.Roles.Rider,
            NotificationMessage.Types.RiderRideRequested, messageId, trip)));

        var error = await Assert.ThrowsAsync<DeliveryFailureException>(() =>
            ProcessAsync(Envelope(rider, NotificationMessage.Roles.Rider,
                NotificationMessage.Types.RiderRideCancelled, messageId, trip)));

        Assert.Equal(FailureKind.Poison, error.Kind);
        Assert.Equal("NOTIFICATION_CONTENT_CONFLICT", error.Code);
        Assert.Single(await InboxAsync(rider));
    }

    [Fact]
    public async Task ConsumerReplayAfterCommitAndWorkerRestart_PreserveOneRow()
    {
        var rider = Guid.NewGuid();
        var envelope = Envelope(rider, NotificationMessage.Roles.Rider,
            NotificationMessage.Types.RiderTripCompleted, "n-replay-" + Guid.NewGuid(), Guid.NewGuid());
        Assert.True(await ProcessAsync(envelope));

        var replayProcessor = fixture.Factory.Services.GetRequiredService<NotificationEventProcessor>();
        Assert.False(await replayProcessor.ProcessAsync(envelope, default));

        await using var source = NpgsqlDataSource.Create(fixture.ConnectionString);
        var restarted = new NotificationProjectionStore(source);
        var rows = await restarted.ListForRecipientAsync(rider, 10, default);
        Assert.Single(rows);
    }

    [Fact]
    public async Task PermanentInvalidEvent_IsVisiblePoison()
    {
        var error = await Assert.ThrowsAsync<DeliveryFailureException>(() =>
            ProcessAsync(Envelope(Guid.NewGuid(), NotificationMessage.Roles.Driver,
                NotificationMessage.Types.RiderRideRequested, "n-invalid-" + Guid.NewGuid(), Guid.NewGuid())));
        Assert.Equal(FailureKind.Poison, error.Kind);
        Assert.Equal("INVALID_NOTIFICATION_EVENT", error.Code);
    }

    [Fact]
    public async Task CorrelationIdIsPreserved()
    {
        var rider = Guid.NewGuid();
        var correlation = "correlation-" + Guid.NewGuid().ToString("N");
        await ProcessAsync(Envelope(rider, NotificationMessage.Roles.Rider,
            NotificationMessage.Types.RiderRideRequested, "n-correlation-" + Guid.NewGuid(),
            Guid.NewGuid(), correlation));

        var entry = Assert.Single(await fixture.Store.ListForRecipientAsync(rider, 10, default));
        Assert.Equal(correlation, entry.CorrelationId);
    }

    [Fact]
    public async Task ContentContainsNoCredentialsPaymentSecretsOrTokens()
    {
        var rider = Guid.NewGuid();
        var trip = Guid.NewGuid();
        foreach (var type in new[]
                 {
                     NotificationMessage.Types.RiderRideRequested,
                     NotificationMessage.Types.RiderDriverAssigned,
                     NotificationMessage.Types.RiderDriverRejected,
                     NotificationMessage.Types.RiderNoDriver,
                     NotificationMessage.Types.RiderTripCompleted,
                     NotificationMessage.Types.RiderRideCancelled,
                     NotificationMessage.Types.RiderPaymentFailed,
                 })
        {
            await ProcessAsync(rider, NotificationMessage.Roles.Rider, type, "n-safe-" + Guid.NewGuid(), trip);
        }

        var items = await InboxAsync(rider);
        Assert.Equal(7, items.Length);
        foreach (var item in items)
        {
            Assert.DoesNotContain("Bearer", item.Content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("password", item.Content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("token", item.Content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("eyJ", item.Content, StringComparison.Ordinal);
            Assert.DoesNotContain("card", item.Content, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task LimitIsBounded()
    {
        var rider = Guid.NewGuid();
        for (var i = 0; i < 3; i++)
            await ProcessAsync(rider, NotificationMessage.Roles.Rider,
                NotificationMessage.Types.RiderRideRequested, $"n-limit-{i}-" + Guid.NewGuid());

        Assert.Single(await InboxAsync(rider, limit: 1));
        Assert.Equal(3, (await InboxAsync(rider, limit: 1000)).Length);
    }

    [Fact]
    public async Task FreshDatabaseSchemaMigrationSucceeds()
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT table_name, column_name
            FROM information_schema.columns
            WHERE table_schema = 'public'
              AND table_name IN ('notification_deliveries','notifications','notification_source_messages')
            """, connection);
        var columns = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            columns.Add(reader.GetString(0) + "." + reader.GetString(1));

        foreach (var expected in new[]
                 {
                     "notification_deliveries.content_fingerprint",
                     "notification_source_messages.message_id",
                     "notification_source_messages.fingerprint",
                     "notifications.notification_id",
                     "notifications.recipient_user_id",
                     "notifications.recipient_role",
                     "notifications.notification_type",
                     "notifications.trip_id",
                     "notifications.source_message_id",
                     "notifications.correlation_id",
                     "notifications.content",
                     "notifications.created_at",
                     "notifications.available_at",
                     "notifications.read_at",
                     "notifications.delivery_status",
                 })
        {
            Assert.Contains(expected, columns);
        }
    }

    [Fact]
    public async Task ExistingNotificationRowsSurviveUpgrade()
    {
        var database = "notif_upgrade_" + Guid.NewGuid().ToString("N");
        var admin = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = "postgres" };
        await using (var adminConnection = new NpgsqlConnection(admin.ToString()))
        {
            await adminConnection.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", adminConnection);
            await create.ExecuteNonQueryAsync();
        }

        var probe = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = database };
        try
        {
            await using (var connection = new NpgsqlConnection(probe.ToString()))
            {
                await connection.OpenAsync();
                await using var legacy = new NpgsqlCommand("""
                    CREATE TABLE notification_deliveries (
                        consumer_scope text NOT NULL,
                        message_id text NOT NULL,
                        message_type text NOT NULL,
                        correlation_id text NOT NULL,
                        aggregate_id text NULL,
                        state text NOT NULL,
                        attempt_count integer NOT NULL DEFAULT 1,
                        last_error_code text NULL,
                        first_seen_at timestamptz NOT NULL,
                        delivered_at timestamptz NULL,
                        PRIMARY KEY (consumer_scope, message_id)
                    );
                    INSERT INTO notification_deliveries(
                        consumer_scope,message_id,message_type,correlation_id,aggregate_id,state,
                        first_seen_at,delivered_at)
                    VALUES('legacy','legacy-1','PaymentCapturedV1','corr','trip','DELIVERED',now(),now());
                    """, connection);
                await legacy.ExecuteNonQueryAsync();
            }

            await using (var source = NpgsqlDataSource.Create(probe.ToString()))
            {
                var store = new NotificationProjectionStore(source);
                await store.EnsureSchemaAsync();
                await using var connection = await source.OpenConnectionAsync();
                await using var verify = new NpgsqlCommand("""
                    SELECT
                      (SELECT count(*) FROM notification_deliveries
                       WHERE consumer_scope='legacy' AND message_id='legacy-1'),
                      (SELECT count(*) FROM information_schema.columns
                       WHERE table_schema='public' AND table_name='notification_deliveries'
                         AND column_name='content_fingerprint'),
                      (SELECT count(*) FROM information_schema.tables
                       WHERE table_schema='public' AND table_name='notifications'),
                      (SELECT count(*) FROM information_schema.tables
                       WHERE table_schema='public' AND table_name='notification_source_messages')
                    """, connection);
                await using var reader = await verify.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal(1L, reader.GetInt64(0));
                Assert.Equal(1L, reader.GetInt64(1));
                Assert.Equal(1L, reader.GetInt64(2));
                Assert.Equal(1L, reader.GetInt64(3));
            }
        }
        finally
        {
            await using var adminConnection = new NpgsqlConnection(admin.ToString());
            await adminConnection.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE \"{database}\" WITH (FORCE)", adminConnection);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
