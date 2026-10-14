// B-W08 message-identity fix: cross-contract proof using A's REAL fixture.
// Loads src/Services/Trip/.../Fixtures/ride-requested-v1.example.json from
// the repository (never a hand-copied approximation), wraps that exact payload
// in C's real MessageEnvelope<JsonElement> with a valid NON-UUID messageId,
// and proves the W8 handler persists the raw envelope identity verbatim.
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Npgsql;
using RideHailing.MatchingService.Application.Messaging;
using RideHailing.MatchingService.Infrastructure.Persistence;
using RideHailing.Messaging.Contracts;

namespace RideHailing.MatchingService.IntegrationTests;

[Collection(MatchingPostgresCollection.Name)]
public sealed class RideRequestedEnvelopeIdentityTests
{
    private readonly MatchingPostgresDatabase _db;

    public RideRequestedEnvelopeIdentityTests(MatchingPostgresDatabase db)
    {
        _db = db;
    }

    private sealed class TestLogger<T> : ILogger<T>
    {
        IDisposable? ILogger.BeginScope<TState>(TState state) => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) { }
    }

    private static string FixturePath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RideHailing.Distributed.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.True(directory is not null, "Repository root (RideHailing.Distributed.slnx) was not found.");
        return Path.Combine(
            directory!.FullName,
            "src", "Services", "Trip", "RideHailing.TripService",
            "Integration", "Matching", "Fixtures", "ride-requested-v1.example.json");
    }

    private static JsonElement LoadFixturePayload()
    {
        var json = File.ReadAllText(FixturePath());
        using var doc = JsonDocument.Parse(json);
        // Clone so the element survives disposal.
        return doc.RootElement.Clone();
    }

    private static byte[] EnvelopeBytes(string messageId, JsonElement payload)
    {
        var envelope = MessageEnvelope<JsonElement>.Create(
            "RideRequestedV1", Guid.NewGuid().ToString("N"), payload, messageId: messageId);
        return JsonSerializer.SerializeToUtf8Bytes(envelope);
    }

    private async Task<string?> ReadInputMessageIdAsync(Guid trip, int round)
    {
        await using var conn = new NpgsqlConnection(_db.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT input_message_id FROM matching_rounds WHERE trip_id = @t AND matching_round = @r", conn);
        cmd.Parameters.AddWithValue("t", trip);
        cmd.Parameters.AddWithValue("r", round);
        var value = await cmd.ExecuteScalarAsync();
        return value is null or DBNull ? null : (string)value;
    }

    private async Task CleanupAsync(Guid trip)
    {
        await using var conn = new NpgsqlConnection(_db.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "DELETE FROM matching_rounds WHERE trip_id = @t", conn);
        cmd.Parameters.AddWithValue("t", trip);
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task A_Fixture_Has_Five_Fields_And_No_InputMessageId()
    {
        var json = File.ReadAllText(FixturePath());
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(5, root.EnumerateObject().Count());
        foreach (var name in new[] { "tripId", "matchingRound", "pickup", "dropoff", "requestedAt" })
        {
            Assert.True(root.TryGetProperty(name, out _), $"missing A fixture property '{name}'");
        }

        Assert.False(root.TryGetProperty("inputMessageId", out _),
            "A fixture must not carry a B-specific inputMessageId field.");
    }

    [Fact]
    public async Task Handler_Persists_Raw_NonUuid_Envelope_MessageId_Verbatim()
    {
        var payload = LoadFixturePayload();
        var trip = Guid.Parse(payload.GetProperty("tripId").GetString()!);
        var round = payload.GetProperty("matchingRound").GetInt32();
        // Use a fresh trip per run: the checked-in fixture trip may already exist.
        var freshTrip = Guid.NewGuid();
        var fixtureJson = File.ReadAllText(FixturePath())
            .Replace(
                payload.GetProperty("tripId").GetString()!,
                freshTrip.ToString("D"),
                StringComparison.Ordinal);
        using var freshDoc = JsonDocument.Parse(fixtureJson);
        var freshPayload = freshDoc.RootElement.Clone();

        const string messageId = "01K8EXAMPLEMESSAGE";
        var handler = new RideRequestedHandler(
            new MatchingAttemptRepository(_db.ConnectionString),
            new TestLogger<RideRequestedHandler>());
        try
        {
            var body = EnvelopeBytes(messageId, freshPayload);
            var outcome = await handler.HandleAsync(
                new BrokerDelivery("trip.ride-requested.v1", body,
                    new Dictionary<string, object?>(), 0, false),
                CancellationToken.None);
            Assert.Equal(HandlerOutcome.Done, outcome);

            var stored = await ReadInputMessageIdAsync(freshTrip, round);
            Assert.Equal(messageId, stored);

            // Exact redelivery converges: same row, same stored identity, no
            // attempt sequence, ACK after durable completion.
            var redelivery = await handler.HandleAsync(
                new BrokerDelivery("trip.ride-requested.v1", body,
                    new Dictionary<string, object?>(), 1, true),
                CancellationToken.None);
            Assert.Equal(HandlerOutcome.Done, redelivery);
            Assert.Equal(messageId, await ReadInputMessageIdAsync(freshTrip, round));

            var attempts = await new MatchingAttemptRepository(_db.ConnectionString)
                .LoadAttemptsAsync(freshTrip, round);
            Assert.Empty(attempts);
            _ = trip;
        }
        finally
        {
            await CleanupAsync(freshTrip);
        }
    }

    [Fact]
    public async Task Handler_Accepts_Varied_NonUuid_MessageIds_Verbatim()
    {
        var fixtureJson = File.ReadAllText(FixturePath());
        foreach (var messageId in new[]
                 {
                     "ride-requested-event-42",
                     "550e8400e29b41d4a716446655440000",
                     "MSG with-dashes_and.dots:123",
                 })
        {
            var trip = Guid.NewGuid();
            var json = fixtureJson.Replace(
                "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                trip.ToString("D"),
                StringComparison.Ordinal);
            using var doc = JsonDocument.Parse(json);
            var handler = new RideRequestedHandler(
                new MatchingAttemptRepository(_db.ConnectionString),
                new TestLogger<RideRequestedHandler>());
            try
            {
                var body = EnvelopeBytes(messageId, doc.RootElement.Clone());
                Assert.Equal(HandlerOutcome.Done, await handler.HandleAsync(
                    new BrokerDelivery("trip.ride-requested.v1", body,
                        new Dictionary<string, object?>(), 0, false),
                    CancellationToken.None));
                Assert.Equal(messageId, await ReadInputMessageIdAsync(trip, 1));
            }
            finally
            {
                await CleanupAsync(trip);
            }
        }
    }

    [Fact]
    public async Task Handler_Ignores_Payload_Embedded_InputMessageId()
    {
        // The old B-specific payload field is NOT required and, when present
        // as an unrelated extra, must not override the envelope identity.
        var trip = Guid.NewGuid();
        var fixtureJson = File.ReadAllText(FixturePath()).Replace(
            "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
            trip.ToString("D"),
            StringComparison.Ordinal);
        using var doc = JsonDocument.Parse(fixtureJson);
        using var mutated = JsonDocument.Parse(
            doc.RootElement.GetRawText().TrimEnd('}')
            + $",\"inputMessageId\":\"{Guid.NewGuid():N}\"}}");

        const string envelopeId = "01K8EXAMPLEMESSAGE";
        var handler = new RideRequestedHandler(
            new MatchingAttemptRepository(_db.ConnectionString),
            new TestLogger<RideRequestedHandler>());
        try
        {
            var body = EnvelopeBytes(envelopeId, mutated.RootElement.Clone());
            Assert.Equal(HandlerOutcome.Done, await handler.HandleAsync(
                new BrokerDelivery("trip.ride-requested.v1", body,
                    new Dictionary<string, object?>(), 0, false),
                CancellationToken.None));
            Assert.Equal(envelopeId, await ReadInputMessageIdAsync(trip, 1));
        }
        finally
        {
            await CleanupAsync(trip);
        }
    }

    [Fact]
    public async Task Handler_Rejects_Missing_Or_Invalid_Envelope_MessageId()
    {
        var payload = LoadFixturePayload();
        var handler = new RideRequestedHandler(
            new MatchingAttemptRepository(_db.ConnectionString),
            new TestLogger<RideRequestedHandler>());

        foreach (var badId in new[] { "", "   " })
        {
            var envelope = new MessageEnvelope<JsonElement>(
                badId, "RideRequestedV1", 1, DateTimeOffset.UtcNow,
                Guid.NewGuid().ToString("N"), null, payload);
            var body = JsonSerializer.SerializeToUtf8Bytes(envelope);
            await Assert.ThrowsAsync<ArgumentException>(() => handler.HandleAsync(
                new BrokerDelivery("trip.ride-requested.v1", body,
                    new Dictionary<string, object?>(), 0, false),
                CancellationToken.None));
        }
    }

    [Fact]
    public async Task Same_Round_Different_MessageId_Does_Not_Overwrite_Stored_Identity()
    {
        var fixtureJson = File.ReadAllText(FixturePath());
        var trip = Guid.NewGuid();
        var json = fixtureJson.Replace(
            "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
            trip.ToString("D"),
            StringComparison.Ordinal);
        using var doc = JsonDocument.Parse(json);
        var payload = doc.RootElement.Clone();
        var repo = new MatchingAttemptRepository(_db.ConnectionString);
        var handler = new RideRequestedHandler(repo, new TestLogger<RideRequestedHandler>());
        try
        {
            const string first = "01K8EXAMPLEMESSAGE";
            const string second = "ride-requested-event-42";
            Assert.Equal(HandlerOutcome.Done, await handler.HandleAsync(
                new BrokerDelivery("trip.ride-requested.v1", EnvelopeBytes(first, payload),
                    new Dictionary<string, object?>(), 0, false),
                CancellationToken.None));
            Assert.Equal(HandlerOutcome.Done, await handler.HandleAsync(
                new BrokerDelivery("trip.ride-requested.v1", EnvelopeBytes(second, payload),
                    new Dictionary<string, object?>(), 1, true),
                CancellationToken.None));
            Assert.Equal(first, await ReadInputMessageIdAsync(trip, 1));
        }
        finally
        {
            await CleanupAsync(trip);
        }
    }

    [Fact]
    public async Task Same_MessageId_Different_Round_Is_Rejected_Without_Second_Effect()
    {
        var fixtureJson = File.ReadAllText(FixturePath());
        var tripA = Guid.NewGuid();
        var tripB = Guid.NewGuid();
        const string messageId = "01K8EXAMPLEMESSAGE";
        var repo = new MatchingAttemptRepository(_db.ConnectionString);
        var handler = new RideRequestedHandler(repo, new TestLogger<RideRequestedHandler>());
        try
        {
            using var docA = JsonDocument.Parse(fixtureJson.Replace(
                "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", tripA.ToString("D"), StringComparison.Ordinal));
            Assert.Equal(HandlerOutcome.Done, await handler.HandleAsync(
                new BrokerDelivery("trip.ride-requested.v1", EnvelopeBytes(messageId, docA.RootElement.Clone()),
                    new Dictionary<string, object?>(), 0, false),
                CancellationToken.None));

            using var docB = JsonDocument.Parse(fixtureJson.Replace(
                "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", tripB.ToString("D"), StringComparison.Ordinal));
            await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync(
                new BrokerDelivery("trip.ride-requested.v1", EnvelopeBytes(messageId, docB.RootElement.Clone()),
                    new Dictionary<string, object?>(), 0, false),
                CancellationToken.None));

            // The conflicting round was not created as a second effect.
            Assert.Null(await repo.LoadRoundAsync(tripB, 1));
            Assert.Equal(messageId, await ReadInputMessageIdAsync(tripA, 1));
        }
        finally
        {
            await CleanupAsync(tripA);
            await CleanupAsync(tripB);
        }
    }
}
