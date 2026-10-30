// A-W12-01/02 telemetry semantics against real PostgreSQL through the real
// HTTP endpoints and real consumer handler.
//
// ORACLE: metric deltas exported through a real OpenTelemetry collector
// (delta temporality) and started activities (ActivityListener) — never
// handler call counts. Counters must describe COMMITTED business
// transitions: replays, create-race reconciliation and redelivered receipts
// must not inflate them; OCC rollback never reaches a counter; metric
// dimensions stay bounded vocabulary values.

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using RideHailing.Messaging.Contracts;
using RideHailing.TripService.Api;
using RideHailing.TripService.Application.Trips.Lifecycle;
using RideHailing.TripService.Domain.Trips;
using RideHailing.TripService.Infrastructure.Persistence;
using RideHailing.TripService.Integration.Messaging;
using RideHailing.TripService.Integration.Observability;

namespace RideHailing.TripService.IntegrationTests;

[Collection(TripPostgresCollection.Name)]
public sealed class TripW12TelemetryTests : IDisposable
{
    private readonly TripPostgresFixture _fixture;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public TripW12TelemetryTests(TripPostgresFixture fixture)
    {
        _fixture = fixture;
        _factory = new TripApplicationFactory(fixture.ConnectionString)
            .WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
                services.AddTransient<IStartupFilter, TestPrincipalStartupFilter>()));
        _client = _factory.CreateClient();
        using var setup = new TripDbContext(Options());
        setup.Database.Migrate();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private DbContextOptions<TripDbContext> Options() =>
        new DbContextOptionsBuilder<TripDbContext>().UseNpgsql(_fixture.ConnectionString).Options;

    private static StringContent RideBody(double pickupLat = 10.762622, double pickupLon = 106.660172) =>
        new(JsonSerializer.Serialize(new
        {
            pickup = new { latitude = pickupLat, longitude = pickupLon },
            dropoff = new { latitude = pickupLat + 0.01, longitude = pickupLon + 0.01 },
        }), Encoding.UTF8, "application/json");

    private static HttpRequestMessage Post(string key, Guid rider, HttpContent? body = null)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, TripRoutes.CreateRide)
        {
            Content = body ?? RideBody(),
        };
        message.Headers.Add(TripIdentityHeaders.IdempotencyKey, key);
        message.Headers.Add(TestPrincipalStartupFilter.NameIdentifierHeader, rider.ToString("D"));
        message.Headers.Add(TestPrincipalStartupFilter.RoleHeader, "Rider");
        return message;
    }

    private static async Task<string> ProblemCodeAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.TryGetProperty("code", out var code) ? code.GetString() ?? "" : "";
    }

    private HttpRequestMessage Cancel(Guid tripId, Guid rider)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, $"/trips/{tripId:D}/cancel");
        message.Headers.Add(TestPrincipalStartupFilter.NameIdentifierHeader, rider.ToString("D"));
        message.Headers.Add(TestPrincipalStartupFilter.RoleHeader, "Rider");
        return message;
    }

    private static long Total(IReadOnlyList<MeterEvent> events, string instrument,
        string? tagValue = null, string? tagKey = null) =>
        events.Where(e => e.Instrument == instrument &&
                (tagValue is null || e.Tags.GetValueOrDefault(tagKey ?? "reason") == tagValue))
            .Sum(e => (long)e.Value);

    [Fact]
    public async Task CreateRide_CommittedCreateCountsOnce_ReplaysAndConflictsDoNot()
    {
        using var meter = new MeterRecorder();
        using var activities = new ActivityRecorder();
        var rider = Guid.NewGuid();

        using (var created = await _client.SendAsync(Post($"w12-create-{Guid.NewGuid():N}", rider)))
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var createEvents = meter.Flush();
        Assert.Equal(1, Total(createEvents, "trip.created"));

        var createSpan = activities.All("RideHailing.Trip/CreateRide")
            .Single(a => a.GetTagItem("ride.outcome")?.ToString() == "created");
        Assert.Contains(createSpan.TagObjects, t => t.Key == "trip.id");

        // Idempotent replay of the same key + body: no second creation. A
        // fresh Rider holds no active Trip, so both legs reach the handler.
        var replayRider = Guid.NewGuid();
        var key = $"w12-create-{Guid.NewGuid():N}";
        using (var first = await _client.SendAsync(Post(key, replayRider)))
            Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(1, Total(meter.Flush(), "trip.created"));
        using (var replay = await _client.SendAsync(Post(key, replayRider, RideBody())))
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        // The replay itself contributes no creation increment.
        Assert.Equal(0, Total(meter.Flush(), "trip.created"));

        // Same key + different semantics: explicit domain conflict, counted.
        using (var conflict = await _client.SendAsync(Post(key, replayRider, RideBody(10.5, 106.5))))
        {
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
            Assert.Equal("IDEMPOTENCY_CONFLICT", await ProblemCodeAsync(conflict));
        }

        using (var active = await _client.SendAsync(Post($"w12-create-{Guid.NewGuid():N}", replayRider)))
            Assert.Equal(HttpStatusCode.Conflict, active.StatusCode);
        var rejectionEvents = meter.Flush();
        Assert.Equal(1, Total(rejectionEvents, "trip.transition.rejected", "idempotency_conflict"));
        Assert.Equal(1, Total(rejectionEvents, "trip.transition.rejected", "active_trip_conflict"));
    }

    [Fact]
    public async Task CancelRide_CommittedCancelCountsOnce_TerminalReplayDoesNot()
    {
        using var meter = new MeterRecorder();
        var rider = Guid.NewGuid();
        var tripId = await CreateMatchingTripAsync(rider);

        using (var cancelled = await _client.SendAsync(Cancel(tripId, rider)))
            Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        Assert.Equal(1, Total(meter.Flush(), "trip.cancelled"));

        using (var replay = await _client.SendAsync(Cancel(tripId, rider)))
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(0, Total(meter.Flush(), "trip.cancelled"));
    }

    [Fact]
    public async Task CancelRide_DomainRejectionsCountByBoundedReason()
    {
        using var meter = new MeterRecorder();
        var rider = Guid.NewGuid();

        // CREATED cancellation is policy-unresolved: a domain rejection.
        var fresh = await CreateTripAsync(rider);
        using (var policy = await _client.SendAsync(Cancel(fresh, rider)))
        {
            Assert.Equal(HttpStatusCode.Conflict, policy.StatusCode);
            Assert.Equal("CANCELLATION_POLICY_UNRESOLVED", await ProblemCodeAsync(policy));
        }

        Assert.Equal(1, Total(meter.Flush(), "trip.transition.rejected", "cancellation_policy_unresolved"));

        // Terminal states reject with a bounded terminal_state reason (fresh
        // Rider: one active Trip each).
        var terminalRider = Guid.NewGuid();
        var terminal = await CreateMatchingTripAsync(terminalRider);
        await ExecuteAsync("""
            UPDATE trips SET state = 'COMPLETED', current_matching_round = 1,
                assigned_driver_id = @driver, reservation_id = @reservation, matching_round = 1
            WHERE trip_id = @p
            """, terminal);
        using (var rejected = await _client.SendAsync(Cancel(terminal, terminalRider)))
        {
            Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
            Assert.Equal("TERMINAL_STATE", await ProblemCodeAsync(rejected));
        }

        Assert.Equal(1, Total(meter.Flush(), "trip.transition.rejected", "terminal_state"));
    }

    [Fact]
    public async Task MessageHandler_NeedsReviewCountsOnce_RedeliveryDoesNotDoubleCount()
    {
        using var meter = new MeterRecorder();
        var rider = Guid.NewGuid();
        var tripId = await CreateMatchingTripAsync(rider);

        // A release that identifies no owned cleanup obligation is a stale
        // authority fact: parked NeedsReview, Trip unchanged, counted once.
        var envelope = MessageEnvelope<JsonElement>.Create("DriverReservationReleasedV1",
            Guid.NewGuid().ToString("N"),
            JsonSerializer.SerializeToElement(new
            {
                tripId,
                driverId = Guid.NewGuid(),
                reservationId = Guid.NewGuid(),
            }));

        await DeliverAsync("trip.driver-state.q", envelope);
        Assert.Equal(1, Total(meter.Flush(), "trip.transition.rejected", "stale_identity"));

        // Redelivery of the same messageId is a durable receipt replay: the
        // handler returns before the domain applies anything, so no counter.
        await DeliverAsync("trip.driver-state.q", envelope);
        Assert.Equal(0, Total(meter.Flush(), "trip.transition.rejected"));

        await using var db = new TripDbContext(Options());
        var trip = await db.Trips.AsNoTracking().SingleAsync(t => t.TripId == tripId);
        Assert.Equal(TripState.Matching, trip.State);
    }

    [Fact]
    public async Task SagaCompensation_CountedOncePerCommittedEntry()
    {
        using var meter = new MeterRecorder();
        var (tripId, riderId) = await SeedDriverAcceptedWithSagaAsync();

        await using (var db = new TripDbContext(Options()))
        {
            var result = await new CancelRideHandler(db, TimeProvider.System,
                NullLogger<CancelRideHandler>.Instance).HandleAsync(tripId, riderId);
            Assert.True(result.IsSuccess);
        }

        Assert.Equal(1, Total(meter.Flush(), "saga.compensation"));

        // The committed saga is already COMPENSATING and the Trip terminal:
        // the idempotent cancel replay cannot count a second entry.
        using (var replay = await _client.SendAsync(Cancel(tripId, riderId)))
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(0, Total(meter.Flush(), "saga.compensation"));
    }

    [Fact]
    public async Task SagaActiveGauge_ReflectsDurableRowsFromDatabase()
    {
        var (tripId, _) = await SeedDriverAcceptedWithSagaAsync();
        await using (var db = new TripDbContext(Options()))
        {
            var riderId = (await db.Trips.AsNoTracking().SingleAsync(t => t.TripId == tripId)).RiderId;
            await new CancelRideHandler(db, TimeProvider.System,
                NullLogger<CancelRideHandler>.Instance).HandleAsync(tripId, riderId);
        }

        // The gauge source of truth is the durable GROUP BY, the same query
        // the background observer runs; the snapshot is what a scrape reads.
        await using var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
        TripDiagnostics.ObserveSagaActive(await TripSagaBacklog.QueryAsync(dataSource));

        using var meter = new MeterRecorder();
        var gaugeEvents = meter.Flush().Where(e => e.Instrument == "saga.active").ToList();
        Assert.NotEmpty(gaugeEvents);

        // Durable truth per step equals the exported gauge values.
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            SELECT current_step, COUNT(*)::bigint
            FROM ride_sagas
            WHERE status IN ('ACTIVE', 'COMPENSATING', 'REQUIRES_REVIEW')
            GROUP BY current_step
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var durable = new Dictionary<string, long>();
        while (await reader.ReadAsync())
            durable[reader.GetString(0)] = reader.GetInt64(1);
        foreach (var (step, count) in durable)
            Assert.Equal(count,
                (long)gaugeEvents.Where(e => e.Tags.GetValueOrDefault("step") == step).Sum(e => e.Value));
    }

    [Fact]
    public async Task EmittedSignals_UseBoundedVocabularyAndSafeFieldsOnly()
    {
        using var meter = new MeterRecorder();
        using var activities = new ActivityRecorder();
        var rider = Guid.NewGuid();
        var tripId = await CreateMatchingTripAsync(rider);

        using (var cancelled = await _client.SendAsync(Cancel(tripId, rider)))
            Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        var events = meter.Flush();

        var allowedKeys = new HashSet<string>(StringComparer.Ordinal) { "reason", "step" };
        var allowedReasons = new HashSet<string>(StringComparer.Ordinal)
        {
            "active_trip_conflict", "idempotency_conflict", "invalid_transition", "terminal_state",
            "cancellation_policy_unresolved", "stale_identity", "status_conflict", "identity_required",
            "authority_rejected",
        };
        var steps = new HashSet<string>(StringComparer.Ordinal)
        {
            "AUTHORIZE_PENDING", "AUTHORIZED", "CAPTURE_PENDING", "CAPTURED", "RELEASE_PENDING", "VOID_PENDING",
        };

        foreach (var emitted in events.Where(e => e.Instrument.StartsWith("trip.", StringComparison.Ordinal) ||
                                                  e.Instrument.StartsWith("saga.", StringComparison.Ordinal)))
        {
            foreach (var key in emitted.Tags.Keys)
                Assert.Contains(key, allowedKeys);
            if (emitted.Tags.TryGetValue("reason", out var reason))
                Assert.Contains(reason, allowedReasons);
            if (emitted.Tags.TryGetValue("step", out var step))
                Assert.Contains(step, steps);
        }

        // Span tags: safe correlation fields only. Identity, credential,
        // payload and location fields must never appear on any Trip span.
        var forbidden = new[] { "rider", "token", "jwt", "secret", "password", "payload", "latitude",
            "longitude", "pickup", "dropoff" };
        foreach (var activity in activities.All("RideHailing.Trip/CancelRide")
                     .Concat(activities.All("RideHailing.Trip/CreateRide"))
                     .Concat(activities.All("RideHailing.Trip/ApplyAuthority")))
        {
            foreach (var tag in activity.TagObjects)
                Assert.False(forbidden.Any(f => tag.Key.Contains(f, StringComparison.OrdinalIgnoreCase)),
                    $"forbidden span tag {tag.Key}");
        }
    }

    private async Task<Guid> CreateTripAsync(Guid rider)
    {
        using var response = await _client.SendAsync(Post($"w12-seed-{Guid.NewGuid():N}", rider));
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("tripId").GetGuid();
    }

    private async Task<Guid> CreateMatchingTripAsync(Guid rider)
    {
        var tripId = await CreateTripAsync(rider);
        await using var db = new TripDbContext(Options());
        await new StartMatchingHandler(db, TimeProvider.System,
            NullLogger<StartMatchingHandler>.Instance).HandleAsync(tripId, rider);
        return tripId;
    }

    private async Task<(Guid TripId, Guid RiderId)> SeedDriverAcceptedWithSagaAsync()
    {
        var rider = Guid.NewGuid();
        var tripId = await CreateMatchingTripAsync(rider);
        var driver = Guid.NewGuid();
        var reservation = Guid.NewGuid();

        await using var db = new TripDbContext(Options());
        var trip = await db.Trips.SingleAsync(t => t.TripId == tripId);
        var reserved = trip.ApplyDriverReserved(driver, reservation, 1, TimeProvider.System.GetUtcNow());
        Assert.True(reserved.IsSuccess);
        await db.SaveChangesAsync();

        var accepted = await new RecordDriverAcceptedHandler(db, TimeProvider.System,
            NullLogger<RecordDriverAcceptedHandler>.Instance).HandleAsync(tripId, driver, reservation);
        Assert.True(accepted.IsSuccess);
        return (tripId, rider);
    }

    private async Task DeliverAsync(string queue, MessageEnvelope<JsonElement> envelope)
    {
        await using var db = new TripDbContext(Options());
        await new TripMessageHandler(db, TimeProvider.System,
            NullLogger<TripMessageHandler>.Instance).HandleAsync(queue, envelope, CancellationToken.None);
    }

    private async Task ExecuteAsync(string sql, Guid? tripId = null)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        if (tripId is not null)
            command.Parameters.AddWithValue("p", tripId.Value);
        command.Parameters.AddWithValue("driver", Guid.NewGuid());
        command.Parameters.AddWithValue("reservation", Guid.NewGuid());
        await command.ExecuteNonQueryAsync();
    }
}
